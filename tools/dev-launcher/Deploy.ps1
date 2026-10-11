param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [string]$DataDirectory = $PSScriptRoot,
    [switch]$NoWindow,
    [string]$PreviewPath
)

$ErrorActionPreference = 'Stop'
if ($NoWindow) { return }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$nodePath = (Get-Command node.exe -ErrorAction Stop).Source
$deployScript = Join-Path $PSScriptRoot 'deploy.mjs'
$settingsPath = Join-Path $DataDirectory 'deploy-settings.json'
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'deploy-defaults.json') -Raw | ConvertFrom-Json
if (Test-Path -LiteralPath $settingsPath) {
    try { $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } catch { }
}
$script:deploymentProcess = $null
$script:deploymentJob = $null
$script:deploymentReview = $null
$script:deploymentState = $null

$window = New-Object System.Windows.Forms.Form
$window.Text = 'Frevo HRMS - Deploy to Coolify'
$window.ClientSize = New-Object System.Drawing.Size(880, 740)
$window.FormBorderStyle = 'FixedDialog'
$window.MaximizeBox = $false
$window.StartPosition = 'CenterScreen'
$window.AutoScaleMode = 'Dpi'
$window.Font = New-Object System.Drawing.Font('Segoe UI', 9)
$window.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#f3f6fb')
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Frevo.ico')) { $window.Icon = New-Object System.Drawing.Icon((Join-Path $PSScriptRoot 'Frevo.ico')) }

