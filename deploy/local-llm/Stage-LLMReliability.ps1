#requires -Version 5.1
[CmdletBinding()]
param([string]$CredentialProfilePath = 'D:\AllCreds\EESLServer.env', [switch]$Activate, [switch]$PrepareTraining, [switch]$ProbeTrainingNetwork)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$server = '10.10.91.100'
$profileValues = @{}
foreach ($line in Get-Content -LiteralPath $CredentialProfilePath) {
    if ($line -match '^\s*([^#=]+?)\s*=\s*(.*?)\s*$') { $profileValues[$Matches[1].Trim()] = $Matches[2].Trim().Trim('"').Trim("'") }
}
if ($profileValues['hundred'] -ne '10.10.91.100:5059' -or -not $profileValues['User'] -or -not $profileValues['password']) { throw 'Server identity/credential profile missing.' }
$credential = [PSCredential]::new($profileValues['User'], (ConvertTo-SecureString $profileValues['password'] -AsPlainText -Force))
$profileValues.Clear()
$drive = 'LlmStage' + $PID
New-PSDrive -Name $drive -PSProvider FileSystem -Root ('\\' + $server + '\E$') -Credential $credential | Out-Null
$remote = '\\' + $server + '\E$\'
$localRelease = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\.codex-tmp\llm-server-review-20261102\release\reliability-v3'))
[void][IO.Directory]::CreateDirectory($localRelease)
try {
    $base = $remote + 'LLM\'
    foreach ($required in @('scripts\Host-LLM.ps1','scripts\Start-LLM.ps1','api\control.php','secrets\control-api-key.txt')) {
        if (-not (Test-Path -LiteralPath ($base + $required))) { throw 'Pinned LLM installation was not found.' }
    }
    $owner = $base + 'maintenance\owner.json'
    if ((Test-Path -LiteralPath ($base + 'maintenance')) -and -not (Test-Path -LiteralPath $owner)) { throw 'Existing maintenance folder is not owned by this deployment.' }
    if (Test-Path -LiteralPath $owner) {
        if ((Get-Content -LiteralPath $owner -Raw | ConvertFrom-Json).Owner -cne 'FrevoLLMOperations-v1') { throw 'Maintenance owner mismatch.' }
    } else {
        [void](New-Item -ItemType Directory -Path ($base + 'maintenance'))
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $sourceAcl = Get-Acl -LiteralPath ($base + 'secrets\control-api-key.txt')
        foreach ($access in $sourceAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if ($access.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
            $rule = New-Object Security.AccessControl.FileSystemAccessRule($access.IdentityReference,
                [Security.AccessControl.FileSystemRights]::FullControl,
                ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),
                [Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)
            [void]$acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath ($base + 'maintenance') -AclObject $acl
        Set-Content -LiteralPath $owner -Value '{"Owner":"FrevoLLMOperations-v1","Scope":"EESL LLM only"}' -Encoding UTF8
    }
    [void](New-Item -ItemType Directory -Path ($base + 'maintenance\package') -Force)
    $files = foreach ($target in @('scripts\Host-LLM.ps1','scripts\RecoveryPolicy.ps1','scripts\Ensure-LLMRecovery.ps1','scripts\Invoke-LLMControl.ps1','api\control.php','scripts\Start-LLM.ps1','scripts\AdapterPolicy.ps1')) {
        $name = [IO.Path]::GetFileName($target)
        $source = Join-Path $PSScriptRoot $name
        Copy-Item -LiteralPath $source -Destination (Join-Path $localRelease $name)
        [ordered]@{ Source = $name; Target = $target; Sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
    }
    $manifest = [ordered]@{ Version = 'reliability-v3'; Scope = 'EESL LLM API only'; Files = @($files) }
    [IO.File]::WriteAllText((Join-Path $localRelease 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    $manifestHash = (Get-FileHash -LiteralPath (Join-Path $localRelease 'manifest.json') -Algorithm SHA256).Hash
    $installer = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-LLMDeployment.ps1') -Raw).Replace('__MANIFEST_SHA256__',$manifestHash)
    [IO.File]::WriteAllText((Join-Path $localRelease 'Invoke-LLMDeployment.ps1'),$installer,(New-Object Text.UTF8Encoding($false)))
    $installerHash = (Get-FileHash -LiteralPath (Join-Path $localRelease 'Invoke-LLMDeployment.ps1') -Algorithm SHA256).Hash
    $trainingSetupHash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Initialize-LLMTraining.ps1') -Algorithm SHA256).Hash
    $probeHash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Probe-LLMTrainingNetwork.ps1')).Hash
    $gateway = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'maintenance.php') -Raw).Replace('__INSTALLER_SHA256__',$installerHash).Replace('__TRAINING_SETUP_SHA256__',$trainingSetupHash).Replace('__NETWORK_PROBE_SHA256__',$probeHash)
    [IO.File]::WriteAllText((Join-Path $localRelease 'maintenance.php'),$gateway,(New-Object Text.UTF8Encoding($false)))
    foreach ($file in @($files.Source) + @('manifest.json')) {
        Copy-Item -LiteralPath (Join-Path $localRelease $file) -Destination ($base + 'maintenance\package\' + $file) -Force
        if ((Get-FileHash -LiteralPath (Join-Path $localRelease $file)).Hash -cne (Get-FileHash -LiteralPath ($base + 'maintenance\package\' + $file)).Hash) { throw 'Staged file verification failed.' }
    }
    Copy-Item -LiteralPath (Join-Path $localRelease 'Invoke-LLMDeployment.ps1') -Destination ($base + 'maintenance\Invoke-LLMDeployment.ps1') -Force
    foreach ($file in @('Initialize-LLMTraining.ps1','training-lock.json','download_training_base.py','train_verified.py','training_core.py','export_value_lora.py','Probe-LLMTrainingNetwork.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination ($base + 'maintenance\' + $file) -Force
        if ((Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $file)).Hash -cne (Get-FileHash -LiteralPath ($base + 'maintenance\' + $file)).Hash) { throw 'Training setup artifact mismatch.' }
    }
    Copy-Item -LiteralPath (Join-Path $localRelease 'maintenance.php') -Destination ($base + 'api\maintenance.php') -Force
    Set-Acl -LiteralPath ($base + 'api\maintenance.php') -AclObject (Get-Acl -LiteralPath ($base + 'api\control.php'))
    $proxy = $remote + 'Datacopy\wordpress\llm-maintenance.php'
    $proxyContent = "<?php`r`n// Standalone EESL LLM maintenance; no WordPress bootstrap/configuration.`r`nrequire 'E:/LLM/api/maintenance.php';`r`n"
    if ((Test-Path -LiteralPath $proxy) -and (Get-Content -LiteralPath $proxy -Raw) -cne $proxyContent) { throw 'An unrelated file already occupies the LLM maintenance route.' }
    Set-Content -LiteralPath $proxy -Value $proxyContent -Encoding Ascii -NoNewline
    Set-Acl -LiteralPath $proxy -AclObject (Get-Acl -LiteralPath ($remote + 'Datacopy\wordpress\llm-control.php'))
    $summary = [ordered]@{ Staged = $true; ManifestSha256 = $manifestHash; InstallerSha256 = $installerHash; Activated = $false }
    if ($Activate -or $PrepareTraining -or $ProbeTrainingNetwork) {
        # User-authorized fixed operation. Never print or place the secret in argv.
        $key = (Get-Content -LiteralPath ($base + 'secrets\control-api-key.txt') -Raw).Trim()
        if ($key -notmatch '^[a-f0-9]{64}$') { throw 'Invalid maintenance credential.' }
        $client = New-Object Net.Http.HttpClient
        try {
            $client.Timeout = [TimeSpan]::FromSeconds(25)
            $client.DefaultRequestHeaders.Authorization = New-Object Net.Http.Headers.AuthenticationHeaderValue('Bearer',$key)
            $request = @{ action = $(if ($ProbeTrainingNetwork) { 'probe_training_network' } elseif ($PrepareTraining) { 'prepare_training' } else { 'install' }); requestId = [Guid]::NewGuid().ToString('D') } | ConvertTo-Json -Compress
            $content = New-Object Net.Http.StringContent($request,[Text.Encoding]::UTF8,'application/json')
            $response = $client.PostAsync('https://eeslindia.org/llm-maintenance.php',$content).GetAwaiter().GetResult()
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            $summary['HttpStatus'] = [int]$response.StatusCode; $summary['Code'] = $body.code
            $summary.Activated = $response.IsSuccessStatusCode
        } finally { $key = $null; $client.Dispose() }
    }
    $summary | ConvertTo-Json -Compress
} finally { Remove-PSDrive -Name $drive -ErrorAction SilentlyContinue }
