#requires -Version 5.1
# Library: approved adapters only. Base GGUF is never replaced by training.
function Get-LLMAdapterSelection([string]$TrainingRoot = 'E:\LLM\training') {
    $training = $TrainingRoot
    $pointerPath = $training+'\state\active-adapter.json'
    if (-not (Test-Path -LiteralPath $pointerPath)) { return $null }
    try {
        $pointer = Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json
        if ($pointer.schemaVersion -ne 1 -or $pointer.jobId -notmatch '^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$' -or
            $pointer.manifestSha256 -notmatch '^[a-f0-9]{64}$') { return $null }
        if (Test-Path -LiteralPath ($training+'\state\quarantined-'+$pointer.jobId+'.json')) { return $null }
        $directory = $training+'\adapters\'+$pointer.jobId
        $manifestPath = $directory+'\manifest.json'
        if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { return $null }
        if ((Get-FileHash -LiteralPath $manifestPath).Hash -ine $pointer.manifestSha256) { return $null }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.schemaVersion -ne 1 -or $manifest.jobId -cne $pointer.jobId -or $manifest.evaluationPassed -ne $true -or
            $manifest.completedSteps -lt 1 -or $manifest.holdoutCount -lt 4 -or
            $manifest.baseRevision -cne '851bf6e806efd8d0a36b00ddf55e13ccb7b8cd0a' -or
            $manifest.adapterSha256 -notmatch '^[a-f0-9]{64}$' -or $manifest.baseGgufSha256 -notmatch '^[a-f0-9]{64}$' -or
            @($manifest.subjects).Count -lt 1 -or @($manifest.subjects | Where-Object { $_ -notmatch '^[a-f0-9]{64}$' }).Count) { return $null }
        $revokedPath = $training+'\state\revocations.json'
        if (Test-Path -LiteralPath $revokedPath) {
            $revoked = Get-Content -LiteralPath $revokedPath -Raw | ConvertFrom-Json
            foreach ($subject in @($manifest.subjects)) {
                if ($revoked.PSObject.Properties.Name -contains $subject) { return $null }
            }
        }
        $adapter = $directory+'\adapter.gguf'
        if ((Get-Item -LiteralPath $adapter).Attributes -band [IO.FileAttributes]::ReparsePoint) { return $null }
        if ((Get-FileHash -LiteralPath $adapter).Hash -ine $manifest.adapterSha256) { return $null }
        return [pscustomobject]@{ JobId=$pointer.jobId; Path=$adapter; BaseSha256=$manifest.baseGgufSha256 }
    } catch { return $null }
}
function Wait-LLMGenerationDrain {
    $deadline = [DateTime]::UtcNow.AddSeconds(210)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $handle = [IO.File]::Open('E:\LLM\logs\gateway-state.json',[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            $handle.Dispose(); return $true
        } catch [IO.IOException] { Start-Sleep -Seconds 1 }
    }
    return $false
}
function Set-LLMAdapterQuarantine([string]$JobId) {
    if ($JobId -notmatch '^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$') { return }
    Write-LLMAtomicJson ('E:\LLM\training\state\quarantined-'+$JobId+'.json') @{
        JobId=$JobId; Utc=[DateTime]::UtcNow.ToString('o'); Code='RuntimeReadinessFailed'; Fallback='OriginalBase' }
}
