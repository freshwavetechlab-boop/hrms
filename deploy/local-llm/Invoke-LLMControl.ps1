#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Status','Start')][string]$Action = 'Status', [switch]$LibraryOnly)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Invoke-FixedLLMControl([string]$Operation) {
    if ($Operation -notin @('Status','Start')) { throw 'Unsupported operation.' }
    . (Join-Path $PSScriptRoot 'Start-LLM.ps1') -LibraryOnly
    Assert-LLMTarget
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $task = $scheduler.GetFolder('\').GetTask('EESL-LLM-LocalAI')
    $actions = $task.Definition.Actions
    # Refuse a repurposed task. No task creation, enable, account/permission edits or service restart.
    if ($actions.Count -ne 1 -or $actions.Item(1).Path -ine 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -or
        $actions.Item(1).Arguments -cne '-NoProfile -NonInteractive -File E:\LLM\scripts\Host-LLM.ps1' -or
        $task.Definition.Principal.UserId -notin @('NT AUTHORITY\LOCAL SERVICE','S-1-5-19','LOCAL SERVICE')) {
        throw 'Task contract mismatch.'
    }
    if (-not $task.Enabled) { return 'task_disabled' }
    if ([int]$task.State -eq 2) { return 'starting' }
    if ([int]$task.State -eq 4) {
        try {
            $hostReport = Get-Content -LiteralPath 'E:\LLM\logs\host-status.json' -Raw | ConvertFrom-Json
            if ($hostReport.SchemaVersion -eq 2 -and ([DateTime]::UtcNow - [DateTime]::Parse($hostReport.HeartbeatUtc).ToUniversalTime()).TotalSeconds -lt 60) {
                if ($hostReport.Status -eq 'IDENTITY_BLOCKED') { return 'identity_blocked' }
                if ($hostReport.Status -eq 'WAITING_FOR_SAFE_RESOURCES') { return 'safety_wait' }
                if ($hostReport.Status -in @('RECOVERING','RECOVERY_BACKOFF','CONFIRMING_HEALTH')) { return 'recovering' }
            }
        } catch { }
        if (Test-Path -LiteralPath 'E:\LLM\api\enabled') {
            $admission = [IO.File]::ReadAllText('E:\LLM\api\enabled').Trim()
            if ($admission -eq 'enabled') {
                $health = New-LLMHttpClient 2
                try {
                    $reply = $health.GetAsync('http://127.0.0.1:8091/health').GetAwaiter().GetResult()
                    try { if ([int]$reply.StatusCode -eq 200) { return 'running' } } finally { $reply.Dispose() }
                } catch { } finally { $health.Dispose() }
            }
        }
        return 'starting'
    }
    if ([int]$task.State -ne 3) { throw 'Task state unknown.' }
    if ($Operation -eq 'Status') { return 'stopped' }
    $os = Get-CimInstance Win32_OperatingSystem
    if ($os.FreePhysicalMemory / 1MB -lt 16) { return 'memory_low' }
    $cpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
    if ($null -eq $cpu) { throw 'CPU measurement unavailable.' }
    if ([int]$cpu.PercentProcessorTime -gt 85) { return 'cpu_busy' }
    try {
        $pages = @(Get-LLMWebsiteHealth)
        if ($pages.Count -ne 2 -or @($pages | Where-Object Status -ne 301).Count) { return 'website_unhealthy' }
        foreach ($page in $pages) {
            if ($page.Redirect -cne ('https://eeslindia.org/' + $page.Language + '/')) { return 'website_unhealthy' }
        }
    } catch { return 'website_unhealthy' }
    # Existing Host-LLM handles process ownership, model readiness, admission and watchdog safety.
    $null = $task.Run($null)
    return 'starting'
}

if (-not $LibraryOnly) {
    try { $code = Invoke-FixedLLMControl $Action } catch { $code = 'control_unavailable' }
    [Console]::Out.WriteLine((@{code=$code} | ConvertTo-Json -Compress))
}
