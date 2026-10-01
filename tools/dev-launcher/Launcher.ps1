param(
    [string]$DataDirectory = $PSScriptRoot,
    [switch]$NoWindow
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ProcessHelpers.ps1')
$services = @(
    @{ Id = 'ui'; Title = 'Payroll UI'; Folder = 'payroll-ui'; Port = 5173; Url = 'http://localhost:5173'; Button = 'Start UI' },
    @{ Id = 'api'; Title = 'Payroll API'; Folder = 'Payroll.API'; Port = 5062; Url = 'http://localhost:5062/swagger'; Button = 'Start API' },
    @{ Id = 'ess'; Title = 'Employee Self Service'; Folder = 'ess-mss'; Port = 5174; Url = 'http://localhost:5174'; Button = 'Start ESS' }
)
$settingsFile = Join-Path $DataDirectory 'settings.json'
$logsDirectory = Join-Path $DataDirectory 'logs'
$jobsDirectory = Join-Path $DataDirectory 'jobs'
New-Item -ItemType Directory -Path $DataDirectory, $logsDirectory, $jobsDirectory -Force | Out-Null
$locations = @{ ui = ''; api = ''; ess = '' }
if (Test-Path -LiteralPath $settingsFile) {
    try {
        $saved = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        foreach ($service in $services) { $locations[$service.Id] = [string]$saved.($service.Id) }
    } catch { Write-Warning 'Saved locations could not be read. Choose the project folder again.' }
}

function Save-Locations {
    $temporary = "$settingsFile.tmp"
    $locations | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $settingsFile -Force
}

function Test-ProjectFolder([string]$id, [string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $false }
    try {
        if ($id -eq 'api') { return (Test-Path -LiteralPath (Join-Path $path 'Payroll.API.csproj') -PathType Leaf) }
        $package = Get-Content -LiteralPath (Join-Path $path 'package.json') -Raw | ConvertFrom-Json
        $expectedName = if ($id -eq 'ui') { 'payroll-ui' } else { 'ess-mss' }
        return $package.name -eq $expectedName
    } catch { return $false }
}

function Set-ProjectRoot([string]$path) {
    $next = @{}
    foreach ($service in $services) {
        $candidate = Join-Path $path $service.Folder
        if (-not (Test-ProjectFolder $service.Id $candidate)) { throw "Choose the hrms folder containing payroll-ui, Payroll.API and ess-mss." }
        $next[$service.Id] = [System.IO.Path]::GetFullPath($candidate)
    }
    foreach ($service in $services) { $locations[$service.Id] = $next[$service.Id] }
    Save-Locations
}

function Get-ListeningPorts {
    return @([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | ForEach-Object { $_.Port })
}

function Get-ServiceState([string]$id) {
    $stateFile = Join-Path $jobsDirectory "$id-state.json"
    if (-not (Test-Path -LiteralPath $stateFile)) { return $null }
    try { return Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json } catch { return $null }
}

function Test-WorkerAlive($state) {
    if (-not $state -or $state.Status -notin @('Starting', 'Stopping')) { return $false }
    $process = Get-Process -Id $state.WorkerId -ErrorAction SilentlyContinue
    return $process -and $process.StartTime.ToUniversalTime().ToString('o') -eq $state.StartedAt
}

function Start-ServiceJob($service, [switch]$Stop, $KillProcess) {
    if (-not $Stop) {
        if ((Get-ListeningPorts) -contains $service.Port) { return }
        if (Test-WorkerAlive (Get-ServiceState $service.Id)) { return }
        if ([string]::IsNullOrWhiteSpace($locations[$service.Id])) { throw "Choose a valid folder for $($service.Title)." }
    }
    $stamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff')
    $jobFile = Join-Path $jobsDirectory "$($service.Id)-$stamp.json"
    $job = @{
        Id = $service.Id; Port = $service.Port; ProjectDirectory = $locations[$service.Id]
        StateFile = Join-Path $jobsDirectory "$($service.Id)-state.json"
        OutputLog = Join-Path $logsDirectory "$($service.Id)-$stamp.log"
        ErrorLog = Join-Path $logsDirectory "$($service.Id)-$stamp.error.log"
    }
    if ($KillProcess) {
        $job.TargetProcessId = $KillProcess.ProcessId
        $job.TargetCreatedAt = $KillProcess.CreationDate.ToUniversalTime().ToString('o')
    }
    $job | ConvertTo-Json | Set-Content -LiteralPath $jobFile -Encoding UTF8
    $workerPath = Join-Path $PSScriptRoot 'Worker.ps1'
    $workerArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden',
        '-File', "`"$workerPath`"", '-JobFile', "`"$jobFile`""
    )
    if ($Stop) { $workerArguments += '-Stop' }
    Start-Process -FilePath "$PSHOME\powershell.exe" -ArgumentList $workerArguments -WorkingDirectory $DataDirectory -WindowStyle Hidden | Out-Null
}

if ($NoWindow) { return }

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object System.Windows.Forms.Form
$form.Text = 'Frevo HRMS Launcher'
$form.ClientSize = New-Object System.Drawing.Size(690, 555)
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.StartPosition = 'CenterScreen'
$form.AutoScaleMode = 'Dpi'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$form.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#f3f6fb')
$iconFile = Join-Path $PSScriptRoot 'Frevo.ico'
if (Test-Path -LiteralPath $iconFile) { $form.Icon = New-Object System.Drawing.Icon($iconFile) }
$tooltip = New-Object System.Windows.Forms.ToolTip
$controls = @{}
$startingUntil = @{}

function Add-Label($parent, [string]$text, [int]$x, [int]$y, [int]$width, [int]$height) {
    $label = New-Object System.Windows.Forms.Label
    $label.Text = $text
    $label.SetBounds($x, $y, $width, $height)
    $label.AutoEllipsis = $true
    $parent.Controls.Add($label)
    return $label
}

function Add-Button($parent, [string]$text, [int]$x, [int]$y, [int]$width, [scriptblock]$action, [bool]$primary = $false) {
    $button = New-Object System.Windows.Forms.Button
    $button.Text = $text
    $button.SetBounds($x, $y, $width, 34)
    $button.FlatStyle = 'Flat'
    $button.FlatAppearance.BorderColor = [System.Drawing.ColorTranslator]::FromHtml('#d9e2ef')
    $button.BackColor = [System.Drawing.Color]::White
    if ($primary) {
        $button.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#087bb8')
        $button.ForeColor = [System.Drawing.Color]::White
        $button.FlatAppearance.BorderSize = 0
    }
    $button.Add_Click($action)
    $parent.Controls.Add($button)
    return $button
}

function Show-Error([string]$message) {
    [System.Windows.Forms.MessageBox]::Show($form, $message, 'Frevo HRMS Launcher', 'OK', 'Warning') | Out-Null
}

function Refresh-Locations {
    foreach ($service in $services) {
        $row = $controls[$service.Id]
        $row.Path.Text = $locations[$service.Id]
        $row.Valid = Test-ProjectFolder $service.Id $locations[$service.Id]
        $tooltip.SetToolTip($row.Path, $locations[$service.Id])
    }
}

function Refresh-Status {
    $ports = Get-ListeningPorts
    foreach ($service in $services) {
        $row = $controls[$service.Id]
        $state = Get-ServiceState $service.Id
        $busy = (Test-WorkerAlive $state) -or ($startingUntil.ContainsKey($service.Id) -and $startingUntil[$service.Id] -gt [DateTime]::Now)
        $stopping = $state -and $state.Status -eq 'Stopping' -and (Test-WorkerAlive $state)
        $row.Start.Enabled = $row.Valid -and -not $busy -and $ports -notcontains $service.Port
        $row.Stop.Enabled = (($ports -contains $service.Port) -or (Test-WorkerAlive $state)) -and -not $stopping
        $row.Kill.Enabled = $ports -contains $service.Port -and -not $stopping
        $row.Choose.Enabled = -not $busy -and $ports -notcontains $service.Port
        $row.Status.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#627088')
        if ($state -and $state.Status -eq 'StopFailed') {
            $row.Status.Text = $state.Message.Replace('Stop it from its own console.', 'Use Kill process below.')
            $row.Status.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#b42318')
        } elseif ($state -and $state.Status -eq 'Stopping' -and $busy) {
            $row.Status.Text = 'Stopping...'
        } elseif ($ports -contains $service.Port) {
            $row.Status.Text = "Listening on port $($service.Port)"
            $row.Status.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#16865d')
        } elseif ($busy) {
            $row.Status.Text = if ($state -and $state.Status -eq 'Starting') { $state.Message } else { 'Starting...' }
        } elseif (-not $row.Valid) {
            $row.Status.Text = 'Choose a valid project folder'
        } elseif ($state -and $state.Status -eq 'Failed') {
            $row.Status.Text = $state.Message
            $row.Status.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#b42318')
        } else { $row.Status.Text = 'Ready to start' }
        $tooltip.SetToolTip($row.Status, $row.Status.Text)
    }
}

function Invoke-Start($service, [switch]$Stop, $KillProcess) {
    try {
        Start-ServiceJob $service -Stop:$Stop -KillProcess $KillProcess
        $startingUntil[$service.Id] = [DateTime]::Now.AddSeconds(4)
    } catch { Show-Error $_.Exception.Message }
    Refresh-Status
}

$title = Add-Label $form 'Frevo HRMS' 20 14 230 30
$title.Font = New-Object System.Drawing.Font('Segoe UI', 17, [System.Drawing.FontStyle]::Bold)
Add-Label $form 'Local development console' 21 49 255 22 | Out-Null
Add-Button $form 'Choose project folder' 287 24 174 {
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Choose the hrms folder containing all three projects.'
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog($form) -eq 'OK') {
        try { Set-ProjectRoot $dialog.SelectedPath; Refresh-Locations; Refresh-Status }
        catch { Show-Error $_.Exception.Message }
    }
    $dialog.Dispose()
} | Out-Null
Add-Button $form 'Start all' 471 24 94 {
    foreach ($service in $services) { Invoke-Start $service }
} $true | Out-Null
Add-Button $form 'Stop all' 575 24 94 {
    foreach ($service in $services) {
        if ($controls[$service.Id].Stop.Enabled) { Invoke-Start $service -Stop }
    }
} | Out-Null

