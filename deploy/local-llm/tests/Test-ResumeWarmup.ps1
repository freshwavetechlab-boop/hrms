$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0
Add-Type -AssemblyName System.Net.Http
. (Join-Path $PSScriptRoot '..\ResumeWarmup.ps1')
Add-Type -ReferencedAssemblies System.Net.Http -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
public sealed class WarmupFakeClient : IDisposable {
    public bool Busy, Fail; public int Posts; public string Payload;
    public Task<HttpResponseMessage> GetAsync(string url) {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content=new StringContent("[{\"is_processing\":"+(Busy ? "true" : "false")+"}]") });
    }
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content) {
        if(url!="http://127.0.0.1:8091/v1/chat/completions") throw new Exception("Unexpected endpoint");
        Posts++; Payload=content.ReadAsStringAsync().GetAwaiter().GetResult();
        return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) {
            Content=new StringContent("{\"usage\":{\"prompt_tokens\":600},\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"{\"}}]}") });
    }
    public void Dispose() {}
}
'@
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('llm-warmup-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $artifactPath=Join-Path $PSScriptRoot '..\resume-warmup.json'
    $artifact=Read-LLMResumeWarmup $artifactPath
    $lockPath=Join-Path $temporary 'generation.lock'
    $script:fake=New-Object WarmupFakeClient
    function New-LLMHttpClient([int]$Timeout=90) { return $script:fake }
    $script:ticks=0
    $tick={ $script:ticks++ }
    $result=Invoke-LLMResumeWarmup $artifactPath $tick $lockPath
    if($result.Status -cne 'Completed' -or $result.PromptTokens -ne 600 -or $fake.Posts -ne 1 -or $ticks -lt 1 -or -not $result.Settled){throw 'Shared-prefix warm-up failed.'}
    if($fake.Payload -match 'ApiKey|Bearer|fullName.+synthetic'){throw 'Unexpected private warm-up data.'}
    $serialized=$result | ConvertTo-Json
    if($serialized -match 'choices|content|confidence|currentCompany'){throw 'Warm-up response escaped into status.'}
    $fake.Busy=$true
    if((Invoke-LLMResumeWarmup $artifactPath $tick $lockPath).Status -cne 'DeferredBusy' -or $fake.Posts -ne 1){throw 'Busy backend was overlapped.'}
    $fake.Busy=$false
    $occupied=[IO.File]::Open($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { if((Invoke-LLMResumeWarmup $artifactPath $tick $lockPath).Status -cne 'DeferredBusy' -or $fake.Posts -ne 1){throw 'Accepted gateway generation was overlapped.'} }
    finally { $occupied.Dispose() }
    $fake.Fail=$true
    if((Invoke-LLMResumeWarmup $artifactPath $tick $lockPath).Status -cne 'Unavailable'){throw 'Warm-up transport error was treated as success.'}
    $failed=$false
    try { Invoke-LLMResumeWarmup $artifactPath { throw 'CPU safety guard' } $lockPath | Out-Null } catch { $failed=$_.Exception.Message -eq 'CPU safety guard' }
    if(-not $failed -or $fake.Posts -ne 2){throw 'Watchdog safety exception was swallowed.'}
    $tampered=Join-Path $temporary 'candidate.json'
    $artifact.request.messages[1].content='UNTRUSTED RESUME: real candidate must never be used'
    [IO.File]::WriteAllText($tampered,($artifact | ConvertTo-Json -Depth 8))
    if((Invoke-LLMResumeWarmup $tampered $tick $lockPath).Status -cne 'InvalidArtifact' -or $fake.Posts -ne 2){throw 'Candidate source accepted for warm-up.'}
    'PASS: startup prefill, discarded output, nonblocking admission, busy/transport errors, safety propagation and candidate-free artifact.'
} finally {
    $resolved=[IO.Path]::GetFullPath($temporary)
    if($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('llm-warmup-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
