#requires -Version 5.1
[CmdletBinding()]
param([string]$PrivateClientKeyPath = 'D:\AllCreds\EESL-LLM-learning-100.key')
$ErrorActionPreference = 'Stop'
$base = '\\10.10.91.100\E$\LLM\'
if ((Get-Content -LiteralPath ($base + 'training\owner.json') -Raw | ConvertFrom-Json).Owner -cne 'FrevoLLMTraining-v1') { throw 'Training storage identity mismatch.' }
$source = Join-Path $PSScriptRoot 'learning.php'
Copy-Item -LiteralPath $source -Destination ($base + 'api\learning.php') -Force
Set-Acl -LiteralPath ($base + 'api\learning.php') -AclObject (Get-Acl -LiteralPath ($base + 'api\control.php'))
if ((Get-FileHash -LiteralPath $source).Hash -cne (Get-FileHash -LiteralPath ($base + 'api\learning.php')).Hash) { throw 'Learning gateway hash mismatch.' }
$proxy = '\\10.10.91.100\E$\Datacopy\wordpress\llm-learning.php'
$content = "<?php`r`n// Standalone EESL LLM learning gateway; no WordPress bootstrap/configuration.`r`nrequire 'E:/LLM/api/learning.php';`r`n"
if ((Test-Path -LiteralPath $proxy) -and (Get-Content -LiteralPath $proxy -Raw) -cne $content) { throw 'Learning route is occupied by another file.' }
Set-Content -LiteralPath $proxy -Value $content -Encoding Ascii -NoNewline
Set-Acl -LiteralPath $proxy -AclObject (Get-Acl -LiteralPath '\\10.10.91.100\E$\Datacopy\wordpress\llm-api.php')
$key = (Get-Content -LiteralPath ($base + 'secrets\learning-api-key.txt') -Raw).Trim()
if ($key -notmatch '^[a-f0-9]{64}$') { throw 'Learning credential unavailable.' }
if ((Test-Path -LiteralPath $PrivateClientKeyPath) -and (Get-Content -LiteralPath $PrivateClientKeyPath -Raw).Trim() -cne $key) { throw 'Refusing to overwrite another local credential.' }
[IO.File]::WriteAllText($PrivateClientKeyPath,$key,(New-Object Text.UTF8Encoding($false)))
$acl = Get-Acl -LiteralPath $PrivateClientKeyPath
$acl.SetAccessRuleProtection($true,$false)
foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRuleAll($rule) }
foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18','S-1-5-32-544')) {
    [void]$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),
        [Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.AccessControlType]::Allow)))
}
$expectedSids = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18','S-1-5-32-544')
$existingAcl = Get-Acl -LiteralPath $PrivateClientKeyPath
$safe = $existingAcl.AreAccessRulesProtected
foreach ($entry in $existingAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
    if ($entry.IdentityReference.Value -notin $expectedSids -or $entry.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { $safe = $false }
}
if (-not $safe) {
    # Change the DACL only; Set-Acl on some PS5 domain tokens also attempts a
    # SACL/owner write that requires unrelated SeSecurityPrivilege.
    & "$env:SystemRoot\System32\icacls.exe" $PrivateClientKeyPath '/inheritance:r' '/grant:r' `
        ('*'+$expectedSids[0]+':(F)') '*S-1-5-18:(F)' '*S-1-5-32-544:(F)' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect learning credential DACL.' }
    foreach ($entry in $existingAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($entry.IdentityReference.Value -notin $expectedSids) {
            & "$env:SystemRoot\System32\icacls.exe" $PrivateClientKeyPath '/remove' ('*'+$entry.IdentityReference.Value) | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not remove unrelated credential access.' }
        }
    }
}
$key = $null
[ordered]@{ Endpoint = 'https://eeslindia.org/llm-learning.php'; GatewaySha256 = (Get-FileHash -LiteralPath $source).Hash;
    ClientCredentialFile = $PrivateClientKeyPath; CredentialPrinted = $false; StorageEncrypted = 'AES-256-GCM'; WebsiteConfigurationChanged = $false } | ConvertTo-Json -Compress
