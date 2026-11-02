#requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Start-LLM.ps1') -LibraryOnly
. (Join-Path $PSScriptRoot 'RecoveryPolicy.ps1')
Assert-LLMTarget
$runId = [Guid]::NewGuid().ToString('D')
$logs = 'E:\LLM\logs'
if (Test-Path -LiteralPath 'E:\LLM\api\maintenance-request.json') { exit 0 }
try {
    $scheduler = New-Object -ComObject 'Schedule.Service'; $scheduler.Connect()
    $task = $scheduler.GetFolder('\').GetTask('EESL-LLM-LocalAI')
    $action = $task.Definition.Actions
    if ($action.Count -ne 1 -or $action.Item(1).Path -ine 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -or
        $action.Item(1).Arguments -cne '-NoProfile -NonInteractive -File E:\LLM\scripts\Host-LLM.ps1' -or
        $task.Definition.Principal.UserId -notin @('NT AUTHORITY\LOCAL SERVICE','S-1-5-19','LOCAL SERVICE')) { throw 'Task identity mismatch.' }
    if (-not $task.Enabled -or [int]$task.State -in @(2,4)) { exit 0 }
    $statePath = Join-Path $logs 'recovery-monitor-state.json'
    $state = @{ WindowUtc = [DateTime]::UtcNow.ToString('o'); Attempts = 0 }
    if (Test-Path -LiteralPath $statePath) {
        $saved = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if (([DateTime]::UtcNow - [DateTime]::Parse($saved.WindowUtc).ToUniversalTime()).TotalMinutes -lt 15) {
            $state.WindowUtc = $saved.WindowUtc; $state.Attempts = [int]$saved.Attempts
        }
    }
    if ($state.Attempts -ge 3) { exit 0 }
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
    $pages = @(Get-LLMWebsiteHealth)
    $safe = $os.FreePhysicalMemory / 1MB -ge 16 -and $null -ne $cpu -and $cpu.PercentProcessorTime -le 85
    $webSafe = $pages.Count -eq 2 -and @($pages | Where-Object Status -ne 301).Count -eq 0
    foreach ($page in $pages) { if ($page.Redirect -cne ('https://eeslindia.org/' + $page.Language + '/')) { $webSafe = $false } }
    if ((Get-LLMRecoveryDecision 3 $state.Attempts $safe $webSafe $true $false) -ne 'Recover') { exit 0 }
    $state.Attempts++
    Write-LLMAtomicJson $statePath $state
    Write-LLMHistory $logs 'SupervisorRecovery' 'TaskRestart' $runId @{ Attempt = $state.Attempts }
    [void]$task.Run($null)
} catch {
    try { Write-LLMHistory $logs 'Failure' 'SupervisorRecoveryBlocked' $runId } catch { }
    exit 1
}