function Add-DeployLabel([string]$text, [int]$x, [int]$y, [int]$width, [int]$height) {
    $control = New-Object System.Windows.Forms.Label
    $control.Text = $text
    $control.SetBounds($x, $y, $width, $height)
    $window.Controls.Add($control)
    return $control
}
function Add-DeployText([int]$x, [int]$y, [int]$width, [int]$height, [switch]$ReadOnly, [switch]$Multiline) {
    $control = New-Object System.Windows.Forms.TextBox
    $control.ReadOnly = $ReadOnly.IsPresent
    $control.Multiline = $Multiline.IsPresent
    if ($Multiline) { $control.ScrollBars = 'Vertical' }
    $control.SetBounds($x, $y, $width, $height)
    $window.Controls.Add($control)
    return $control
}
function Add-DeployButton([string]$text, [int]$x, [int]$y, [int]$width, [scriptblock]$action) {
    $control = New-Object System.Windows.Forms.Button
    $control.Text = $text
    $control.SetBounds($x, $y, $width, 36)
    $control.FlatStyle = 'Flat'
    $control.BackColor = [System.Drawing.Color]::White
    $control.Add_Click($action)
    $window.Controls.Add($control)
    return $control
}
function Show-DeployError([string]$message) {
    [System.Windows.Forms.MessageBox]::Show($window, $message, 'Frevo deployment', 'OK', 'Warning') | Out-Null
}
function Save-DeployJson($value, [string]$destination) {
    $temporary = "$destination.tmp"
    $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $destination -Force
}
function Refresh-DeploymentReview {
    $result = & $nodePath $deployScript --review $ProjectRoot 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($result -join "`n") }
    $script:deploymentReview = ($result -join "`n") | ConvertFrom-Json
    $repositoryLabel.Text = "$($script:deploymentReview.root)  |  $($script:deploymentReview.branch) -> $($script:deploymentReview.upstream)"
    $files.Text = if ($script:deploymentReview.status) { $script:deploymentReview.status.Replace("`n", "`r`n") } else { 'No changed files. The current commit will be pushed.' }
}
function Get-DeploymentApps {
    $applications = @()
    foreach ($row in $applicationRows) {
        $url = $row.Url.Text.Trim()
        $parsed = $null
        if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$parsed) -or $parsed.Scheme -notin @('http', 'https') -or
            $parsed.UserInfo -or $parsed.Query -or $parsed.Fragment -or $parsed.AbsolutePath -notmatch '^/project/[^/]+/environment/[^/]+/application/[^/]+/?$') {
            throw "Paste the Coolify Configuration page URL for $($row.Name)."
        }
        $applications += @{ name = $row.Name; url = $url.TrimEnd('/') }
    }
    if (@($applications.url | Select-Object -Unique).Count -ne 3) { throw 'Choose three different Coolify applications.' }
    if (@($applications | ForEach-Object { ([Uri]$_.url).GetLeftPart([UriPartial]::Authority) } | Select-Object -Unique).Count -ne 1) { throw 'All three applications must be on the same Coolify instance.' }
    return $applications
}
function Set-DeploymentInputs([bool]$enabled) {
    $commitButton.Enabled = $enabled
    $refreshButton.Enabled = $enabled
    $message.Enabled = $enabled
    $remember.Enabled = $enabled
    foreach ($row in $applicationRows) { $row.Url.Enabled = $enabled }
}
function Refresh-DeploymentState {
    if (-not $script:deploymentJob) { return }
    if (Test-Path -LiteralPath $script:deploymentJob.StateFile) {
        try { $script:deploymentState = Get-Content -LiteralPath $script:deploymentJob.StateFile -Raw | ConvertFrom-Json } catch { return }
        if ($script:deploymentState.JobId -ne $script:deploymentJob.Id) { return }
        $status.Text = $script:deploymentState.Message
        $approveButton.Enabled = $script:deploymentState.Status -eq 'AwaitingApproval' -and -not (Test-Path -LiteralPath $script:deploymentJob.ApprovalFile) -and -not (Test-Path -LiteralPath $script:deploymentJob.CancelFile)
        $resultLines = @()
        if ($script:deploymentState.Commit) { $resultLines += "Pushed commit: $($script:deploymentState.Commit)" }
        foreach ($app in $script:deploymentState.Apps) { $resultLines += "$($app.Name): $($app.Status)  $($app.Url)" }
        $results.Text = $resultLines -join "`r`n"
        $terminal = $script:deploymentState.Status -in @('Requested', 'Cancelled', 'Failed')
        $cancelButton.Enabled = -not $terminal -and -not (Test-Path -LiteralPath $script:deploymentJob.CancelFile)
        if ($script:deploymentState.Status -eq 'Requested') { $cancelButton.Text = 'Cancel' }
        if ($script:deploymentState.Status -in @('Failed', 'Cancelled')) { $status.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#b42318') }
    }
    if ($script:deploymentProcess -and $script:deploymentProcess.HasExited) {
        Set-DeploymentInputs $true
        $cancelButton.Enabled = $false
        $approveButton.Enabled = $false
        if (-not $script:deploymentState -or $script:deploymentState.Status -notin @('Requested', 'Cancelled', 'Failed')) {
            $status.Text = 'Deployment worker exited unexpectedly. Open logs before trying again.'
        }
    }
}

$heading = Add-DeployLabel 'Deploy HRMS' 20 15 400 32
$heading.Font = New-Object System.Drawing.Font('Segoe UI', 17, [System.Drawing.FontStyle]::Bold)
Add-DeployLabel 'Review files, commit and push. Coolify redeploy requires a separate confirmation.' 21 51 840 25 | Out-Null
$repositoryLabel = Add-DeployLabel $ProjectRoot 21 83 838 28
Add-DeployLabel 'Files included by git add . (ignored files excluded)' 21 114 800 24 | Out-Null
$files = Add-DeployText 20 140 840 126 -ReadOnly -Multiline
$files.Font = New-Object System.Drawing.Font('Consolas', 9)
Add-DeployLabel 'Commit message' 21 277 180 24 | Out-Null
$message = Add-DeployText 20 303 840 27
Add-DeployLabel 'Coolify Configuration URLs (prefilled from your screenshots; verify before first use)' 21 342 838 24 | Out-Null
$applicationRows = @()
$index = 0
foreach ($application in $settings.Apps) {
    Add-DeployLabel $application.name 21 (374 + $index * 34) 125 25 | Out-Null
    $urlBox = Add-DeployText 150 (371 + $index * 34) 710 27
    $urlBox.Text = $application.url
    $applicationRows += @{ Name = $application.name; Url = $urlBox }
    $index++
}
$remember = New-Object System.Windows.Forms.CheckBox
$remember.Text = 'Remember Coolify login in a separate Chrome profile on this PC'
$remember.SetBounds(21, 477, 830, 25)
$remember.Checked = $false
$window.Controls.Add($remember)
$status = Add-DeployLabel 'Ready. Enter a commit message and review the files above.' 21 512 838 65
$results = New-Object System.Windows.Forms.RichTextBox
$results.ReadOnly = $true
$results.DetectUrls = $true
$results.SetBounds(20, 583, 840, 82)
$results.Add_LinkClicked({ param($sender, $eventArgs) Start-Process $eventArgs.LinkText })
$window.Controls.Add($results)

$commitButton = Add-DeployButton 'Commit && push' 20 685 155 {
    try {
        if (-not $message.Text.Trim()) { throw 'Enter a commit message.' }
        if ($script:deploymentProcess -and -not $script:deploymentProcess.HasExited) { throw 'A deployment is already running.' }
        $applications = @(Get-DeploymentApps)
        $reviewResult = & $nodePath $deployScript --review $ProjectRoot 2>&1
        if ($LASTEXITCODE -ne 0) { throw ($reviewResult -join "`n") }
        $latestReview = ($reviewResult -join "`n") | ConvertFrom-Json
        if ($latestReview.fingerprint -ne $script:deploymentReview.fingerprint) { Refresh-DeploymentReview; throw 'The files changed. Review the refreshed list and click Commit & push again.' }
        $confirmText = "Commit all listed files and push $($latestReview.branch) to $($latestReview.upstream)?`n`nMessage: $($message.Text.Trim())`n`nCoolify redeploy will ask for confirmation after the push."
        if ([System.Windows.Forms.MessageBox]::Show($window, $confirmText, 'Confirm Git commit and push', 'YesNo', 'Question', 'Button2') -ne 'Yes') { return }
        Save-DeployJson @{ Apps = $applications } $settingsPath
        $jobs = Join-Path $DataDirectory 'jobs'
        $logs = Join-Path $DataDirectory 'logs'
        New-Item -ItemType Directory -Path $jobs, $logs -Force | Out-Null
        $jobId = [Guid]::NewGuid().ToString('N')
        $base = Join-Path $jobs "deploy-$jobId"
        $script:deploymentJob = @{
            Id = $jobId; ProjectRoot = $ProjectRoot; DataDirectory = $DataDirectory
            Message = $message.Text.Trim(); Review = $latestReview; Apps = $applications
            RememberSession = $remember.Checked; StateFile = "$base-state.json"
            ApprovalFile = "$base-approval.json"; CancelFile = "$base-cancel.json"; CloseFile = "$base-close.json"
            LogFile = Join-Path $logs "deploy-$jobId.log"
        }
        $jobFile = "$base-job.json"
        Save-DeployJson $script:deploymentJob $jobFile
        $script:deploymentState = $null
        $script:deploymentProcess = Start-Process -FilePath $nodePath -ArgumentList @("`"$deployScript`"", '--job', "`"$jobFile`"") -WorkingDirectory $DataDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput "$($script:deploymentJob.LogFile).output.log" -RedirectStandardError "$($script:deploymentJob.LogFile).error.log"
        Set-DeploymentInputs $false
        $cancelButton.Enabled = $true
        $status.ForeColor = [System.Drawing.ColorTranslator]::FromHtml('#627088')
        $status.Text = 'Starting Git commit and push...'
        $results.Clear()
    } catch { Show-DeployError $_.Exception.Message }
}
$commitButton.BackColor = [System.Drawing.ColorTranslator]::FromHtml('#087bb8')
$commitButton.ForeColor = [System.Drawing.Color]::White
$refreshButton = Add-DeployButton 'Refresh files' 186 685 120 { try { Refresh-DeploymentReview } catch { Show-DeployError $_.Exception.Message } }
$approveButton = Add-DeployButton 'Confirm redeploy' 318 685 160 {
    try {
        Refresh-DeploymentState
        if (-not $script:deploymentState -or $script:deploymentState.Status -ne 'AwaitingApproval') { return }
        $names = ($script:deploymentState.Apps | ForEach-Object { $_.Name }) -join ', '
        $confirmText = "Git push completed: $($script:deploymentState.Commit)`n`nRedeploy these Coolify applications now?`n$names"
        if ([System.Windows.Forms.MessageBox]::Show($window, $confirmText, 'Confirm Coolify redeploy', 'YesNo', 'Question', 'Button2') -eq 'Yes') {
            Save-DeployJson @{ JobId = $script:deploymentJob.Id; Commit = $script:deploymentState.Commit; Approved = $true } $script:deploymentJob.ApprovalFile
            $approveButton.Enabled = $false
        }
    } catch { Show-DeployError $_.Exception.Message }
}
$approveButton.Enabled = $false
$cancelButton = Add-DeployButton 'Cancel' 490 685 100 {
    if ($script:deploymentJob) {
        Save-DeployJson @{ Cancelled = $true } $script:deploymentJob.CancelFile
        $cancelButton.Enabled = $false
        $approveButton.Enabled = $false
        $status.Text = 'Cancelling after the current operation. Completed commits, pushes and redeploy requests stay completed.'
    }
}
$cancelButton.Enabled = $false
Add-DeployButton 'Open logs' 602 685 120 { Start-Process explorer.exe -ArgumentList "`"$(Join-Path $DataDirectory 'logs')`"" } | Out-Null
Add-DeployButton 'Close' 734 685 126 { $window.Close() } | Out-Null
$window.Add_FormClosing({
    param($sender, $eventArgs)
    if ($script:deploymentJob -and $script:deploymentProcess -and -not $script:deploymentProcess.HasExited) {
        Save-DeployJson @{ Close = $true } $script:deploymentJob.CloseFile
        if (-not $script:deploymentState -or $script:deploymentState.Status -ne 'Requested') { Save-DeployJson @{ Cancelled = $true } $script:deploymentJob.CancelFile }
    }
})
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 1000
$timer.Add_Tick({ Refresh-DeploymentState })
try {
    Refresh-DeploymentReview
    $timer.Start()
    if ($PreviewPath) {
        $window.Show()
        $window.Refresh()
        [System.Windows.Forms.Application]::DoEvents()
        $window.PerformLayout()
        $bitmap = New-Object System.Drawing.Bitmap($window.Width, $window.Height)
        try { $window.DrawToBitmap($bitmap, (New-Object System.Drawing.Rectangle(0, 0, $window.Width, $window.Height))); $bitmap.Save($PreviewPath) }
        finally { $bitmap.Dispose() }
    } else { [System.Windows.Forms.Application]::Run($window) }
} catch { if ($PreviewPath) { throw } else { Show-DeployError $_.Exception.Message } }
finally { $timer.Dispose(); $window.Dispose() }
