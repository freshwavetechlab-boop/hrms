#requires -Version 5.1
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Start-LLM.ps1') -LibraryOnly
. (Join-Path $PSScriptRoot 'RecoveryPolicy.ps1')
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'AdapterPolicy.ps1')) { . (Join-Path $PSScriptRoot 'AdapterPolicy.ps1') }
. (Join-Path $PSScriptRoot 'ResumeWarmup.ps1')
Assert-LLMTarget
$logDirectory = 'E:\LLM\logs'
$admission = 'E:\LLM\api\enabled'
$maintenance = 'E:\LLM\api\maintenance-request.json'
$runId = [Guid]::NewGuid().ToString('D')
$hostState = $null
$selectedAdapter = $null
$restarts = New-Object 'Collections.Generic.List[DateTime]'
$report = [ordered]@{ SchemaVersion = 2; StartedUtc = [DateTime]::UtcNow.ToString('o'); RunId = $runId;
    Status = 'STARTING'; HostPid = $PID; HostStartUtc = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o');
    LastResources = $null; LastLocalWebRouting = $null; ModelLoadSeconds = $null; ConsecutiveHealthFailures = 0;
    Failure = $null; LastFailureCode = $null; NextRecoveryUtc = $null; RecoveryCount = 0; DiagnosticsDegraded = $false;
    ResumeWarmup = $null }

function Save-HostReport {
    $report['HeartbeatUtc'] = [DateTime]::UtcNow.ToString('o')
    try { Write-LLMAtomicJson (Join-Path $logDirectory 'host-status.json') $report }
    catch { $report.DiagnosticsDegraded = $true }
}
function Record-HostEvent([string]$Event, [string]$Code, [hashtable]$Metrics = @{}) {
    try { Write-LLMHistory $logDirectory $Event $Code $runId $Metrics }
    catch { $report.DiagnosticsDegraded = $true }
}
function Set-Admission([bool]$Enabled) {
    if ($Enabled -and (Test-Path -LiteralPath 'E:\LLM\api\drain-request.json')) { $Enabled = $false }
    [IO.File]::WriteAllText($admission, $(if ($Enabled) { 'enabled' } else { 'disabled' }), $script:LlmEncoding)
}
function Wait-SafeRecovery([int]$Seconds) {
    $report.NextRecoveryUtc = [DateTime]::UtcNow.AddSeconds($Seconds).ToString('o')
    for ($step = 0; $step -lt $Seconds; $step += 5) { Save-HostReport; Start-Sleep -Seconds ([math]::Min(5, $Seconds - $step)) }
}
function Assert-SafeStart {
    Assert-LLMTarget
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
    if ($os.FreePhysicalMemory / 1MB -lt 16) { throw 'Insufficient free memory.' }
    if ($null -eq $cpu -or $cpu.PercentProcessorTime -gt 85) { throw 'CPU safety gate is not ready.' }
    $pages = @(Get-LLMWebsiteHealth)
    if ($pages.Count -ne 2 -or @($pages | Where-Object Status -ne 301).Count) { throw 'Website safety gate failed.' }
    foreach ($page in $pages) { if ($page.Redirect -cne ('https://eeslindia.org/' + $page.Language + '/')) { throw 'Website routing safety gate failed.' } }
    return $pages
}

