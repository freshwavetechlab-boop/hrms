#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Dispatch','Run','Status')][string]$Action = 'Status', [switch]$LibraryOnly)
$ErrorActionPreference = 'Stop'
$root = 'E:\LLM'
$maintenanceRoot = [IO.Path]::Combine($root, 'maintenance')
$statusPath = [IO.Path]::Combine($root, 'logs\deployment-status.json')
$packageRoot = [IO.Path]::Combine($maintenanceRoot, 'package')
$manifestPath = [IO.Path]::Combine($packageRoot, 'manifest.json')
$expectedManifestHash = '__MANIFEST_SHA256__'

function Test-LLMDeploymentTarget([string]$Target) {
    return $Target -in @('scripts\Host-LLM.ps1','scripts\RecoveryPolicy.ps1','scripts\Ensure-LLMRecovery.ps1',
        'scripts\Invoke-LLMControl.ps1','api\control.php','scripts\Start-LLM.ps1','scripts\AdapterPolicy.ps1',
        'scripts\ResumeWarmup.ps1','scripts\resume-warmup.json')
}
function Assert-LLMPackage([string]$Package, [string]$ExpectedHash) {
    $manifestFile = Join-Path $Package 'manifest.json'
    if ((Get-FileHash -LiteralPath $manifestFile -Algorithm SHA256).Hash -cne $ExpectedHash) { throw 'Package manifest hash mismatch.' }
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($manifest.Version -ne 'reliability-v4' -or @($manifest.Files).Count -ne 9) { throw 'Unsupported package.' }
    $seen = @{}
    foreach ($file in $manifest.Files) {
        if (-not (Test-LLMDeploymentTarget $file.Target) -or $file.Source -cne [IO.Path]::GetFileName($file.Target) -or
            $file.Sha256 -notmatch '^[A-F0-9]{64}$' -or $seen.ContainsKey($file.Target)) { throw 'Package scope invalid.' }
        $seen[$file.Target] = $true
        $path = Join-Path $Package $file.Source
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package link refused.' }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $file.Sha256) { throw 'Package file hash mismatch.' }
    }
    return $manifest
}
function Set-DeploymentStatus([string]$State, [string]$Code, [hashtable]$Extra = @{}) {
    $value = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); Version = 'reliability-v4'; State = $State; Code = $Code }
    foreach ($key in $Extra.Keys) { $value[$key] = $Extra[$key] }
    [IO.File]::WriteAllText($statusPath, ($value | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
}
function Assert-MainTask($Task) {
    $actions = $Task.Definition.Actions
    if ($actions.Count -ne 1 -or $actions.Item(1).Path -ine 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -or
        $actions.Item(1).Arguments -cne '-NoProfile -NonInteractive -File E:\LLM\scripts\Host-LLM.ps1' -or
        $Task.Definition.Principal.UserId -notin @('NT AUTHORITY\LOCAL SERVICE','S-1-5-19','LOCAL SERVICE')) { throw 'Main task identity mismatch.' }
}
function Get-LLMTaskOrNull($Folder, [string]$Name) {
    try { return $Folder.GetTask($Name) }
    catch {
        $failure = $_.Exception
        while ($failure.InnerException) { $failure = $failure.InnerException }
        if ($failure.HResult -eq -2147024894) { return $null }
        throw
    }
}
function Get-ProtectedHashes {
    $paths = @('C:\Apache24\conf\httpd.conf','C:\php74\php.ini','E:\Datacopy\wordpress\wp-config.php',
        'E:\Datacopy\wordpress\llm-api.php','E:\LLM\api\gateway.php','E:\LLM\scripts\launch-arguments.json',
        'E:\LLM\runtime\b10964\llama-server.exe')
    $result = @{}
    foreach ($path in $paths) { $result[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    return $result
}
if ($LibraryOnly) { return }
try {
    . (Join-Path $root 'scripts\Start-LLM.ps1') -LibraryOnly
    Assert-LLMTarget
    if ($Action -eq 'Status') {
        if (Test-Path -LiteralPath $statusPath) { Get-Content -LiteralPath $statusPath -Raw }
        else { '{"State":"NotInstalled","Code":"not_installed"}' }
        exit 0
    }
    $manifest = Assert-LLMPackage $packageRoot $expectedManifestHash
    $scheduler = New-Object -ComObject 'Schedule.Service'; $scheduler.Connect()
    $folder = $scheduler.GetFolder('\')
    if ($Action -eq 'Dispatch') {
        $dispatchLock = [IO.File]::Open((Join-Path $root 'logs\deployment-dispatch.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        try {
            if (Test-Path -LiteralPath $statusPath) {
                $prior = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                if ($prior.State -eq 'Complete' -and $prior.ManifestHash -ceq $expectedManifestHash) {
                    $same = $true
                    foreach ($file in $manifest.Files) {
                        if ((Get-FileHash -LiteralPath (Join-Path $root $file.Target) -Algorithm SHA256).Hash -cne $file.Sha256) { $same = $false }
                    }
                    if ($same) { '{"code":"already_installed"}'; exit 0 }
                }
            }
            $existing = Get-LLMTaskOrNull $folder 'EESL-LLM-Maintenance'
            if ($existing) {
            if ($existing.Definition.Actions.Count -ne 1 -or $existing.Definition.Actions.Item(1).Arguments -cne
                '-NoProfile -NonInteractive -File E:\LLM\maintenance\Invoke-LLMDeployment.ps1 -Action Run' -or
                $existing.Definition.Principal.UserId -notin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM')) { throw 'Maintenance task identity mismatch.' }
            if ([int]$existing.State -in @(2,4)) { '{"code":"installing"}'; exit 0 }
            }
        $definition = $scheduler.NewTask(0)
        $definition.RegistrationInfo.Description = 'Fixed, hash-verified LLM-only deployment; no caller command or path.'
        $definition.Principal.UserId = 'S-1-5-18'; $definition.Principal.LogonType = 5; $definition.Principal.RunLevel = 1
        $definition.Settings.ExecutionTimeLimit = 'PT10M'; $definition.Settings.MultipleInstances = 2
        $definition.Settings.DisallowStartIfOnBatteries = $false; $definition.Settings.StopIfGoingOnBatteries = $false
        $exec = $definition.Actions.Create(0); $exec.Path = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
        $exec.Arguments = '-NoProfile -NonInteractive -File E:\LLM\maintenance\Invoke-LLMDeployment.ps1 -Action Run'
        $exec.WorkingDirectory = $maintenanceRoot
        $registered = $folder.RegisterTaskDefinition('EESL-LLM-Maintenance', $definition, 6, 'SYSTEM', $null, 5, $null)
        Set-DeploymentStatus 'Queued' 'installing'
        [void]$registered.Run($null)
        '{"code":"installing"}'; exit 0
        } finally { $dispatchLock.Dispose() }
    }
    $main = $folder.GetTask('EESL-LLM-LocalAI'); Assert-MainTask $main
    $oldRunId = (Get-Content -LiteralPath (Join-Path $root 'logs\host-status.json') -Raw | ConvertFrom-Json).RunId
    $protected = Get-ProtectedHashes
    $backup = Join-Path $root ('logs\backups\reliability-v4-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
    [void][IO.Directory]::CreateDirectory($backup)
    [IO.File]::WriteAllText((Join-Path $backup 'task.xml'), $main.Xml)
    $taskSddl = $main.GetSecurityDescriptor(7)
    [IO.File]::WriteAllText((Join-Path $backup 'task.sddl'), $taskSddl)
    foreach ($file in $manifest.Files) {
        $target = Join-Path $root $file.Target
        if (Test-Path -LiteralPath $target) { Copy-Item -LiteralPath $target -Destination (Join-Path $backup $file.Source) }
    }
    Set-DeploymentStatus 'Draining' 'installing' @{ Backup = $backup; ProtectedHashes = $protected }
    $marker = Join-Path $root 'api\maintenance-request.json'
    $drainMarker = Join-Path $root 'api\drain-request.json'
    $originalAdmission = Get-Content -LiteralPath (Join-Path $root 'api\enabled') -Raw
    $interrupted = $false
    try {
    [IO.File]::WriteAllText($drainMarker, '{"reason":"authorized-llm-deployment"}')
    [IO.File]::WriteAllText((Join-Path $root 'api\enabled'), 'disabled')
    # PHP's gateway lock covers the complete generation. Close admission first,
    # then wait for the accepted request to finish before stopping our own task.
    $drain = [Diagnostics.Stopwatch]::StartNew(); $drained = $false
    while ($drain.Elapsed.TotalSeconds -lt 210) {
        $stream = $null
        try {
            $stream = [IO.File]::Open((Join-Path $root 'logs\gateway-state.json'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
            $stream.Lock(0,1); $stream.Unlock(0,1); $drained = $true; break
        } catch { Start-Sleep -Seconds 2 }
        finally { if ($stream) { $stream.Dispose() } }
    }
    if (-not $drained) { throw 'Generation drain deadline exceeded.' }
    [IO.File]::WriteAllText($marker, '{"reason":"authorized-llm-deployment"}')
    $interrupted = $true
    if ([int]$main.State -eq 4) { $main.Stop(0) }
    Start-Sleep -Seconds 2
    if (Test-Path -LiteralPath $script:LlmStatePath) {
        $state = Get-Content -LiteralPath $script:LlmStatePath -Raw | ConvertFrom-Json
        if (@(Get-LLMOwnedProcesses $state).Count) { [void](Stop-LLMOwned $state) }
    }
        foreach ($file in $manifest.Files) {
            $target = Join-Path $root $file.Target
            $candidate = $target + '.reliability-v4.tmp'
            Copy-Item -LiteralPath (Join-Path $packageRoot $file.Source) -Destination $candidate
            if (Test-Path -LiteralPath $target) {
                Set-Acl -LiteralPath $candidate -AclObject (Get-Acl -LiteralPath $target)
                [IO.File]::Replace($candidate, $target, (Join-Path $backup ($file.Source + '.replaced')))
            } else { [IO.File]::Move($candidate, $target) }
        }
        $definition = $main.Definition
        $definition.Settings.RestartCount = 3; $definition.Settings.RestartInterval = 'PT1M'
        [void]$folder.RegisterTaskDefinition('EESL-LLM-LocalAI', $definition, 6, 'NT AUTHORITY\LOCAL SERVICE', $null, 5, $taskSddl)
        $recovery = $scheduler.NewTask(0)
        $recovery.RegistrationInfo.Description = 'LLM-only supervisor recovery with CPU/memory/website and identity gates.'
        $recovery.Principal.UserId = 'S-1-5-18'; $recovery.Principal.LogonType = 5; $recovery.Principal.RunLevel = 1
        $recovery.Settings.MultipleInstances = 2; $recovery.Settings.ExecutionTimeLimit = 'PT2M'
        $recovery.Settings.DisallowStartIfOnBatteries = $false; $recovery.Settings.StopIfGoingOnBatteries = $false
        $trigger = $recovery.Triggers.Create(1); $trigger.StartBoundary = (Get-Date).AddMinutes(5).ToString('s')
        $trigger.Repetition.Interval = 'PT5M'; $trigger.Repetition.StopAtDurationEnd = $false
        $exec = $recovery.Actions.Create(0); $exec.Path = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
        $exec.Arguments = '-NoProfile -NonInteractive -File E:\LLM\scripts\Ensure-LLMRecovery.ps1'; $exec.WorkingDirectory = 'E:\LLM\scripts'
        $existing = Get-LLMTaskOrNull $folder 'EESL-LLM-Recovery'
        if ($existing -and ($existing.Definition.Actions.Count -ne 1 -or $existing.Definition.Actions.Item(1).Arguments -cne $exec.Arguments -or
            $existing.Definition.Principal.UserId -notin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM'))) { throw 'Recovery task identity mismatch.' }
        [void]$folder.RegisterTaskDefinition('EESL-LLM-Recovery', $recovery, 6, 'SYSTEM', $null, 5, $null)
        [IO.File]::Delete($marker)
        [IO.File]::Delete($drainMarker)
        $main = $folder.GetTask('EESL-LLM-LocalAI'); [void]$main.Run($null)
        $watch = [Diagnostics.Stopwatch]::StartNew(); $ready = $false
        while ($watch.Elapsed.TotalSeconds -lt 180) {
            Start-Sleep -Seconds 3
            try {
                $report = Get-Content -LiteralPath (Join-Path $root 'logs\host-status.json') -Raw | ConvertFrom-Json
                if ($report.SchemaVersion -eq 2 -and $report.Status -eq 'READY' -and $report.RunId -ne $null -and $report.RunId -cne $oldRunId) { $ready = $true; break }
            } catch { }
        }
        if (-not $ready) { throw 'New supervisor readiness deadline exceeded.' }
        $after = Get-ProtectedHashes
        foreach ($path in $protected.Keys) { if ($protected[$path] -cne $after[$path]) { throw 'Protected configuration changed.' } }
        $pages = @(Get-LLMWebsiteHealth)
        if ($pages.Count -ne 2 -or @($pages | Where-Object Status -ne 301).Count) { throw 'Website post-check failed.' }
        Set-DeploymentStatus 'Complete' 'installed' @{ Backup = $backup; ManifestHash = $expectedManifestHash; ProtectedHashes = $after; HostRunId = $report.RunId;
            HostStatus = $report.Status; CpuCount = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors;
            FreeMemoryGB = [math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB,2);
            DiskFreeGB = [math]::Round((Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='E:'").FreeSpace/1GB,2) }
    } catch {
        # Roll back only this package and its own task; never restore/overwrite a
        # protected website/database/global configuration changed by someone else.
        if (-not $interrupted) {
            if (Test-Path -LiteralPath $marker) { [IO.File]::Delete($marker) }
            if (Test-Path -LiteralPath $drainMarker) { [IO.File]::Delete($drainMarker) }
            [IO.File]::WriteAllText((Join-Path $root 'api\enabled'), $originalAdmission)
            Set-DeploymentStatus 'Failed' 'drain_failed_original_service_preserved' @{ Backup = $backup }
            exit 1
        }
        try { $folder.GetTask('EESL-LLM-LocalAI').Stop(0) } catch { }
        if (Test-Path -LiteralPath $script:LlmStatePath) {
            try { $state = Get-Content $script:LlmStatePath -Raw | ConvertFrom-Json; [void](Stop-LLMOwned $state) } catch { }
        }
        foreach ($file in $manifest.Files) {
            $original = Join-Path $backup $file.Source
            if (Test-Path -LiteralPath $original) { Copy-Item -LiteralPath $original -Destination (Join-Path $root $file.Target) -Force }
        }
        [void]$folder.RegisterTask('EESL-LLM-LocalAI', (Get-Content (Join-Path $backup 'task.xml') -Raw), 6, 'NT AUTHORITY\LOCAL SERVICE', $null, 5, $taskSddl)
        if (Test-Path -LiteralPath $marker) { [IO.File]::Delete($marker) }
        if (Test-Path -LiteralPath $drainMarker) { [IO.File]::Delete($drainMarker) }
        [void]$folder.GetTask('EESL-LLM-LocalAI').Run($null)
        Set-DeploymentStatus 'RolledBack' 'deployment_failed' @{ Backup = $backup }
        exit 1
    }
} catch {
    try { Set-DeploymentStatus 'Failed' 'deployment_preflight_failed' } catch { }
    if ($Action -eq 'Dispatch') { '{"code":"deployment_failed"}' }
    exit 1
}
