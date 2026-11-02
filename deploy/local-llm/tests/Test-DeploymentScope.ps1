$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Invoke-LLMDeployment.ps1') -LibraryOnly
foreach ($target in @('scripts\Host-LLM.ps1','scripts\RecoveryPolicy.ps1','scripts\Ensure-LLMRecovery.ps1','scripts\Invoke-LLMControl.ps1','api\control.php')) {
    if (-not (Test-LLMDeploymentTarget $target)) { throw 'Authorized LLM target rejected.' }
}
foreach ($target in @('..\wordpress\wp-config.php','C:\Apache24\conf\httpd.conf','scripts\..\..\other.ps1','api\gateway.php','Qwen3.5-4B-Q4_K_M.gguf','scripts\launch-arguments.json')) {
    if (Test-LLMDeploymentTarget $target) { throw 'Out-of-scope target accepted.' }
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('llm-package-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $files = foreach ($target in @('scripts\Host-LLM.ps1','scripts\RecoveryPolicy.ps1','scripts\Ensure-LLMRecovery.ps1','scripts\Invoke-LLMControl.ps1','api\control.php','scripts\Start-LLM.ps1','scripts\AdapterPolicy.ps1')) {
        $name = [IO.Path]::GetFileName($target); [IO.File]::WriteAllText((Join-Path $temporary $name), 'synthetic')
        @{ Source = $name; Target = $target; Sha256 = (Get-FileHash (Join-Path $temporary $name)).Hash }
    }
    $manifest = Join-Path $temporary 'manifest.json'
    [IO.File]::WriteAllText($manifest, (@{Version='reliability-v3';Files=@($files)} | ConvertTo-Json -Depth 5))
    $expected = (Get-FileHash $manifest).Hash
    [void](Assert-LLMPackage $temporary $expected)
    [IO.File]::WriteAllText((Join-Path $temporary 'Host-LLM.ps1'), 'changed after review')
    $refused = $false; try { [void](Assert-LLMPackage $temporary $expected) } catch { $refused = $true }
    if (-not $refused) { throw 'Changed artifact accepted.' }
    Write-Output 'PASS: LLM-only allow-list, path confinement and immutable manifest/artifact validation.'
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('llm-package-test-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
