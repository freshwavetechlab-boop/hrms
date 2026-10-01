function Get-PortProcess([int]$port) {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) { return Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" }
}

function Test-ProcessInTree($process, [int]$rootId) {
    for ($depth = 0; $process -and $depth -lt 16; $depth++) {
        if ($process.ProcessId -eq $rootId) { return $true }
        if (-not $process.ParentProcessId) { break }
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ParentProcessId)"
    }
    return $false
}

function Test-HrmsProcess($process, $job) {
    if (-not $process) { return $false }
    if ($job.Id -eq 'api') {
        $expectedApi = [System.IO.Path]::GetFullPath($job.ProjectDirectory).TrimEnd('\') + '\'
        return ($process.Name -eq 'Payroll.API.exe' -and $process.ExecutablePath -and $process.ExecutablePath.StartsWith($expectedApi, [StringComparison]::OrdinalIgnoreCase)) -or
            ($process.Name -eq 'dotnet.exe' -and $process.CommandLine -match '(?:^|\s|["/\\])Payroll\.API\.(?:dll|csproj)(?:"|\s|$)')
    }
    return $job.Id -in @('ui', 'ess') -and $process.Name -eq 'node.exe' -and
        $process.CommandLine -match 'vite[/\\]bin[/\\]vite\.js' -and
        $process.CommandLine -match "(?:^|\s)--port(?:=|\s+)$($job.Port)(?:\s|$)"
}
