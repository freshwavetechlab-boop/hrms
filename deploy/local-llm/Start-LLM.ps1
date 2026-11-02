#requires -Version 5.1
[CmdletBinding()]
param([switch]$LibraryOnly)
Set-StrictMode -Version 2.0
$ErrorActionPreference='Stop'
$script:LlmRoot='E:\LLM'
$script:LlmRuntime='E:\LLM\runtime\b10964'
$script:LlmStatePath='E:\LLM\logs\active-process.json'
$script:LlmEncoding=New-Object Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Net.Http

function Assert-LLMTarget {
    if($env:COMPUTERNAME -ne 'DCDB1'){throw 'This script runs only on DCDB1.'}
    $os=Get-CimInstance Win32_OperatingSystem
    if($os.Caption -notmatch 'Windows Server 2022 Standard'){throw 'Unexpected operating system.'}
    if(@(Get-NetIPAddress -AddressFamily IPv4 | Where-Object IPAddress -eq '10.10.91.100').Count -ne 1){throw 'Unexpected target IP.'}
}

function ConvertTo-LLMWindowsArgument([string]$Value) {
    '"'+[regex]::Replace([regex]::Replace($Value,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"'
}

function Save-LLMState($State) {
    [IO.File]::WriteAllText($script:LlmStatePath,($State | ConvertTo-Json -Depth 7),$script:LlmEncoding)
}

function Get-LLMOwnedProcesses($State) {
    $owned=@()
    foreach($identity in @($State.Processes)) {
        $p=Get-Process -Id ([int]$identity.Id) -ErrorAction SilentlyContinue
        if(-not $p){continue}
        $llmActualUtc=$p.StartTime.ToUniversalTime()
        $llmSavedUtc=[DateTime]::Parse($identity.StartUtc).ToUniversalTime()
        $llmActualPath=$p.Path
        # A newly created process can briefly have no module path. Never relax identity checks.
        if($llmActualUtc.Ticks -eq $llmSavedUtc.Ticks -and [string]::IsNullOrWhiteSpace($llmActualPath)){
            for($llmPathAttempt=0;$llmPathAttempt -lt 25;$llmPathAttempt++){
                Start-Sleep -Milliseconds 200
                $p=Get-Process -Id ([int]$identity.Id) -ErrorAction SilentlyContinue
                if(-not $p){break}
                $llmActualUtc=$p.StartTime.ToUniversalTime()
                $llmActualPath=$p.Path
                if($llmActualUtc.Ticks -ne $llmSavedUtc.Ticks -or -not [string]::IsNullOrWhiteSpace($llmActualPath)){break}
            }
            if(-not $p){continue}
        }
        if($llmActualUtc.Ticks -ne $llmSavedUtc.Ticks -or $llmActualPath -ine $identity.Path){
            throw ('PID identity mismatch; refusing to operate on an unrelated process. Id={0}; ActualUtc={1}; SavedUtc={2}; ActualPath=<{3}>; SavedPath=<{4}>; SavedType={5}; Account={6}; Is64Bit={7}' -f $identity.Id,$llmActualUtc.ToString('o'),$llmSavedUtc.ToString('o'),$llmActualPath,$identity.Path,$identity.StartUtc.GetType().FullName,[Security.Principal.WindowsIdentity]::GetCurrent().Name,[Environment]::Is64BitProcess)
        }
        $owned+=$p
    }
    # Discover only descendants of still-verified processes within the pinned runtime.
    foreach($parent in @($owned)) {
        foreach($child in @(Get-CimInstance Win32_Process -Filter ('ParentProcessId='+$parent.Id))) {
            if(@($State.Processes | Where-Object Id -eq $child.ProcessId).Count){continue}
            $p=Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
            if($p -and $p.Path -and $p.Path.StartsWith($script:LlmRuntime+'\',[StringComparison]::OrdinalIgnoreCase) -and $p.StartTime -ge $parent.StartTime){
                $State.Processes+= [pscustomobject]@{Id=$p.Id;Path=$p.Path;StartUtc=$p.StartTime.ToUniversalTime().ToString('o')}
                $owned+=$p
                Save-LLMState $State
            }
        }
    }
    $owned
}

function Stop-LLMOwned($State) {
    $enabled=Join-Path $script:LlmRoot 'api\enabled'
    if(Test-Path -LiteralPath $enabled){[IO.File]::WriteAllText($enabled,'disabled',$script:LlmEncoding)}
    $owned=@(Get-LLMOwnedProcesses $State)
    foreach($p in @($owned | Sort-Object StartTime -Descending)) {
        if(-not $p.HasExited){$p.Kill();if(-not $p.WaitForExit(15000)){throw 'Owned LLM process did not stop.'}}
    }
    if(@(Get-LLMOwnedProcesses $State).Count){throw 'LLM process remained active.'}
    $listeners=@(Get-NetTCPConnection -State Listen | Where-Object LocalPort -eq 8091)
    if($listeners.Count){throw 'Port 8091 is still occupied; no unrelated process was stopped.'}
    $State.StoppedUtc=[DateTime]::UtcNow.ToString('o')
    Save-LLMState $State
    [pscustomobject]@{Stopped=$true;Port8091Free=$true;ProcessIds=@($State.Processes | ForEach-Object Id)}
}

function Get-LLMResources($State) {
    $owned=@(Get-LLMOwnedProcesses $State)
    if($owned.Count -eq 0){throw 'The LLM process exited.'}
    $os=Get-CimInstance Win32_OperatingSystem
    $cpu=Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
    $ws=($owned | Measure-Object WorkingSet64 -Sum).Sum
    $r=[pscustomobject]@{Utc=[DateTime]::UtcNow.ToString('o');HostCpuPercent=[int]$cpu.PercentProcessorTime;
        FreeMemoryGB=[math]::Round($os.FreePhysicalMemory/1MB,3);ProcessWorkingSetGB=[math]::Round($ws/1GB,3);
        ProcessCpuSeconds=[math]::Round(($owned | Measure-Object CPU -Sum).Sum,3);ProcessIds=@($owned | ForEach-Object Id)}
    if($r.FreeMemoryGB -lt 16 -or $r.ProcessWorkingSetGB -gt 8){throw 'LLM memory safety threshold exceeded.'}
    if($r.HostCpuPercent -gt 85){
        if(-not $script:LlmHighCpuSince){$script:LlmHighCpuSince=[DateTime]::UtcNow}
        if(([DateTime]::UtcNow-$script:LlmHighCpuSince).TotalSeconds -ge 30){throw 'Host CPU exceeded 85 percent for 30 seconds.'}
    }else{$script:LlmHighCpuSince=$null}
    $r
}

function Get-LLMWebsiteHealth {
    if((Get-Service -Name 'Apache2.4').Status -ne 'Running'){throw 'Apache is not running.'}
    $pages=@()
    foreach($lang in @('en','hi')) {
        # Existing HTTP listener: routing/service check only, not page content.
        # Full public HTTPS page/marker checks run from the management PC at deployment.
        # No TLS validation setting is disabled to work around offline CRL access.
        $output=@(& 'C:\Windows\System32\curl.exe' --silent --noproxy '*' --connect-timeout 2 --max-time 5 --header 'Host: eeslindia.org' --output NUL --write-out "LLM_HEALTH_META:%{http_code}|%{time_total}|%{redirect_url}" ('http://127.0.0.1/'+$lang+'/'))
        $exit=$LASTEXITCODE
        $parts=($output -join "`n") -split 'LLM_HEALTH_META:',2
        if($exit -ne 0 -or $parts.Count -ne 2){throw ('Website '+$lang+' health request failed.')}
        $meta=$parts[1].Trim() -split '\|'
        $pages+=[pscustomobject]@{Language=$lang;Status=[int]$meta[0];Seconds=[double]::Parse($meta[1],[Globalization.CultureInfo]::InvariantCulture);Redirect=$meta[2];CheckType='Local HTTP routing only'}
    }
    $pages
}

function Assert-LLMWebsiteHealth($Baseline) {
    $pages=@(Get-LLMWebsiteHealth)
    foreach($before in @($Baseline)) {
        $after=@($pages | Where-Object Language -eq $before.Language)[0]
        if($after.Status -ne 301 -or $after.Redirect -ne ('https://eeslindia.org/'+$after.Language+'/')){throw 'Production local HTTP routing regressed.'}
        $threshold=[math]::Max(15,2*$before.Seconds)
        if($after.Seconds -gt $threshold){
            $script:LlmSlowPages[$after.Language]++
            if($script:LlmSlowPages[$after.Language] -ge 2){throw 'Production website latency repeatedly exceeded its baseline threshold.'}
        }else{$script:LlmSlowPages[$after.Language]=0}
    }
    $pages
}

function Get-LLMLogOffsets {
    @(Get-Item -LiteralPath 'C:\Apache24\logs\error.log','C:\Apache24\logs\eeslindia-ssl-error.log' | ForEach-Object {
        [pscustomobject]@{Path=$_.FullName;Offset=[long]$_.Length}
    })
}

function Test-LLMNewErrors($Offsets) {
    foreach($entry in @($Offsets)) {
        $s=[IO.File]::Open($entry.Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try {
            if($s.Length -lt $entry.Offset){$entry.Offset=0}
            if($s.Length-$entry.Offset -gt 1048576){throw 'Log growth exceeds the monitoring scan budget.'}
            [void]$s.Seek($entry.Offset,[IO.SeekOrigin]::Begin)
            $reader=New-Object IO.StreamReader($s)
            $text=$reader.ReadToEnd()
            $entry.Offset=$s.Position
            $reader.Dispose()
            $fatal=[regex]::Matches($text,'(?im)PHP (?:Fatal|Parse) error|Uncaught (?:Error|Exception)').Count
            $errors=[regex]::Matches($text,'(?im)\[[^\]\r\n]*:(?:error|crit|alert|emerg)\]').Count
            if($fatal){throw 'New PHP/Apache fatal marker detected.'}
            [pscustomobject]@{Path=$entry.Path;NewFatalMarkers=$fatal;NewErrorMarkers=$errors;Offset=$entry.Offset}
        } finally {$s.Dispose()}
    }
}

function New-LLMHttpClient([int]$Timeout=90) {
    $handler=New-Object Net.Http.HttpClientHandler
    $handler.UseProxy=$false;$handler.AllowAutoRedirect=$false
    $client=New-Object Net.Http.HttpClient($handler)
    $client.Timeout=[TimeSpan]::FromSeconds($Timeout)
    $client.MaxResponseContentBufferSize=131072
    $key=[IO.File]::ReadAllText('E:\LLM\secrets\backend-api-key.txt').Trim()
    if($key -notmatch '^[a-f0-9]{64}$'){throw 'Backend key is invalid.'}
    $client.DefaultRequestHeaders.Authorization=New-Object Net.Http.Headers.AuthenticationHeaderValue('Bearer',$key)
    $key=$null
    $client
}

function Wait-LLMReady($State) {
    $client=New-LLMHttpClient 5
    $watch=[Diagnostics.Stopwatch]::StartNew()
    try {
        while($watch.Elapsed.TotalSeconds -lt 600) {
            $null=Get-LLMResources $State
            $code=0
            try {
                $reply=$client.GetAsync('http://127.0.0.1:8091/health').GetAwaiter().GetResult()
                try{$code=[int]$reply.StatusCode}finally{$reply.Dispose()}
            }catch{if(@(Get-LLMOwnedProcesses $State).Count -eq 0){throw 'Model process exited while loading.'}}
            if($code -eq 200){
                $listeners=@(Get-NetTCPConnection -State Listen | Where-Object LocalPort -eq 8091)
                $ids=@($State.Processes | ForEach-Object Id)
                if($listeners.Count -ne 1 -or $listeners[0].LocalAddress -ne '127.0.0.1' -or $ids -notcontains $listeners[0].OwningProcess){throw 'Unexpected model listener identity or binding.'}
                return [math]::Round(([DateTime]::UtcNow-[DateTime]::Parse($State.Processes[0].StartUtc).ToUniversalTime()).TotalSeconds,3)
            }
            if($code -ne 0 -and $code -ne 503){throw ('Unexpected model health status: '+$code)}
            Start-Sleep -Seconds 2
        }
        throw 'Model load exceeded 600 seconds.'
    }finally{$client.Dispose()}
}

$script:LlmHighCpuSince=$null
$script:LlmSlowPages=@{en=0;hi=0}
if($LibraryOnly){return}
Assert-LLMTarget
if(Test-Path -LiteralPath $script:LlmStatePath){
    $old=Get-Content -LiteralPath $script:LlmStatePath -Raw | ConvertFrom-Json
    if(@(Get-LLMOwnedProcesses $old).Count){throw 'An owned LLM process is already active.'}
}
if(@(Get-NetTCPConnection -State Listen | Where-Object LocalPort -eq 8091).Count){throw 'Port 8091 is already occupied.'}
$free=(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB
if($free -lt 16){throw 'Insufficient free memory.'}
$exe=Join-Path $script:LlmRuntime 'llama-server.exe'
$arguments=[string[]](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'launch-arguments.json') -Raw | ConvertFrom-Json)
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'AdapterPolicy.ps1')) {
    . (Join-Path $PSScriptRoot 'AdapterPolicy.ps1')
    $approvedAdapter = Get-LLMAdapterSelection
    if ($approvedAdapter) {
        if ((Get-FileHash -LiteralPath 'E:\LLM\Qwen3.5-4B-Q4_K_M.gguf').Hash -ine $approvedAdapter.BaseSha256) { throw 'Approved adapter base model hash mismatch.' }
        $arguments += @('--lora',$approvedAdapter.Path)
    }
}
$help=[IO.File]::ReadAllText('E:\LLM\logs\llama-help.txt')
foreach($flag in @($arguments | Where-Object { $_ -like '--*' })){
    if($help -notmatch ('(?<![\w-])'+[regex]::Escape($flag)+'(?=[\s,=]|$)')){throw ('Unsupported model option: '+$flag)}
}
$stamp=[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$stdout='E:\LLM\logs\llama-'+$stamp+'.stdout.log'
$stderr='E:\LLM\logs\llama-'+$stamp+'.stderr.log'
$environmentBackup=@{}
$process=$null
try {
    # Process-only changes, restored immediately. No machine/user environment writes.
    foreach($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(LLAMA_|HF_|GGML_)' })){
        $environmentBackup[$item.Name]=$item.Value
        [Environment]::SetEnvironmentVariable($item.Name,$null,'Process')
    }
    $argLine=(@($arguments | ForEach-Object {ConvertTo-LLMWindowsArgument ([string]$_)}) -join ' ')
    $process=Start-Process -FilePath $exe -ArgumentList $argLine -WorkingDirectory $script:LlmRuntime -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $process.PriorityClass=[Diagnostics.ProcessPriorityClass]::BelowNormal
    # Start-Process can leave its returned object's Path unset; pin the executable
    # we launched and validate it against a fresh Get-Process before every operation.
    $state=[pscustomobject]@{Processes=@([pscustomobject]@{Id=$process.Id;Path=$exe;StartUtc=$process.StartTime.ToUniversalTime().ToString('o')});Stdout=$stdout;Stderr=$stderr;StartedUtc=[DateTime]::UtcNow.ToString('o');StoppedUtc=$null}
    Save-LLMState $state
    $state
}catch{
    if($process -and -not $process.HasExited){$process.Kill();$process.WaitForExit()}
    throw
}finally{
    foreach($name in $environmentBackup.Keys){[Environment]::SetEnvironmentVariable($name,$environmentBackup[$name],'Process')}
}
