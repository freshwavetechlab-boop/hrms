#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Dispatch','Run')][string]$Action = 'Dispatch', [switch]$LibraryOnly)
$ErrorActionPreference = 'Stop'
$trainingRoot = 'E:\LLM\training'
$setupStatus = 'E:\LLM\logs\training-setup.json'
function Save-TrainingSetup([string]$State, [string]$Step, [hashtable]$Extra = @{}) {
    $value = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('o'); State = $State; Step = $Step }
    foreach ($name in $Extra.Keys) { $value[$name] = $Extra[$name] }
    . 'E:\LLM\scripts\RecoveryPolicy.ps1'
    Write-LLMAtomicJson $setupStatus $value
}
function Set-TrainingDirectoryAcl([string]$Path, [bool]$Writable) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not ($resolved -eq $trainingRoot -or $resolved.StartsWith($trainingRoot + '\',[StringComparison]::OrdinalIgnoreCase))) { throw 'Training path escaped LLM storage.' }
    [void][IO.Directory]::CreateDirectory($resolved)
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $source = Get-Acl -LiteralPath 'E:\LLM\secrets\control-api-key.txt'
    foreach ($access in $source.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if ($access.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow) {
            [void]$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($access.IdentityReference,
                [Security.AccessControl.FileSystemRights]::FullControl,
                ([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),
                [Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)))
        }
    }
    $rights = $(if ($Writable) { [Security.AccessControl.FileSystemRights]::Modify } else { [Security.AccessControl.FileSystemRights]::ReadAndExecute })
    [void]$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-19')),
        $rights,([Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit),
        [Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)))
    Set-Acl -LiteralPath $resolved -AclObject $acl
}
function Write-TrainingKey([string]$Name, [bool]$WorkerCanRead) {
    if ($Name -notin @('learning-api-key.txt','learning-data-key.txt')) { throw 'Invalid key purpose.' }
    $path = 'E:\LLM\secrets\' + $Name
    if (-not (Test-Path -LiteralPath $path)) {
        $bytes = New-Object byte[] 32; $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        $text = ([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant()
        [IO.File]::WriteAllText($path,$text,(New-Object Text.UTF8Encoding($false))); $text = $null
    }
    if (([IO.File]::ReadAllText($path)).Trim() -notmatch '^[a-f0-9]{64}$') { throw 'Invalid existing learning credential.' }
    $acl = Get-Acl -LiteralPath 'E:\LLM\secrets\control-api-key.txt'
    if ($WorkerCanRead) {
        [void]$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-19')),
            [Security.AccessControl.FileSystemRights]::Read,[Security.AccessControl.AccessControlType]::Allow)))
    }
    Set-Acl -LiteralPath $path -AclObject $acl
}
function Invoke-SetupPython([string[]]$Arguments, [string]$LogName) {
    if ($LogName -notmatch '^[a-z0-9-]+\.log$') { throw 'Invalid setup log.' }
    $quoted = @($Arguments | ForEach-Object { if ($_ -match '["\r\n]') { throw 'Invalid fixed setup argument.' }; '"' + $_ + '"' })
    $process = Start-Process -FilePath 'E:\LLM\training\python\python.exe' -ArgumentList $quoted -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput ('E:\LLM\logs\' + $LogName) -RedirectStandardError ('E:\LLM\logs\' + $LogName + '.stderr')
    if ($process.ExitCode -ne 0) { throw ('Training prerequisite failed: ' + $LogName) }
}
if ($LibraryOnly) { return }
try {
    . 'E:\LLM\scripts\Start-LLM.ps1' -LibraryOnly
    Assert-LLMTarget
    if ($Action -eq 'Dispatch') {
        $scheduler = New-Object -ComObject 'Schedule.Service'; $scheduler.Connect(); $folder = $scheduler.GetFolder('\')
        $existing = $null
        try { $existing = $folder.GetTask('EESL-LLM-TrainingSetup') }
        catch {
            $failure = $_.Exception; while ($failure.InnerException) { $failure = $failure.InnerException }
            if ($failure.HResult -ne -2147024894) { throw }
        }
        if ($existing) {
            if ($existing.Definition.Actions.Count -ne 1 -or $existing.Definition.Actions.Item(1).Arguments -cne
                '-NoProfile -NonInteractive -File E:\LLM\maintenance\Initialize-LLMTraining.ps1 -Action Run' -or
                $existing.Definition.Principal.UserId -notin @('SYSTEM','S-1-5-18','NT AUTHORITY\SYSTEM')) { throw 'Training setup task identity mismatch.' }
            if ([int]$existing.State -in @(2,4)) { '{"code":"preparing_training"}'; exit 0 }
        }
        if (Test-Path -LiteralPath $setupStatus) {
            $previous = Get-Content -LiteralPath $setupStatus -Raw | ConvertFrom-Json
            if ($previous.State -eq 'Ready' -and ($previous.PSObject.Properties.Name -contains 'TrainingBaseMode') -and $previous.TrainingBaseMode -eq 'serving-gguf-fp32') {
                $same = $true
                foreach ($source in @('train_verified.py','training_core.py','export_value_lora.py')) {
                    $installed = $trainingRoot+'\worker\'+$source
                    if (-not (Test-Path -LiteralPath $installed) -or (Get-FileHash -LiteralPath $installed).Hash -cne
                        (Get-FileHash -LiteralPath ('E:\LLM\maintenance\'+$source)).Hash) { $same = $false }
                }
                if ($same) { '{"code":"training_ready"}'; exit 0 }
            }
        }
        $definition = $scheduler.NewTask(0)
        $definition.RegistrationInfo.Description = 'Prepare the isolated CPU LoRA environment under E:\LLM only; no global install.'
        $definition.Principal.UserId = 'S-1-5-18'; $definition.Principal.LogonType = 5; $definition.Principal.RunLevel = 1
        $definition.Settings.MultipleInstances = 2; $definition.Settings.ExecutionTimeLimit = 'PT6H'
        $definition.Settings.DisallowStartIfOnBatteries = $false; $definition.Settings.StopIfGoingOnBatteries = $false
        $exec = $definition.Actions.Create(0); $exec.Path = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
        $exec.Arguments = '-NoProfile -NonInteractive -File E:\LLM\maintenance\Initialize-LLMTraining.ps1 -Action Run'
        $exec.WorkingDirectory = 'E:\LLM\maintenance'
        $registered = $folder.RegisterTaskDefinition('EESL-LLM-TrainingSetup',$definition,6,'SYSTEM',$null,5,$null)
        Save-TrainingSetup 'Queued' 'AwaitingSetup'
        [void]$registered.Run($null)
        '{"code":"preparing_training"}'; exit 0
    }
    if ((Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='E:'").FreeSpace / 1GB -lt 35) { throw 'Insufficient LLM training disk space.' }
    if ((Test-Path -LiteralPath $trainingRoot) -and -not (Test-Path -LiteralPath ($trainingRoot + '\owner.json'))) { throw 'Unowned training folder.' }
    Set-TrainingDirectoryAcl $trainingRoot $false
    if (Test-Path -LiteralPath ($trainingRoot + '\owner.json')) {
        if ((Get-Content ($trainingRoot + '\owner.json') -Raw | ConvertFrom-Json).Owner -cne 'FrevoLLMTraining-v1') { throw 'Training owner mismatch.' }
    } else { [IO.File]::WriteAllText(($trainingRoot + '\owner.json'),'{"Owner":"FrevoLLMTraining-v1"}') }
    foreach ($directory in @('downloads','python','base','converter','worker')) { Set-TrainingDirectoryAcl ($trainingRoot + '\' + $directory) $false }
    foreach ($directory in @('state','jobs','datasets','adapters','cache','tmp')) { Set-TrainingDirectoryAcl ($trainingRoot + '\' + $directory) $true }
    Write-TrainingKey 'learning-api-key.txt' $false
    Write-TrainingKey 'learning-data-key.txt' $true
    $lock = Get-Content -LiteralPath 'E:\LLM\maintenance\training-lock.json' -Raw | ConvertFrom-Json
    if ($lock.schemaVersion -ne 1 -or $lock.baseModel -cne 'Qwen/Qwen3.5-4B' -or $lock.baseRevision -notmatch '^[a-f0-9]{40}$' -or
        $lock.converterRevision -notmatch '^[a-f0-9]{40}$') { throw 'Unsupported training lock.' }
    $env:TEMP = $trainingRoot + '\tmp'; $env:TMP = $env:TEMP
    $env:PIP_CACHE_DIR = $trainingRoot + '\cache\pip'; $env:HF_HOME = $trainingRoot + '\cache\huggingface'
    $env:HF_HUB_DISABLE_XET = '1'; $env:CUDA_VISIBLE_DEVICES = ''; $env:OMP_NUM_THREADS = '1'; $env:MKL_NUM_THREADS = '1'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Save-TrainingSetup 'Preparing' 'PythonRuntime'
    $pythonZip = $trainingRoot + '\downloads\python-3.11.9-embed-amd64.zip'
    if (-not (Test-Path -LiteralPath ($trainingRoot + '\python\python.exe'))) {
        if (-not (Test-Path -LiteralPath $pythonZip)) { Invoke-WebRequest -Uri 'https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip' -OutFile $pythonZip -UseBasicParsing -TimeoutSec 120 }
        Expand-Archive -LiteralPath $pythonZip -DestinationPath ($trainingRoot + '\python') -Force
        $pth = $trainingRoot + '\python\python311._pth'
        [IO.File]::WriteAllText($pth,"python311.zip`r`n.`r`nLib\site-packages`r`nimport site`r`n",(New-Object Text.UTF8Encoding($false)))
    }
    $getPip = $trainingRoot + '\downloads\get-pip.py'
    $wheelhouse = $trainingRoot + '\downloads\wheels'
    if (-not (Test-Path -LiteralPath ($wheelhouse + '\offline-ready.json'))) { throw 'Verified offline wheelhouse is not complete.' }
    foreach ($item in (Get-Content -LiteralPath ($wheelhouse + '\offline-manifest.json') -Raw | ConvertFrom-Json)) {
        if ($item.name -notmatch '^[A-Za-z0-9_.+\-]+\.whl$') { throw 'Invalid offline wheel filename.' }
        $wheel = Join-Path $wheelhouse $item.name
        if ((Get-Item -LiteralPath $wheel).Length -ne $item.bytes -or (Get-FileHash -LiteralPath $wheel -Algorithm SHA256).Hash -ine $item.sha256) { throw 'Offline wheel verification failed.' }
    }
    foreach ($item in (Get-Content -LiteralPath ($trainingRoot + '\downloads\asset-manifest.json') -Raw | ConvertFrom-Json)) {
        if ($item.name -notmatch '^[A-Za-z0-9_.\-]+$' -or (Get-FileHash -LiteralPath (Join-Path ($trainingRoot + '\downloads') $item.name)).Hash -ine $item.sha256) { throw 'Offline asset verification failed.' }
    }
    if (-not (Test-Path -LiteralPath ($trainingRoot + '\python\Lib\site-packages\pip'))) {
        Invoke-SetupPython @($getPip,'--disable-pip-version-check','--no-index','--find-links',$wheelhouse) 'training-install-pip.log'
    }
    Save-TrainingSetup 'Preparing' 'CpuTrainingLibraries'
    Invoke-SetupPython @('-m','pip','install','--disable-pip-version-check','--no-index','--find-links',$wheelhouse,
        '--require-hashes','-r',($wheelhouse+'\requirements.lock')) 'training-install-libraries.log'
    Save-TrainingSetup 'Preparing' 'PinnedConverter'
    $converterZip = $trainingRoot + '\downloads\llama-' + $lock.converterRevision + '.zip'
    $converterSource = $trainingRoot + '\converter\llama.cpp-' + $lock.converterRevision
    if (-not (Test-Path -LiteralPath ($converterSource + '\convert_lora_to_gguf.py'))) {
        if (-not (Test-Path -LiteralPath $converterZip)) { Invoke-WebRequest -Uri ('https://codeload.github.com/ggml-org/llama.cpp/zip/' + $lock.converterRevision) -OutFile $converterZip -UseBasicParsing -TimeoutSec 240 }
        Expand-Archive -LiteralPath $converterZip -DestinationPath ($trainingRoot + '\converter') -Force
    }
    # Embedded Python does not add a launched script's directory to sys.path.
    # Trust only the already hash-pinned converter package, not a caller's cwd.
    [IO.File]::WriteAllText(($trainingRoot+'\python\python311._pth'),
        ("python311.zip`r`n.`r`nLib\site-packages`r`n..\converter\llama.cpp-"+$lock.converterRevision+"`r`nimport site`r`n"),(New-Object Text.UTF8Encoding($false)))
    Invoke-SetupPython @('-m','pip','install','--disable-pip-version-check','--no-index','--find-links',$wheelhouse,
        '--no-build-isolation',($converterSource+'\gguf-py')) 'training-install-gguf.log'
    Invoke-SetupPython @(($converterSource+'\convert_lora_to_gguf.py'),'--help') 'training-converter-smoke.log'
    Save-TrainingSetup 'Preparing' 'OriginalTrainingWeights'
    # Large weights may still be arriving over the authenticated offline share.
    # This setup task waits without restarting inference or using global networking.
    $weightDeadline = [DateTime]::UtcNow.AddHours(4)
    while (-not (Test-Path -LiteralPath ($trainingRoot+'\base\offline-manifest.json'))) {
        if ([DateTime]::UtcNow -gt $weightDeadline) { throw 'Offline base weight transfer is incomplete.' }
        Start-Sleep -Seconds 30
    }
    Invoke-SetupPython @('E:\LLM\maintenance\download_training_base.py') 'training-download-base.log'
    Invoke-SetupPython @('-m','pip','freeze') 'training-environment-lock.log'
    Copy-Item -LiteralPath 'E:\LLM\maintenance\training-lock.json' -Destination ($trainingRoot + '\training-lock.json') -Force
    foreach ($file in @('train_verified.py','training_core.py','export_value_lora.py')) {
        Copy-Item -LiteralPath ('E:\LLM\maintenance\'+$file) -Destination ($trainingRoot+'\worker\'+$file) -Force
    }
    $scheduler = New-Object -ComObject 'Schedule.Service'; $scheduler.Connect(); $folder = $scheduler.GetFolder('\')
    $worker = $scheduler.NewTask(0)
    $worker.RegistrationInfo.Description = 'Verified CPU LoRA only; fixed LLM paths, one thread, 20 percent job CPU and 24 GB job memory.'
    $worker.Principal.UserId = 'S-1-5-19'; $worker.Principal.LogonType = 5; $worker.Principal.RunLevel = 0
    $worker.Settings.MultipleInstances = 2; $worker.Settings.ExecutionTimeLimit = 'PT8H'
    $worker.Settings.DisallowStartIfOnBatteries = $false; $worker.Settings.StopIfGoingOnBatteries = $false
    $trigger = $worker.Triggers.Create(1); $trigger.StartBoundary = [DateTime]::Now.AddMinutes(1).ToString('yyyy-MM-ddTHH:mm:ss')
    $trigger.Repetition.Interval = 'PT5M'; $trigger.Repetition.StopAtDurationEnd = $false
    $execute = $worker.Actions.Create(0); $execute.Path = $trainingRoot+'\python\python.exe'
    $execute.Arguments = '-I E:\LLM\training\worker\train_verified.py'; $execute.WorkingDirectory = $trainingRoot+'\worker'
    [void]$folder.RegisterTaskDefinition('EESL-LLM-VerifiedTraining',$worker,6,'NT AUTHORITY\LOCAL SERVICE',$null,5,$null)
    Save-TrainingSetup 'Ready' 'Complete' @{ BaseModel = $lock.baseModel; BaseRevision = $lock.baseRevision;
        ConverterRevision = $lock.converterRevision; PythonVersion = $lock.pythonVersion;
        TorchCpuVersion = $lock.torchCpuVersion; TrainingBaseMode = $lock.trainingBaseMode;
        BaseWeightsFile = 'E:\LLM\Qwen3.5-4B-Q4_K_M.gguf'; MetadataDirectory = 'E:\LLM\training\base' }
} catch {
    $failure = $_
    try { Save-TrainingSetup 'Failed' 'PrerequisiteFailed' @{ FailureLine = $failure.InvocationInfo.ScriptLineNumber;
        FailureType = $failure.Exception.GetType().Name; Message = ($failure.Exception.Message -replace '(?i)Bearer\s+\S+|\b[a-f0-9]{64}\b','[redacted]') } } catch { }
    if ($Action -eq 'Dispatch') { '{"code":"training_setup_failed"}' }
    exit 1
}
