#requires -Version 5.1
Set-StrictMode -Version 2.0

function Get-LLMRecoveryDecision {
    param([int]$ConsecutiveFailures, [int]$RecentRestarts, [bool]$ResourcesSafe,
        [bool]$WebsiteSafe, [bool]$IdentityVerified, [bool]$Maintenance,
        [int]$FailureLimit = 3, [int]$RestartLimit = 3)
    if ($Maintenance) { return 'Maintenance' }
    if (-not $IdentityVerified) { return 'IdentityBlocked' }
    if (-not $ResourcesSafe -or -not $WebsiteSafe) { return 'SafetyWait' }
    if ($RecentRestarts -ge $RestartLimit) { return 'Backoff' }
    if ($ConsecutiveFailures -lt $FailureLimit) { return 'ConfirmHealth' }
    return 'Recover'
}

function Write-LLMAtomicJson {
    param([string]$Path, $Value)
    $temporary = $Path + '.' + $PID + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $backup = $temporary + '.previous'
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 9), (New-Object Text.UTF8Encoding($false)))
        for ($attempt = 0; $attempt -lt 4; $attempt++) {
            try {
                if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, $backup) }
                else { [IO.File]::Move($temporary, $Path) }
                return
            } catch {
                if ($attempt -eq 3) { throw }
                Start-Sleep -Milliseconds 75
            }
        }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
    }
}

function Write-LLMHistory {
    param([string]$LogDirectory, [string]$Event, [string]$Code, [string]$RunId,
        [hashtable]$Metrics = @{}, [long]$MaximumBytes = 5242880)
    # No prompt, response, credential, arbitrary exception or caller path enters the journal.
    if ($Event -notmatch '^[A-Za-z][A-Za-z0-9_.-]{0,47}$' -or $Code -notmatch '^[A-Za-z][A-Za-z0-9_.-]{0,63}$') { throw 'Invalid history code.' }
    $safe = [ordered]@{}
    foreach ($key in @('Attempt','ConsecutiveFailures','HostCpuPercent','FreeMemoryGB','ProcessWorkingSetGB','DelaySeconds','Seconds','ExitCode')) {
        if ($Metrics.ContainsKey($key) -and $Metrics[$key] -is [ValueType]) { $safe[$key] = $Metrics[$key] }
    }
    $record = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); RunId = $RunId; Event = $Event; Code = $Code; Metrics = $safe }
    $path = Join-Path $LogDirectory 'recovery-history.jsonl'
    $line = ($record | ConvertTo-Json -Depth 5 -Compress) + "`n"
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $stream = $null
        try {
            $stream = [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
            if ($stream.Length -gt $MaximumBytes) {
                $stream.Dispose(); $stream = $null
                if ([IO.File]::Exists($path + '.3')) { [IO.File]::Delete($path + '.3') }
                for ($index = 2; $index -ge 1; $index--) {
                    if ([IO.File]::Exists($path + '.' + $index)) { [IO.File]::Move($path + '.' + $index, $path + '.' + ($index + 1)) }
                }
                [IO.File]::Move($path, $path + '.1')
                $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            }
            $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($line)
            [void]$stream.Seek(0, [IO.SeekOrigin]::End)
            $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()
            if ($Event -eq 'Failure') { Write-LLMAtomicJson (Join-Path $LogDirectory 'last-failure.json') $record }
            return
        } catch {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Milliseconds 75
        } finally { if ($stream) { $stream.Dispose() } }
    }
}

function Get-LLMFailureCode {
    param([string]$Message)
    if ($Message -match 'identity mismatch|unrelated|Unexpected target|Unexpected operating|only on DCDB1') { return 'IdentityBlocked' }
    if ($Message -match 'memory|Insufficient free') { return 'MemorySafety' }
    if ($Message -match 'CPU') { return 'CpuSafety' }
    if ($Message -match 'website|Website|Apache|PHP|routing|Log growth') { return 'WebsiteSafety' }
    if ($Message -match 'exited|process remained|process is already') { return 'ProcessExited' }
    return 'BackendHealth'
}
