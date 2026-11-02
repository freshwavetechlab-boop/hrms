#requires -Version 5.1
param([ValidateSet('Dispatch')][string]$Action='Dispatch')
$ErrorActionPreference='Stop'
. 'E:\LLM\scripts\Start-LLM.ps1' -LibraryOnly
Assert-LLMTarget
$handler = New-Object Net.Http.HttpClientHandler
$handler.UseProxy=$false; $handler.AllowAutoRedirect=$true; $handler.MaxAutomaticRedirections=5
$client = New-Object Net.Http.HttpClient($handler)
$client.Timeout=[TimeSpan]::FromSeconds(7)
$urls=@('https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip',
    'https://huggingface.co/Qwen/Qwen3.5-4B/resolve/851bf6e806efd8d0a36b00ddf55e13ccb7b8cd0a/model.safetensors-00002-of-00002.safetensors')
try {
    $pending=@($urls | ForEach-Object { $message=New-Object Net.Http.HttpRequestMessage([Net.Http.HttpMethod]::Head,$_); $client.SendAsync($message,[Net.Http.HttpCompletionOption]::ResponseHeadersRead) })
    $results=for($i=0;$i -lt $pending.Count;$i++) {
        try { $reply=$pending[$i].GetAwaiter().GetResult(); @{Host=([Uri]$urls[$i]).Host;Status=[int]$reply.StatusCode;FinalHost=$reply.RequestMessage.RequestUri.Host}; $reply.Dispose() }
        catch { @{Host=([Uri]$urls[$i]).Host;Status='Unavailable';FailureType=$_.Exception.GetType().Name} }
    }
    . 'E:\LLM\scripts\RecoveryPolicy.ps1'
    $workerMetrics = $null
    $trainingStatus = 'E:\LLM\training\state\training-status.json'
    if (Test-Path -LiteralPath $trainingStatus) {
        $saved = Get-Content -LiteralPath $trainingStatus -Raw | ConvertFrom-Json
        if ($saved.pid -is [int] -or $saved.pid -is [long]) {
            $ownedWorker = Get-Process -Id ([int]$saved.pid) -ErrorAction SilentlyContinue
            if ($ownedWorker -and $ownedWorker.Path -ieq 'E:\LLM\training\python\python.exe') {
                $workerMetrics = @{Running=$true;Pid=$ownedWorker.Id;CpuSeconds=[math]::Round($ownedWorker.CPU,2);
                    WorkingSetGB=[math]::Round($ownedWorker.WorkingSet64/1GB,2);StartUtc=$ownedWorker.StartTime.ToUniversalTime().ToString('o')}
            } else { $workerMetrics = @{Running=$false} }
        }
    }
    Write-LLMAtomicJson 'E:\LLM\logs\training-network-probe.json' @{Utc=[DateTime]::UtcNow.ToString('o');Results=@($results);Proxy='ProcessOnlyDirect';Worker=$workerMetrics}
    '{"code":"training_network_probed"}'
} finally { $client.Dispose() }
