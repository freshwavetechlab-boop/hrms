param([Parameter(Mandatory = $true)][string]$JobFile, [switch]$Stop)

$ErrorActionPreference = 'Stop'
$job = Get-Content -LiteralPath $JobFile -Raw | ConvertFrom-Json

function Set-JobStatus([string]$status, [string]$message) {
    $state = @{
        Status = $status; Message = $message; WorkerId = $PID
        StartedAt = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o')
        LogPath = $job.OutputLog; ErrorLog = $job.ErrorLog
    }
    $temporary = "$($job.StateFile).$PID.tmp"
    $state | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $job.StateFile -Force
}

if ($Stop) {
    try {
        $previous = if (Test-Path -LiteralPath $job.StateFile) { Get-Content -LiteralPath $job.StateFile -Raw | ConvertFrom-Json }
        $worker = if ($previous) { Get-Process -Id $previous.WorkerId -ErrorAction SilentlyContinue }
        $targetId = $null
        if ($worker -and $previous.Status -eq 'Starting' -and $worker.StartTime.ToUniversalTime().ToString('o') -eq $previous.StartedAt) {
            # Stop our worker and its complete process tree, including dotnet's API child.
            $targetId = $worker.Id
        } else {
            $listener = Get-NetTCPConnection -State Listen -LocalPort $job.Port -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($listener) {
                $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
                $expectedApi = [System.IO.Path]::GetFullPath($job.ProjectDirectory).TrimEnd('\') + '\'
                $isApi = $job.Id -eq 'api' -and $process.Name -eq 'Payroll.API.exe' -and $process.ExecutablePath.StartsWith($expectedApi, [StringComparison]::OrdinalIgnoreCase)
                $isVite = $job.Id -in @('ui', 'ess') -and $process.Name -eq 'node.exe' -and $process.CommandLine -match 'vite[/\\]bin[/\\]vite\.js' -and $process.CommandLine -match "(?:^|\s)--port(?:=|\s+)$($job.Port)(?:\s|$)"
                if (-not ($isApi -or $isVite)) { throw "Port $($job.Port) belongs to another app. Stop it from its own console." }
                $targetId = $process.ProcessId
            }
        }
        Set-JobStatus 'Stopping' 'Stopping...'
        if ($targetId) {
            & "$env:SystemRoot\System32\taskkill.exe" /PID $targetId /T /F *> $job.OutputLog
            if ($LASTEXITCODE -ne 0 -and (Get-Process -Id $targetId -ErrorAction SilentlyContinue)) { throw 'Could not stop the app. Open logs for details.' }
        }
        Set-JobStatus 'Stopped' 'Stopped. Click Start to run again.'
    } catch { Set-JobStatus 'StopFailed' $_.Exception.Message }
    exit
}

$mutex = New-Object System.Threading.Mutex($false, "Local\FrevoHRMS.Port.$($job.Port)")
$ownsMutex = $false
try {
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [System.Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { exit }
    $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    if ($listeners.Port -contains [int]$job.Port) {
        Set-JobStatus 'Stopped' "Port $($job.Port) is already in use."
        exit
    }
    if (-not (Test-Path -LiteralPath $job.ProjectDirectory -PathType Container)) {
        throw 'Project folder is unavailable. Choose its new location in the launcher.'
    }
    Set-Location -LiteralPath $job.ProjectDirectory
    Set-JobStatus 'Starting' 'Starting...'
    if ($job.Id -eq 'api') {
        if (-not (Test-Path -LiteralPath 'Payroll.API.csproj')) { throw 'Choose the Payroll.API project folder.' }
        $executable = (Get-Command dotnet.exe -ErrorAction Stop).Source
        $arguments = @('run', '--launch-profile', 'http', '--', '--urls', "http://localhost:$($job.Port)",
            '--Database:AutoMigrate=false', '--BackgroundWorkers:Enabled=false',
            '--EngineHistory:Enabled=false', '--EngineActivity:Enabled=false')
        Set-JobStatus 'Starting' 'Building and starting API...'
    } else {
        $package = Get-Content -LiteralPath 'package.json' -Raw | ConvertFrom-Json
        $expectedName = if ($job.Id -eq 'ui') { 'payroll-ui' } else { 'ess-mss' }
        if ($package.name -ne $expectedName) { throw "Choose the $expectedName project folder." }
        $executable = (Get-Command node.exe -ErrorAction Stop).Source
        if (-not (Test-Path -LiteralPath 'node_modules/vite/bin/vite.js')) {
            Set-JobStatus 'Starting' 'Installing project dependencies...'
            $npm = (Get-Command npm.cmd -ErrorAction Stop).Source
            $installCommand = if (Test-Path -LiteralPath 'package-lock.json') { 'ci' } else { 'install' }
            $ErrorActionPreference = 'Continue'
            try {
                & $npm $installCommand *> "$($job.OutputLog).install.log"
                $installExitCode = $LASTEXITCODE
            } finally { $ErrorActionPreference = 'Stop' }
            if ($installExitCode -ne 0) { throw "Dependency installation failed. Check $($job.OutputLog).install.log" }
        }
        $arguments = @('node_modules/vite/bin/vite.js', '--host', 'localhost', '--port', [string]$job.Port, '--strictPort')
    }
    $server = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $job.ProjectDirectory `
        -WindowStyle Hidden -RedirectStandardOutput $job.OutputLog -RedirectStandardError $job.ErrorLog -PassThru
    # Retain the handle so Windows PowerShell can read ExitCode after the process exits.
    $null = $server.Handle
    $server.WaitForExit()
    if ($server.ExitCode -ne 0) { throw "Process exited ($($server.ExitCode)). Open logs for details." }
    Set-JobStatus 'Stopped' 'Stopped. Click Start to run again.'
} catch {
    Set-JobStatus 'Failed' $_.Exception.Message
} finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
