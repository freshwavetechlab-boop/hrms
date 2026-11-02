$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\AdapterPolicy.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('llm-adapter-test-'+[Guid]::NewGuid().ToString('N'))
$id = [Guid]::NewGuid().ToString('D')
$directory = Join-Path $root ('adapters\'+$id)
[void][IO.Directory]::CreateDirectory($directory)
[void][IO.Directory]::CreateDirectory((Join-Path $root 'state'))
try {
    [IO.File]::WriteAllText((Join-Path $directory 'adapter.gguf'),'synthetic adapter')
    $manifest = @{schemaVersion=1;jobId=$id;evaluationPassed=$true;completedSteps=8;holdoutCount=4;
        baseRevision='851bf6e806efd8d0a36b00ddf55e13ccb7b8cd0a';adapterSha256=(Get-FileHash (Join-Path $directory 'adapter.gguf')).Hash.ToLowerInvariant();
        baseGgufSha256=('b'*64);subjects=@('a'*64)}
    $manifestFile = Join-Path $directory 'manifest.json'
    [IO.File]::WriteAllText($manifestFile,($manifest | ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllText((Join-Path $root 'state\active-adapter.json'),(@{schemaVersion=1;jobId=$id;manifestSha256=(Get-FileHash $manifestFile).Hash.ToLowerInvariant()} | ConvertTo-Json))
    if ((Get-LLMAdapterSelection $root).JobId -cne $id) { throw 'Verified adapter was rejected.' }
    [IO.File]::WriteAllText((Join-Path $root 'state\revocations.json'),('{"'+('a'*64)+'":"revoked"}'))
    if ($null -ne (Get-LLMAdapterSelection $root)) { throw 'Revoked subject adapter accepted.' }
    [IO.File]::WriteAllText((Join-Path $root 'state\revocations.json'),'{}')
    [IO.File]::WriteAllText((Join-Path $directory 'adapter.gguf'),'tampered')
    if ($null -ne (Get-LLMAdapterSelection $root)) { throw 'Tampered adapter accepted.' }
    [IO.File]::WriteAllText((Join-Path $root 'state\active-adapter.json'),'{"schemaVersion":1,"jobId":"../../outside"}')
    if ($null -ne (Get-LLMAdapterSelection $root)) { throw 'Adapter path escape accepted.' }
    'PASS: approved identity/hash, revocation, tamper and path confinement gates.'
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('llm-adapter-test-')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
