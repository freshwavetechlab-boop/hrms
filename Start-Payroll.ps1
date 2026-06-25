$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$mysqlBin = $env:PAYROLL_MYSQL_BIN
if (!$mysqlBin) { $mysqlBin = 'C:\Program Files\MySQL\MySQL Server 8.0\bin' }
$mysqld = Join-Path $mysqlBin 'mysqld.exe'
$mysql = Join-Path $mysqlBin 'mysql.exe'
$data = Join-Path $root '.mysql-runtime2'
$sql = Join-Path $root 'payroll (1).sql'
$port = if ($env:PAYROLL_MYSQL_PORT) { [int]$env:PAYROLL_MYSQL_PORT } else { 3306 }
$apiPort = if ($env:PAYROLL_API_PORT) { [int]$env:PAYROLL_API_PORT } else { 5062 }
$uiPort = if ($env:PAYROLL_UI_PORT) { [int]$env:PAYROLL_UI_PORT } else { 5173 }
$password = $env:PAYROLL_MYSQL_PASSWORD
if (!$password) { throw 'Set PAYROLL_MYSQL_PASSWORD before starting the stack.' }
$logs = Join-Path $root '.runtime-logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null

if (!(Test-Path $mysqld) -or !(Test-Path $mysql)) { throw "MySQL 8 not found: $mysqlBin" }
if (!(Test-Path $sql)) { throw "Seed SQL missing: $sql" }

function Test-Mysql([bool]$withPass) {
  $args = @('--protocol=tcp', '--host=127.0.0.1', "--port=$port", '--user=root', '--connect-timeout=2')
  if ($withPass) { $args += "--password=$password" }
  $args += '--execute=SELECT 1'
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  & $mysql @args 1>$null 2>$null
  $ErrorActionPreference = $old
  $LASTEXITCODE -eq 0
}

function Invoke-Mysql([string]$query, [bool]$withPass = $true) {
  $args = @('--protocol=tcp', '--host=127.0.0.1', "--port=$port", '--user=root')
  if ($withPass) { $args += "--password=$password" }
  $args += "--execute=$query"
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  & $mysql @args 2>$null | Out-Host
  $ErrorActionPreference = $old
  if ($LASTEXITCODE -ne 0) { throw "MySQL query failed." }
}

function Get-MysqlScalar([string]$query) {
  $args = @('--protocol=tcp', '--host=127.0.0.1', "--port=$port", '--user=root', "--password=$password", '--batch', '--skip-column-names', "--execute=$query")
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  $value = (& $mysql @args 2>$null | Select-Object -First 1)
  $ErrorActionPreference = $old
  $value.Trim()
}

function Initialize-Db {
  if (Test-Path $data) {
    Rename-Item $data "$data.bad-$(Get-Date -Format yyyyMMdd-HHmmss)"
  }
  New-Item -ItemType Directory -Path $data | Out-Null
  & $mysqld --no-defaults --initialize-insecure --datadir="$data" --lower-case-table-names=1 --console | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "MySQL initialize failed." }
}

function Start-MysqlRuntime {
  if (Test-Mysql $true) { return }
  if (!(Test-Path (Join-Path $data 'mysql'))) { Initialize-Db }

  Start-Process -FilePath $mysqld -ArgumentList @(
    '--no-defaults', "--datadir=$data", "--innodb-undo-directory=$data",
    "--port=$port", '--bind-address=127.0.0.1', '--lower-case-table-names=1', '--console'
  ) -WindowStyle Hidden

  foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 500
    if ((Test-Mysql $true) -or (Test-Mysql $false)) { return }
  }

  Initialize-Db
  Start-Process -FilePath $mysqld -ArgumentList @(
    '--no-defaults', "--datadir=$data", "--innodb-undo-directory=$data",
    "--port=$port", '--bind-address=127.0.0.1', '--lower-case-table-names=1', '--console'
  ) -WindowStyle Hidden
  foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 500
    if ((Test-Mysql $true) -or (Test-Mysql $false)) { return }
  }
  throw "MySQL runtime could not start."
}

function Import-SeedIfNeeded {
  if (Test-Mysql $false) {
    Invoke-Mysql "ALTER USER 'root'@'localhost' IDENTIFIED BY '$password'; FLUSH PRIVILEGES;" $false
  }
  Invoke-Mysql 'CREATE DATABASE IF NOT EXISTS payroll;'
  $tables = Get-MysqlScalar "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='payroll';"
  if ([int]$tables -gt 0) { return }

  $cmd = "`"$mysql`" --protocol=tcp --host=127.0.0.1 --port=$port --user=root --password=$password payroll < `"$sql`""
  & "$env:SystemRoot\System32\cmd.exe" /c $cmd
  if ($LASTEXITCODE -ne 0) { throw "Seed import failed." }
}

function Test-Port([int]$p) {
  [bool](Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue)
}

function Start-LoggedProcess([string]$name, [string]$file, [string[]]$arguments, [string]$workingDirectory) {
  $stdout = Join-Path $logs "$name.out.log"
  $stderr = Join-Path $logs "$name.err.log"
  $process = Start-Process -FilePath $file -ArgumentList $arguments -WorkingDirectory $workingDirectory -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
  Write-Host "$name started. PID: $($process.Id). Logs: $stdout / $stderr"
  $process
}

function Wait-Http([string]$url, [string]$name) {
  foreach ($i in 1..30) {
    try {
      $response = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 2
      if ($response.StatusCode -lt 500) { Write-Host "$name ready: $url"; return }
    } catch {}
    Start-Sleep -Seconds 1
  }
  throw "$name did not become ready. Check .runtime-logs and Payroll.API/logs."
}

Start-MysqlRuntime
Import-SeedIfNeeded

$connection = "Server=127.0.0.1;Port=$port;User ID=root;Password=$password;SslMode=Preferred;AllowPublicKeyRetrieval=True;"
if (!(Test-Port $apiPort)) {
  $env:ConnectionStrings__Default = $connection
  $env:CORS_ALLOWED_ORIGINS = "http://127.0.0.1:$uiPort,http://localhost:$uiPort"
  $env:Auth__CookieSecure = 'false'
  Start-LoggedProcess 'backend' 'dotnet' @('run', '--urls', "http://localhost:$apiPort") (Join-Path $root 'Payroll.API') | Out-Null
}
if (!(Test-Port $uiPort)) {
  $npm = (Get-Command npm.cmd -ErrorAction SilentlyContinue).Source
  if (!$npm) { $npm = 'npm' }
  $env:VITE_API_URL = "http://localhost:$apiPort"
  Start-LoggedProcess 'frontend' $npm @('run', 'dev', '--', '--host', '127.0.0.1', '--port', "$uiPort") (Join-Path $root 'payroll-ui') | Out-Null
}

Wait-Http "http://localhost:$apiPort/ready" 'Backend'
Wait-Http "http://127.0.0.1:$uiPort/" 'Frontend'

Write-Host 'Payroll stack ready.'
Write-Host "UI:      http://127.0.0.1:$uiPort/"
Write-Host "Backend: http://localhost:$apiPort"
