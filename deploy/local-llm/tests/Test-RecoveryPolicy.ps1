$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\RecoveryPolicy.ps1')
function Expect($Actual, $Expected) { if ($Actual -ne $Expected) { throw "Expected $Expected, got $Actual" } }
Expect (Get-LLMRecoveryDecision 1 0 $true $true $true $false) 'ConfirmHealth'
Expect (Get-LLMRecoveryDecision 2 0 $true $true $true $false) 'ConfirmHealth'
Expect (Get-LLMRecoveryDecision 3 0 $true $true $true $false) 'Recover'
Expect (Get-LLMRecoveryDecision 3 3 $true $true $true $false) 'Backoff'
Expect (Get-LLMRecoveryDecision 3 0 $false $true $true $false) 'SafetyWait'
Expect (Get-LLMRecoveryDecision 3 0 $true $false $true $false) 'SafetyWait'
Expect (Get-LLMRecoveryDecision 3 0 $true $true $false $false) 'IdentityBlocked'
Expect (Get-LLMRecoveryDecision 3 0 $true $true $true $true) 'Maintenance'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('llm-recovery-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $runId = [Guid]::NewGuid().ToString('D')
    Write-LLMHistory $temporary 'Failure' 'BackendHealth' $runId @{ Attempt = 1; Prompt = 'must-not-persist'; ApiKey = 'must-not-persist' }
    Write-LLMHistory $temporary 'Ready' 'AuthenticatedHealth' $runId
    $failure = Get-Content (Join-Path $temporary 'last-failure.json') -Raw | ConvertFrom-Json
    Expect $failure.Code 'BackendHealth'
    $journal = Get-Content (Join-Path $temporary 'recovery-history.jsonl') -Raw
    if ($journal.Contains('must-not-persist')) { throw 'Unapproved history fields were persisted.' }
    for ($i = 0; $i -lt 8; $i++) { Write-LLMHistory $temporary 'Ready' 'AuthenticatedHealth' $runId @{} 100 }
    if (-not (Test-Path (Join-Path $temporary 'recovery-history.jsonl.3'))) { throw 'Journal rotation was not exercised.' }
    Write-LLMAtomicJson (Join-Path $temporary 'status.json') @{ Status = 'STARTING' }
    Write-LLMAtomicJson (Join-Path $temporary 'status.json') @{ Status = 'READY' }
    Expect ((Get-Content (Join-Path $temporary 'status.json') -Raw | ConvertFrom-Json).Status) 'READY'
    Expect (Get-LLMFailureCode 'PID identity mismatch; refusing unrelated process') 'IdentityBlocked'
    Expect (Get-LLMFailureCode 'Host CPU exceeded safety threshold') 'CpuSafety'
    Expect (Get-LLMFailureCode 'Production website routing regressed') 'WebsiteSafety'
    Write-Output 'PASS: health confirmation, safety blocking, bounded recovery, persistent failure history, journal rotation and atomic status.'
} finally {
    # The target is a verified GUID-named directory created by this test.
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('llm-recovery-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
