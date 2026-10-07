param([Parameter(Mandatory=$true)][string]$InputPdf)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime] | Out-Null
[Windows.Data.Pdf.PdfDocument,Windows.Data.Pdf,ContentType=WindowsRuntime] | Out-Null
[Windows.Storage.Streams.InMemoryRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime] | Out-Null
$taskMethod = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$actionMethod = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and -not $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
function Await-Result($Operation, [Type]$ResultType) {
    $task = $taskMethod.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.GetAwaiter().GetResult()
}
function Await-Action($Operation) {
    $task = $actionMethod.Invoke($null, @($Operation))
    [void]$task.GetAwaiter().GetResult()
}
try {
    $file = Await-Result ([Windows.Storage.StorageFile]::GetFileFromPathAsync([IO.Path]::GetFullPath($InputPdf))) ([Windows.Storage.StorageFile])
    $pdf = Await-Result ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
    if ($pdf.PageCount -gt 5) { throw 'Document exceeds bounded OCR page limit.' }
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    if ($null -eq $engine) { throw 'No installed OCR recognition language.' }
    $text = New-Object System.Text.StringBuilder
    for ($pageNumber = 0; $pageNumber -lt $pdf.PageCount; $pageNumber++) {
        $page = $pdf.GetPage([uint32]$pageNumber)
        $stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
        $bitmap = $null
        try {
            $options = New-Object Windows.Data.Pdf.PdfPageRenderOptions
            $scale = [Math]::Min(2200, [Windows.Media.Ocr.OcrEngine]::MaxImageDimension) / [Math]::Max($page.Size.Width, $page.Size.Height)
            $options.DestinationWidth = [uint32]($page.Size.Width * $scale)
            $options.DestinationHeight = [uint32]($page.Size.Height * $scale)
            Await-Action ($page.RenderToStreamAsync($stream, $options))
            $decoder = Await-Result ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $bitmap = Await-Result ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
            $result = Await-Result ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
            # WinRT can return table columns in reading order. Reassemble physical
            # rows from word coordinates so "Position Name | Platform Engineer"
            # stays together instead of separating all labels from their values.
            $lines = foreach ($line in $result.Lines) {
                $top = ($line.Words | ForEach-Object { $_.BoundingRect.Y } | Measure-Object -Minimum).Minimum
                $bottom = ($line.Words | ForEach-Object { $_.BoundingRect.Y + $_.BoundingRect.Height } | Measure-Object -Maximum).Maximum
                [pscustomobject]@{ Center=($top+$bottom)/2; Height=$bottom-$top; Words=@($line.Words) }
            }
            $rowWords = New-Object System.Collections.Generic.List[object]
            $rowCenter = -1000; $rowHeight = 0
            foreach ($line in ($lines | Sort-Object Center)) {
                if ($rowWords.Count -gt 0 -and [Math]::Abs($line.Center-$rowCenter) -gt [Math]::Max(5,[Math]::Min($rowHeight,$line.Height)*0.6)) {
                    [void]$text.AppendLine((($rowWords | Sort-Object { $_.BoundingRect.X } | ForEach-Object { $_.Text }) -join ' '))
                    $rowWords.Clear()
                }
                if ($rowWords.Count -eq 0) { $rowCenter=$line.Center; $rowHeight=$line.Height }
                foreach ($word in $line.Words) { $rowWords.Add($word) }
            }
            if ($rowWords.Count -gt 0) { [void]$text.AppendLine((($rowWords | Sort-Object { $_.BoundingRect.X } | ForEach-Object { $_.Text }) -join ' ')) }
        } finally {
            if ($bitmap -is [IDisposable]) { $bitmap.Dispose() }
            if ($stream -is [IDisposable]) { $stream.Dispose() }
            if ($page -is [IDisposable]) { $page.Dispose() }
        }
    }
    [Console]::Write($text.ToString())
} catch {
    # Do not expose document paths/content or detailed native errors in telemetry.
    [Console]::Error.WriteLine('Windows OCR could not read this PDF. Check installed OCR language support and page limits.')
    [Console]::Error.WriteLine(('Failure type: {0}; helper line: {1}' -f $_.Exception.GetType().Name, $_.InvocationInfo.ScriptLineNumber))
    exit 2
}