$index = 0
foreach ($service in $services) {
    $panel = New-Object System.Windows.Forms.Panel
    $panel.SetBounds(20, (84 + $index * 134), 650, 124)
    $panel.BackColor = [System.Drawing.Color]::White
    $panel.BorderStyle = 'FixedSingle'
    $form.Controls.Add($panel)
    $heading = Add-Label $panel $service.Title 12 10 242 24
    $heading.Font = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)
    $status = Add-Label $panel '' 254 12 380 21
    $status.TextAlign = 'TopRight'
    $path = New-Object System.Windows.Forms.TextBox
    $path.ReadOnly = $true
    $path.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#f8fafc')
    $path.SetBounds(12, 43, 487, 25)
    $panel.Controls.Add($path)
    $id = $service.Id
    $choose = Add-Button $panel 'Choose folder' 510 39 124 {
        param($sender, $eventArgs)
        $selectedId = [string]$sender.Tag
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = 'Choose this application project folder.'
        $dialog.ShowNewFolderButton = $false
        if ($dialog.ShowDialog($form) -eq 'OK') {
            if (Test-ProjectFolder $selectedId $dialog.SelectedPath) {
                $locations[$selectedId] = $dialog.SelectedPath
                Save-Locations; Refresh-Locations; Refresh-Status
            } else { Show-Error 'This folder does not contain the selected application. Choose its project folder.' }
        }
        $dialog.Dispose()
    }
    $choose.Tag = $id
    $start = Add-Button $panel $service.Button 12 80 100 {
        param($sender, $eventArgs)
        Invoke-Start ($services | Where-Object { $_.Id -eq $sender.Tag })
    } $true
    $start.Tag = $id
    $stopButton = Add-Button $panel 'Stop' 121 80 75 {
        param($sender, $eventArgs)
        Invoke-Start ($services | Where-Object { $_.Id -eq $sender.Tag }) -Stop
    }
    $stopButton.Tag = $id
    $stopButton.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#b42318')
    $killButton = Add-Button $panel 'Kill process' 205 80 105 {
        param($sender, $eventArgs)
        try {
            $selectedService = $services | Where-Object { $_.Id -eq $sender.Tag }
            $process = Get-PortProcess $selectedService.Port
            if (-not $process) { Refresh-Status; return }
            if ($process.ProcessId -le 4) { throw 'This is a Windows system process and cannot be stopped here.' }
            $message = "Kill $($process.Name) (PID $($process.ProcessId)) on port $($selectedService.Port)?`n`nThe process and its child processes will stop."
            if ([System.Windows.Forms.MessageBox]::Show($form, $message, 'Kill running process', 'YesNo', 'Warning', 'Button2') -eq 'Yes') {
                Invoke-Start $selectedService -Stop -KillProcess $process
            }
        } catch { Show-Error $_.Exception.Message }
    }
    $killButton.Tag = $id
    $killButton.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#b42318')
    $tooltip.SetToolTip($stopButton, 'Stop this app, including when started from another terminal.')
    $tooltip.SetToolTip($killButton, 'Kill the process currently using this port. Shows its name and PID before stopping it.')
    $link = New-Object System.Windows.Forms.LinkLabel
    $link.Text = $service.Url
    $link.Tag = $service.Url
    $link.SetBounds(325, 87, 307, 24)
    $link.LinkColor = [System.Drawing.ColorTranslator]::FromHtml('#087bb8')
    $link.Add_LinkClicked({ param($sender, $eventArgs) Start-Process ([string]$sender.Tag) })
    $panel.Controls.Add($link)
    $controls[$id] = @{ Path = $path; Status = $status; Start = $start; Stop = $stopButton; Kill = $killButton; Choose = $choose; Valid = $false }
    $index++
}
Add-Label $form 'Closing this window keeps your apps running.' 21 494 472 21 | Out-Null
$note = Add-Label $form 'API uses existing appsettings. Automatic migrations are off.' 21 517 490 21
$note.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#627088')
Add-Button $form 'Open logs' 543 498 126 { Start-Process explorer.exe -ArgumentList "`"$logsDirectory`"" } | Out-Null

Refresh-Locations
Refresh-Status
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2000
$timer.Add_Tick({ Refresh-Status })
$timer.Start()
try { [System.Windows.Forms.Application]::Run($form) }
finally { $timer.Dispose(); $tooltip.Dispose(); $form.Dispose() }
