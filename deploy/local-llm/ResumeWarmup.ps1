#requires -Version 5.1
# Startup-only prefill. No candidate data, background generation loop or training.
function Read-LLMResumeWarmup([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -gt 16384) { throw 'Warm-up artifact exceeds its limit.' }
    $artifact = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($artifact.schemaVersion -ne 1 -or $artifact.promptVersion -notmatch '^[a-z0-9-]{1,64}$' -or
        @($artifact.PSObject.Properties.Name | Where-Object { $_ -notin @('schemaVersion','promptVersion','request') }).Count) {
        throw 'Invalid warm-up artifact.'
    }
    $request = $artifact.request
    if (@($request.PSObject.Properties.Name | Where-Object { $_ -notin @('model','messages','max_tokens','temperature','stream','response_format','chat_template_kwargs') }).Count -or
        $request.model -cne 'hrms-local' -or $request.max_tokens -ne 1 -or $request.temperature -ne 0 -or $request.stream -ne $false -or
        $request.response_format.type -cne 'json_object' -or $request.chat_template_kwargs.enable_thinking -ne $false -or
        @($request.messages).Count -ne 2 -or $request.messages[0].role -cne 'system' -or $request.messages[1].role -cne 'user' -or
        $request.messages[0].content -isnot [string] -or $request.messages[1].content -isnot [string] -or
        $request.messages[1].content.Trim() -cne 'UNTRUSTED RESUME:') {
        throw 'Warm-up must contain only the shared policy and an empty resume.'
    }
    return $artifact
}

function Test-LLMResumeSlotIdle($Client) {
    $reply = $null
    try {
        $reply = $Client.GetAsync('http://127.0.0.1:8091/slots').GetAwaiter().GetResult()
        if ([int]$reply.StatusCode -ne 200) { return $false }
        $slots = @($reply.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
        return $slots.Count -eq 1 -and $slots[0].is_processing -eq $false
    } catch { return $false }
    finally { if ($reply) { $reply.Dispose() } }
}

function Invoke-LLMResumeWarmup([string]$ArtifactPath, [scriptblock]$SafetyTick,
    [string]$GenerationLockPath = 'E:\LLM\logs\gateway-state.json') {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $nextSafetyTick = 5
    $result = [ordered]@{ Status='Unavailable'; PromptVersion=$null; Seconds=0; PromptTokens=0; Settled=$true }
    try { $artifact = Read-LLMResumeWarmup $ArtifactPath }
    catch { $result.Status='InvalidArtifact'; return [pscustomobject]$result }
    $result.PromptVersion = $artifact.promptVersion
    $client = New-LLMHttpClient 180
    $probe = New-LLMHttpClient 5
    $lock = $null; $content = $null; $reply = $null; $pending = $null
    try {
        # The supervisor keeps public admission CLOSED until this returns. Use
        # the existing gateway lock too; never overlap an accepted generation.
        try {
            $lock = [IO.File]::Open($GenerationLockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        } catch [IO.IOException] { $result.Status='DeferredBusy'; return [pscustomobject]$result }
        & $SafetyTick
        if (-not (Test-LLMResumeSlotIdle $probe)) { $result.Status='DeferredBusy'; return [pscustomobject]$result }
        $content = New-Object Net.Http.StringContent(($artifact.request | ConvertTo-Json -Depth 6 -Compress),[Text.Encoding]::UTF8,'application/json')
        $pending = $client.PostAsync('http://127.0.0.1:8091/v1/chat/completions',$content)
        # Continue watchdog/resource/website checks and heartbeats during prefill.
        while (-not $pending.IsCompleted) {
            Start-Sleep -Milliseconds 500
            if ($watch.Elapsed.TotalSeconds -ge $nextSafetyTick) { & $SafetyTick; $nextSafetyTick = $watch.Elapsed.TotalSeconds + 5 }
        }
        try {
            $reply = $pending.GetAwaiter().GetResult()
            if ([int]$reply.StatusCode -eq 200) {
                $answer = $reply.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                if ($answer.usage.prompt_tokens -gt 0) {
                    $result.Status='Completed'; $result.PromptTokens=[int]$answer.usage.prompt_tokens
                }
            }
        } catch { $result.Status='Unavailable' }
        # A canceled HTTP call alone cannot establish backend idleness. Keep
        # admission closed while it settles; failure is not an adapter verdict.
        if ($result.Status -ne 'Completed') {
            $settle = [Diagnostics.Stopwatch]::StartNew()
            while (-not (Test-LLMResumeSlotIdle $probe) -and $settle.Elapsed.TotalSeconds -lt 90) {
                & $SafetyTick; Start-Sleep -Seconds 5
            }
            $result.Settled = Test-LLMResumeSlotIdle $probe
        }
    } finally {
        if ($reply) { $reply.Dispose() }
        $client.Dispose(); $probe.Dispose()
        if ($content) { $content.Dispose() }
        if ($lock) { $lock.Dispose() }
        $result.Seconds=[math]::Round($watch.Elapsed.TotalSeconds,3)
    }
    # Deliberately discard the one-token response. It is neither a resume result
    # nor a verified example, and cannot enter receipts, RAG, usage or training.
    return [pscustomobject]$result
}