Record-HostEvent 'HostStarted' 'SupervisorV2'
Set-Admission $false
try {
    while (-not (Test-Path -LiteralPath $maintenance)) {
        $code = 'BackendHealth'
        try {
            $now = [DateTime]::UtcNow
            for ($i = $restarts.Count - 1; $i -ge 0; $i--) { if (($now - $restarts[$i]).TotalSeconds -gt 900) { $restarts.RemoveAt($i) } }
            if ((Get-LLMRecoveryDecision 3 $restarts.Count $true $true $true $false) -eq 'Backoff') {
                $report.Status = 'RECOVERY_BACKOFF'
                Record-HostEvent 'RecoveryDeferred' 'RestartRateLimit' @{ DelaySeconds = 300 }
                Wait-SafeRecovery 300
                continue
            }
            $baseline = @(Assert-SafeStart)
            # A crashed supervisor can leave its own child alive. Reclaim only the
            # pinned PID/start-time/path identities, never an unrelated listener.
            if (Test-Path -LiteralPath $script:LlmStatePath) {
                $previous = Get-Content -LiteralPath $script:LlmStatePath -Raw | ConvertFrom-Json
                if (@(Get-LLMOwnedProcesses $previous).Count) { [void](Stop-LLMOwned $previous) }
            }
            $offsets = @(Get-LLMLogOffsets)
            $report.Status = 'STARTING'; $report.NextRecoveryUtc = $null; Save-HostReport
            $selectedAdapter = $null
            if (Get-Command Get-LLMAdapterSelection -ErrorAction SilentlyContinue) { $selectedAdapter = Get-LLMAdapterSelection }
            $hostState = & (Join-Path $PSScriptRoot 'Start-LLM.ps1')
            $restarts.Add([DateTime]::UtcNow)
            $report.ModelLoadSeconds = Wait-LLMReady $hostState
            $report.Status = 'WARMING'; $report.ResumeWarmup = $null; Save-HostReport
            $warmupLastWebCheck = [DateTime]::MinValue
            $report.ResumeWarmup = Invoke-LLMResumeWarmup (Join-Path $PSScriptRoot 'resume-warmup.json') {
                $report.LastResources = Get-LLMResources $hostState
                if (([DateTime]::UtcNow - $warmupLastWebCheck).TotalSeconds -ge 60) {
                    $report.LastLocalWebRouting = @(Assert-LLMWebsiteHealth $baseline)
                    $script:warmupLastWebCheck = [DateTime]::UtcNow
                }
                Save-HostReport
            }
            Record-HostEvent 'ResumeWarmup' $report.ResumeWarmup.Status
            if (-not $report.ResumeWarmup.Settled) { throw 'Resume warm-up backend did not settle.' }
            $report.Status = 'READY'; $report.Failure = $null; $report.ConsecutiveHealthFailures = 0
            $report['ActiveAdapter'] = $(if ($selectedAdapter) { $selectedAdapter.JobId } else { $null })
            Set-Admission $true
            Record-HostEvent 'Ready' 'AuthenticatedHealth'
            $lastHealth = [DateTime]::MinValue
            $healthClient = New-LLMHttpClient 5
            try {
                while (-not (Test-Path -LiteralPath $maintenance)) {
                    if (Get-Command Get-LLMAdapterSelection -ErrorAction SilentlyContinue) {
                        $desired = Get-LLMAdapterSelection
                        $oldId = $(if ($selectedAdapter) { $selectedAdapter.JobId } else { '' })
                        $newId = $(if ($desired) { $desired.JobId } else { '' })
                        if ($oldId -cne $newId) {
                            Set-Admission $false
                            $report.Status = 'ADAPTER_DRAIN'; Save-HostReport
                            if (Wait-LLMGenerationDrain) {
                                Record-HostEvent 'AdapterActivation' 'ApprovedSelectionChanged'
                                break
                            }
                            Record-HostEvent 'AdapterDeferred' 'GenerationBusy'; Set-Admission $true
                        }
                    }
                    $report.LastResources = Get-LLMResources $hostState
                    $checkDue = ([DateTime]::UtcNow - $lastHealth).TotalSeconds -ge 60 -or $report.ConsecutiveHealthFailures -gt 0
                    if ($checkDue) {
                        # Production resource and website guards retain immediate
                        # stop authority; only transient model health is confirmed.
                        $report.LastLocalWebRouting = @(Assert-LLMWebsiteHealth $baseline)
                        $report['NewLogMarkers'] = @(Test-LLMNewErrors $offsets)
                        $healthy = $false
                        try {
                            $reply = $healthClient.GetAsync('http://127.0.0.1:8091/health').GetAwaiter().GetResult()
                            try { $healthy = [int]$reply.StatusCode -eq 200 } finally { $reply.Dispose() }
                        } catch { $healthy = $false }
                        $lastHealth = [DateTime]::UtcNow
                        if (-not $healthy) {
                            Set-Admission $false
                            $report.ConsecutiveHealthFailures++
                            $report.Status = 'CONFIRMING_HEALTH'
                            Record-HostEvent 'HealthSampleFailed' 'BackendHealth' @{ ConsecutiveFailures = $report.ConsecutiveHealthFailures }
                            if ((Get-LLMRecoveryDecision $report.ConsecutiveHealthFailures 0 $true $true $true $false) -eq 'Recover') { throw 'Persistent backend health failure after three samples.' }
                        } else {
                            if ($report.ConsecutiveHealthFailures -gt 0) { Record-HostEvent 'HealthRecovered' 'NoRestartNeeded' }
                            $report.ConsecutiveHealthFailures = 0; $report.Status = 'READY'; Set-Admission $true
                        }
                    }
                    Save-HostReport
                    Start-Sleep -Seconds 5
                }
            } finally { $healthClient.Dispose() }
        } catch {
            $code = Get-LLMFailureCode $_.Exception.Message
            if ($_.Exception.Message -ceq 'Resume warm-up backend did not settle.') { $code = 'WarmupUnavailable' }
            $report.LastFailureCode = $code; $report.Failure = $code; $report.Status = 'RECOVERING'
            Record-HostEvent 'Failure' $code @{ Attempt = $restarts.Count; ConsecutiveFailures = $report.ConsecutiveHealthFailures }
            if ($selectedAdapter -and $code -notin @('MemorySafety','CpuSafety','WebsiteSafety','IdentityBlocked','WarmupUnavailable')) {
                Set-LLMAdapterQuarantine $selectedAdapter.JobId
                Record-HostEvent 'AdapterRollback' 'ReadinessFailedOriginalBaseRestored'
            }
        } finally {
            Set-Admission $false
            if ($hostState) {
                try { [void](Stop-LLMOwned $hostState); $hostState = $null }
                catch { $code = 'IdentityBlocked'; $report.LastFailureCode = $code; $report.Failure = $code; Record-HostEvent 'Failure' $code }
            }
            Save-HostReport
        }
        if (Test-Path -LiteralPath $maintenance) { break }
        if ($code -eq 'IdentityBlocked') {
            $report.Status = 'IDENTITY_BLOCKED'
            # No automatic operation on an identity mismatch; an operator must
            # repair the pinned state. The supervisor continues reporting health.
            while (-not (Test-Path -LiteralPath $maintenance)) { Wait-SafeRecovery 300 }
            break
        }
        $report.RecoveryCount++
        $report.Status = $(if ($code -in @('MemorySafety','CpuSafety','WebsiteSafety')) { 'WAITING_FOR_SAFE_RESOURCES' } else { 'RECOVERING' })
        Record-HostEvent 'RecoveryScheduled' $code @{ DelaySeconds = 30 }
        Wait-SafeRecovery 30
    }
} finally {
    Set-Admission $false
    if ($hostState) { try { [void](Stop-LLMOwned $hostState) } catch { Record-HostEvent 'Failure' 'CleanupFailed' } }
    $report.Status = 'MAINTENANCE'; $report['FinishedUtc'] = [DateTime]::UtcNow.ToString('o')
    Record-HostEvent 'HostStopped' 'Maintenance'; Save-HostReport
}
