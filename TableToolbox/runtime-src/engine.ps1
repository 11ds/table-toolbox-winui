# TableLens engine: WinRT offline OCR + compiled C# core. Loaded by server.ps1 and tests.
# All processing is local. No HTTP client, no cloud, no external runtime.
$ErrorActionPreference = 'Stop'
$script:AppRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:TempDir = Join-Path $script:AppRoot 'temp'
try {
    if (-not (Test-Path $script:TempDir)) { New-Item -ItemType Directory -Path $script:TempDir -Force | Out-Null }
    $probe = Join-Path $script:TempDir ('.w_' + [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($probe, 'ok')
    Remove-Item $probe -Force
} catch {
    $script:TempDir = Join-Path ([IO.Path]::GetTempPath()) 'TableLens'
    if (-not (Test-Path $script:TempDir)) { New-Item -ItemType Directory -Path $script:TempDir -Force | Out-Null }
}
$script:Version = '1.1.1'
$script:Ready = $false

function Initialize-Engine {
    if ($script:Ready) { return }
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    Add-Type -AssemblyName System.Core
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Xml
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Add-Type -AssemblyName System.Web.Extensions

    $parts = @()
    foreach ($f in @('core.cs', 'book.cs', 'biff.cs')) {
        $parts += [IO.File]::ReadAllText((Join-Path $script:AppRoot $f), [Text.Encoding]::UTF8)
    }
    $raw = ($parts -join "`n")
    $uses = [regex]::Matches($raw, '(?m)^using [^\r\n]+;') | ForEach-Object { $_.Value } | Select-Object -Unique
    $body = [regex]::Replace($raw, '(?m)^using [^\r\n]+;[ \t]*\r?\n', '')
    $src = (($uses -join "`n") + "`n" + $body)
    Add-Type -TypeDefinition $src -ReferencedAssemblies @(
        'System.Core', 'System.Drawing', 'System.Xml',
        'System.IO.Compression', 'System.IO.Compression.FileSystem', 'System.Web.Extensions'
    )

    $script:AsTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]

    [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Globalization.Language, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.BitmapPixelFormat, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Graphics.Imaging.BitmapAlphaMode, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.StorageFile, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.FileAccessMode, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Storage.Streams.IRandomAccessStream, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
    [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime] | Out-Null
    [Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType = WindowsRuntime] | Out-Null
    $script:AsTaskAction = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'
    })[0]
    $script:Ready = $true
}

function Wait-WinRT {
    param($Op, $Type)
    $m = $script:AsTask.MakeGenericMethod($Type)
    $t = $m.Invoke($null, @($Op))
    $t.Wait(-1) | Out-Null
    return $t.Result
}

function Get-OcrLanguages {
    Initialize-Engine
    $list = @()
    foreach ($l in [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages) { $list += $l.LanguageTag }
    return , $list
}

function Get-SystemAccent {
    $accent = '#0078D4'
    try {
        $c = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\DWM' -Name ColorizationColor -ErrorAction Stop).ColorizationColor
        $r = ($c -shr 16) -band 0xFF
        $g = ($c -shr 8) -band 0xFF
        $b = $c -band 0xFF
        if (($r + $g + $b) -gt 60 -and ($r + $g + $b) -lt 720) { $accent = ('#{0:X2}{1:X2}{2:X2}' -f $r, $g, $b) }
    } catch { }
    $theme = 'light'
    try {
        $v = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme -ErrorAction Stop).AppsUseLightTheme
        if ($v -eq 0) { $theme = 'dark' }
    } catch { }
    return @{ accent = $accent; theme = $theme }
}

function Parse-PageSpec {
    param([string]$Spec, [int]$PageCount)
    $all = @()
    if (-not $Spec -or $Spec.Trim().Length -eq 0 -or $Spec.Trim().ToLowerInvariant() -eq 'all') {
        for ($i = 0; $i -lt $PageCount; $i++) { $all += $i }
        return , $all
    }
    $list = @()
    foreach ($part in ($Spec -split ',')) {
        $p = $part.Trim()
        if ($p -match '^(\d+)\s*-\s*(\d+)$') {
            $a = [int]$Matches[1]; $b = [int]$Matches[2]
            if ($a -gt $b) { $t = $a; $a = $b; $b = $t }
            for ($i = $a; $i -le $b; $i++) {
                $idx = $i - 1
                if ($idx -ge 0 -and $idx -lt $PageCount) { $list += $idx }
            }
        }
        elseif ($p -match '^\d+$') {
            $idx = [int]$p - 1
            if ($idx -ge 0 -and $idx -lt $PageCount) { $list += $idx }
        }
    }
    if ($list.Count -eq 0) { $list += 0 }
    return , $list
}

function Convert-PdfPageToPng {
    param([byte[]]$Bytes, [int]$PageIndex, [int]$PageCountHint = 0)
    $tmpPdf = Join-Path $script:TempDir ('pdf_' + [guid]::NewGuid().ToString('N') + '.pdf')
    [IO.File]::WriteAllBytes($tmpPdf, $Bytes)
    $in = $null; $inRas = $null; $out = $null; $outRas = $null; $page = $null
    try {
        $in = [IO.File]::OpenRead($tmpPdf)
        $inRas = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($in)
        $doc = Wait-WinRT ([Windows.Data.Pdf.PdfDocument]::LoadFromStreamAsync($inRas)) ([Windows.Data.Pdf.PdfDocument])
        $count = [int]$doc.PageCount
        if ($PageIndex -lt 0) { $PageIndex = 0 }
        if ($PageIndex -ge $count) { throw ('页码超出范围：第 ' + ($PageIndex + 1) + ' 页，共 ' + $count + ' 页') }
        $page = $doc.GetPage($PageIndex)
        $tmpPng = Join-Path $script:TempDir ('pdfpg_' + [guid]::NewGuid().ToString('N') + '.png')
        $pw = [double]$page.Size.Width
        $ph = [double]$page.Size.Height
        $renderMode = 'native'
        $rendered = $false

        # 1) High-DPI render. Some hosts silently return a blank page with explicit options,
        #    so sample dark pixels before accepting the result.
        try {
            $rops = [Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType = WindowsRuntime]::new()
            $rops.DestinationWidth = [uint32][math]::Round($pw * 2.6)
            $rops.DestinationHeight = [uint32][math]::Round($ph * 2.6)
            $o1 = [IO.File]::Create($tmpPng)
            $r1 = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($o1)
            try {
                $a1 = $page.RenderToStreamAsync($r1, $rops)
                $script:AsTaskAction.Invoke($null, @($a1)).Wait()
            }
            finally { $r1.Dispose(); $o1.Dispose() }
            if (Test-Path $tmpPng) {
                $bmp = [System.Drawing.Bitmap]::FromFile($tmpPng)
                $dark = 0
                for ($y = 0; $y -lt $bmp.Height; $y += 17) {
                    for ($x = 0; $x -lt $bmp.Width; $x += 17) {
                        if ($bmp.GetPixel($x, $y).GetBrightness() -lt 0.6) { $dark++ }
                    }
                }
                $bmp.Dispose()
                if ($dark -gt 3) { $rendered = $true; $renderMode = 'hi-dpi' }
            }
        }
        catch { $rendered = $false }

        # 2) Fall back to native 96-DPI render
        if (-not $rendered) {
            if (Test-Path $tmpPng) { Remove-Item $tmpPng -Force -ErrorAction SilentlyContinue }
            $o2 = [IO.File]::Create($tmpPng)
            $r2 = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($o2)
            try {
                $a2 = $page.RenderToStreamAsync($r2)
                $script:AsTaskAction.Invoke($null, @($a2)).Wait()
            }
            finally { $r2.Dispose(); $o2.Dispose() }
            $renderMode = 'native'
        }
        $png = [IO.File]::ReadAllBytes($tmpPng)
        Remove-Item $tmpPng -Force -ErrorAction SilentlyContinue
        return @{ png = $png; pageCount = $count; width = [int]$pw; height = [int]$ph; pngBytes = $png.Length; renderMode = $renderMode }
    }
    finally {
        if ($null -ne $outRas) { try { $outRas.Dispose() } catch { } }
        if ($null -ne $out) { try { $out.Dispose() } catch { } }
        if ($null -ne $inRas) { try { $inRas.Dispose() } catch { } }
        if ($null -ne $in) { try { $in.Dispose() } catch { } }
        if (Test-Path $tmpPdf) { try { Remove-Item $tmpPdf -Force -ErrorAction SilentlyContinue } catch { } }
    }
}

function Invoke-PdfOcr {
    param([byte[]]$Bytes, [string]$Name, [hashtable]$Options)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $spec = if ($Options.ContainsKey('pages')) { [string]$Options['pages'] } else { '1' }
    $first = $null
    try { $first = Convert-PdfPageToPng -Bytes $Bytes -PageIndex 0 }
    catch { return [TableLens.J]::Err('PDF 解析失败：' + $_.Exception.Message) }
    $pageCount = [int]$first.pageCount
    $idx = Parse-PageSpec -Spec $spec -PageCount $pageCount
    $merged = New-Object 'System.Collections.Generic.List[object]'
    $pageInfo = @()
    $errs = @()
    $cols = -1
    $preview = $null
    $skipped = 0
    $firstOk = $false
    foreach ($i in $idx) {
        # NB: never assign a byte[] through an if-expression - PowerShell enumerates the array.
        $png = $null
        if ($i -eq 0 -and -not $firstOk) { $png = $first.png }
        else { $png = (Convert-PdfPageToPng -Bytes $Bytes -PageIndex $i).png }
        if ($i -eq 0) { $firstOk = $true }
        $one = Invoke-Ocr -Bytes $png -Name ('page' + ($i + 1) + '.png') -Options $Options
        $o = [TableLens.J]::D($one)
        if ($null -eq $o -or -not $o.ContainsKey('ok') -or -not $o['ok']) {
            $errs += ([TableLens.J]::Str($o['error']))
            $skipped++
            continue
        }
        $rows = [TableLens.J]::L($o['rows'])
        $c = 0
        if ($rows.Count -gt 0) { $c = @($rows[0]).Count }
        if ($cols -lt 0) { $cols = $c }
        if ($c -gt 0 -and $c -eq $cols) { foreach ($r in $rows) { $merged.Add($r) } }
        else { $skipped++ }
        if ($null -eq $preview) { $preview = $o['preview'] }
        $pageInfo += @{ page = ($i + 1); rows = $rows.Count; cols = $c }
    }
    $sw.Stop()
    if ($merged.Count -eq 0) {
        $detail = if ($errs.Count -gt 0) { '：' + ($errs -join ' / ') } else { '（未识别到表格线或文字）' }
        return [TableLens.J]::Err('PDF 没有识别出表格内容' + $detail)
    }
    # Build JSON by hand: PowerShell-wrapped PSObjects break the serializer (same reason as the word JSON).
    $sb = New-Object System.Text.StringBuilder
    $null = $sb.Append('{"ok":true,"rows":[')
    $firstRow = $true
    foreach ($r in $merged) {
        if (-not $firstRow) { $null = $sb.Append(',') }
        $firstRow = $false
        $cells = @($r)
        $null = $sb.Append('[')
        for ($i = 0; $i -lt $cells.Count; $i++) {
            if ($i -gt 0) { $null = $sb.Append(',') }
            $null = $sb.Append([TableLens.J]::S([string]$cells[$i]))
        }
        $null = $sb.Append(']')
    }
    $null = $sb.Append('],"rowCount":').Append($merged.Count)
    $null = $sb.Append(',"colCount":').Append($cols)
    $null = $sb.Append(',"mode":"pdf","pageCount":').Append($pageCount)
    $null = $sb.Append(',"pagesUsed":').Append($idx.Count)
    $null = $sb.Append(',"pagesSkipped":').Append($skipped)
    $null = $sb.Append(',"pageInfo":[')
    for ($i = 0; $i -lt $pageInfo.Count; $i++) {
        if ($i -gt 0) { $null = $sb.Append(',') }
        $pi = $pageInfo[$i]
        $null = $sb.Append('{"page":').Append($pi['page']).Append(',"rows":').Append($pi['rows']).Append(',"cols":').Append($pi['cols']).Append('}')
    }
    $null = $sb.Append(']')
    if ($null -ne $preview) { $null = $sb.Append(',"preview":').Append([TableLens.J]::S([string]$preview)) }
    $null = $sb.Append(',"name":').Append([TableLens.J]::S($Name))
    $null = $sb.Append(',"elapsedMs":').Append([int]$sw.ElapsedMilliseconds)
    $null = $sb.Append('}')
    return $sb.ToString()
}

function Convert-WithOffice {
    param([string]$Json)
    $q = [TableLens.J]::D($Json)
    $name = [TableLens.J]::Str($q['name'])
    $bytes = $null
    if ($q.ContainsKey('data') -and [TableLens.J]::Str($q['data']).Length -gt 0) {
        $bytes = [Convert]::FromBase64String([TableLens.J]::Str($q['data']))
    }
    elseif ($q.ContainsKey('path') -and [TableLens.J]::Str($q['path']).Length -gt 0) {
        $p = [TableLens.J]::Str($q['path'])
        if (-not (Test-Path $p)) { return [TableLens.J]::Err('找不到文件：' + $p) }
        $bytes = [IO.File]::ReadAllBytes($p)
        $name = [IO.Path]::GetFileName($p)
    }
    if ($null -eq $bytes) { return [TableLens.J]::Err('没有收到文件内容。') }
    $ext = [IO.Path]::GetExtension($name).ToLowerInvariant()
    if ($ext.Length -eq 0) { $ext = '.doc' }
    $token = [guid]::NewGuid().ToString('N')
    $tmp = Join-Path $script:TempDir ('office_' + $token + $ext)
    $outJson = Join-Path $script:TempDir ('office_' + $token + '.json')
    $helper = Join-Path $script:AppRoot 'office_helper.ps1'
    [IO.File]::WriteAllBytes($tmp, $bytes)
    $proc = $null
    try {
        if (-not (Test-Path $helper)) { return [TableLens.J]::Err('缺少 office_helper.ps1，无法转换 WPS/Word 文档。') }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'powershell.exe'
        $psi.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $helper + '" -In "' + $tmp + '" -Out "' + $outJson + '"'
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $proc = [System.Diagnostics.Process]::Start($psi)
        # Word can hang on a malformed document; never block the server forever.
        if (-not $proc.WaitForExit(120000)) {
            try { $proc.Kill() } catch { }
            return [TableLens.J]::Err('本机 Word 转换超时（120 秒）。可在 WPS 里另存为 .xlsx 再试。')
        }
        if (-not (Test-Path $outJson)) {
            return [TableLens.J]::Err('本机 Word 转换失败（未安装 Microsoft Word，或文档已损坏）。可在 WPS 里另存为 .xlsx 再试。')
        }
        $raw = [IO.File]::ReadAllText($outJson, [Text.Encoding]::UTF8)
        $d = [TableLens.J]::D($raw)
        if ($null -eq $d) { return [TableLens.J]::Err('本机 Word 转换失败：结果无法解析。') }
        if (-not $d.ContainsKey('ok') -or -not $d['ok']) { return [TableLens.J]::S($d) }
        return [TableLens.J]::S(@{ ok = $true; name = $name; sheets = $d['sheets']; via = 'Word' })
    }
    catch {
        return [TableLens.J]::Err('本机 Word 转换出错：' + $_.Exception.Message)
    }
    finally {
        if ($null -ne $proc) { try { $proc.Dispose() } catch { } }
        if (Test-Path $tmp) { try { Remove-Item $tmp -Force -ErrorAction SilentlyContinue } catch { } }
        if (Test-Path $outJson) { try { Remove-Item $outJson -Force -ErrorAction SilentlyContinue } catch { } }
    }
}

function Invoke-Ocr {
    param([byte[]]$Bytes, [string]$Name, [hashtable]$Options)
    Initialize-Engine
    $isPdf = $false
    if ($Name) { $isPdf = ([IO.Path]::GetExtension($Name).ToLowerInvariant() -eq '.pdf') }
    if (-not $isPdf -and $null -ne $Bytes -and $Bytes.Length -gt 5) {
        $isPdf = ($Bytes[0] -eq 0x25 -and $Bytes[1] -eq 0x50 -and $Bytes[2] -eq 0x44 -and $Bytes[3] -eq 0x46)
    }
    if ($isPdf) {
        if ($null -eq $Options) { $Options = @{} }
        return Invoke-PdfOcr -Bytes $Bytes -Name $Name -Options $Options
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $lang = if ($Options.ContainsKey('language') -and $Options['language']) { [string]$Options['language'] } else { 'zh-Hans-CN' }
    $rotate = if ($Options.ContainsKey('rotate')) { [int]$Options['rotate'] } else { 0 }
    $enhance = if ($Options.ContainsKey('enhance')) { [bool]$Options['enhance'] } else { $true }
    $minWidth = if ($Options.ContainsKey('minWidth')) { [int]$Options['minWidth'] } else { 2200 }

    $bmp = $null
    $tmp = Join-Path $script:TempDir ('ocr_' + [guid]::NewGuid().ToString('N') + '.png')
    $stream = $null
    try {
        $bmp = [TableLens.Img]::LoadPrep($Bytes, $rotate, $minWidth, $enhance)
        $w = $bmp.Width; $h = $bmp.Height
        $png = [TableLens.Img]::ToPng($bmp)
        $bgra = [TableLens.Img]::ToBgra($bmp)
        [IO.File]::WriteAllBytes($tmp, $png)
        $bmp.Dispose(); $bmp = $null

        $engine = $null
        try { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language $lang)) } catch { }
        if ($null -eq $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
        if ($null -eq $engine) { return [TableLens.J]::Err('本机没有可用的离线 OCR 语言包：' + $lang) }

        $file = Wait-WinRT ([Windows.Storage.StorageFile]::GetFileFromPathAsync($tmp)) ([Windows.Storage.StorageFile])
        $stream = Wait-WinRT ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Wait-WinRT ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $sb = Wait-WinRT ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied)) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $ocr = Wait-WinRT ($engine.RecognizeAsync($sb)) ([Windows.Media.Ocr.OcrResult])

        # Build word JSON by hand: PowerShell wraps .NET objects added to List[object]
        # in PSObject, which the JavaScriptSerializer cannot reflect.
        $wj = New-Object System.Text.StringBuilder
        $null = $wj.Append('{"words":[')
        $first = $true
        foreach ($line in $ocr.Lines) {
            foreach ($wd in $line.Words) {
                if (-not $first) { $null = $wj.Append(',') }
                $first = $false
                $null = $wj.Append('{"t":').Append([TableLens.J]::S([string]$wd.Text))
                $null = $wj.Append(',"x":').Append([int]$wd.BoundingRect.X)
                $null = $wj.Append(',"y":').Append([int]$wd.BoundingRect.Y)
                $null = $wj.Append(',"w":').Append([int]$wd.BoundingRect.Width)
                $null = $wj.Append(',"h":').Append([int]$wd.BoundingRect.Height).Append('}')
            }
        }
        $null = $wj.Append(']}')
        $wordsJson = $wj.ToString()
        $modeVal = if ($Options.ContainsKey('mode')) { [string]$Options['mode'] } else { 'auto' }
        $rowsVal = if ($Options.ContainsKey('rows')) { [int]$Options['rows'] } else { 0 }
        $colsVal = if ($Options.ContainsKey('cols')) { [int]$Options['cols'] } else { 0 }
        $inkVal  = if ($Options.ContainsKey('ink')) { [double]$Options['ink'] } else { 0.30 }
        $optJson = '{"mode":"' + $modeVal + '","rows":' + $rowsVal + ',"cols":' + $colsVal + ',"ink":' + $inkVal.ToString([Globalization.CultureInfo]::InvariantCulture) + '}'
        $result = [TableLens.Table]::Analyze($bgra, $w, $h, $wordsJson, $optJson)
        $d = [TableLens.J]::D($result)
        if ($null -ne $d -and $d.ContainsKey('ok') -and $d['ok']) {
            $d['preview'] = [Convert]::ToBase64String($png)
            $d['language'] = [string]$engine.RecognizerLanguage.LanguageTag
            $d['name'] = $Name
            $d['prepWidth'] = $w
            $d['prepHeight'] = $h
        }
        $sw.Stop()
        if ($null -ne $d) { $d['elapsedMs'] = [int]$sw.ElapsedMilliseconds }
        return [TableLens.J]::S($d)
    }
    catch {
        return [TableLens.J]::Err('识别失败：' + $_.Exception.Message + ' @ ' + $_.ScriptStackTrace)
    }
    finally {
        if ($null -ne $stream) { try { $stream.Dispose() } catch { } }
        if ($null -ne $bmp) { try { $bmp.Dispose() } catch { } }
        if (Test-Path $tmp) { try { Remove-Item $tmp -Force -ErrorAction SilentlyContinue } catch { } }
    }
}

function Invoke-Api {
    param([string]$Action, [string]$Json)
    Initialize-Engine
    if (-not $Json -or $Json.Trim().Length -eq 0) { $Json = '{}' }
    switch ($Action) {
        'version' {
            return [TableLens.J]::S(@{ ok = $true; version = $script:Version; offline = $true; engine = 'Windows.Media.Ocr (built-in, offline)' })
        }
        'env' {
            $sys = Get-SystemAccent
            return [TableLens.J]::S(@{
                ok = $true; version = $script:Version
                accent = $sys.accent; theme = $sys.theme
                languages = (Get-OcrLanguages)
                offline = $true
            })
        }
        'languages' { return [TableLens.J]::S(@{ ok = $true; languages = (Get-OcrLanguages) }) }
        'ocr' {
            $q = [TableLens.J]::D($Json)
            $name = [TableLens.J]::Str($q['name'])
            $bytes = $null
            if ($q.ContainsKey('data') -and [TableLens.J]::Str($q['data']).Length -gt 0) {
                try { $bytes = [Convert]::FromBase64String([TableLens.J]::Str($q['data'])) }
                catch { return [TableLens.J]::Err('图片数据不是有效的 base64。') }
            }
            elseif ($q.ContainsKey('path') -and [TableLens.J]::Str($q['path']).Length -gt 0) {
                $p = [TableLens.J]::Str($q['path'])
                if (-not (Test-Path $p)) { return [TableLens.J]::Err('找不到图片：' + $p) }
                $bytes = [IO.File]::ReadAllBytes($p); $name = [IO.Path]::GetFileName($p)
            }
            else { return [TableLens.J]::Err('没有收到图片。') }
            $opt = @{}
            if ($q.ContainsKey('options')) {
                $o = $q['options'] -as [System.Collections.IDictionary]
                if ($null -ne $o) { foreach ($k in $o.Keys) { $opt[[string]$k] = $o[$k] } }
            }
            return Invoke-Ocr -Bytes $bytes -Name $name -Options $opt
        }
        'read' {
            $r = [TableLens.Book]::Read($Json)
            $d = [TableLens.J]::D($r)
            if ($null -ne $d -and $d.ContainsKey('ok') -and -not $d['ok'] -and $d.ContainsKey('error')) {
                $e = [TableLens.J]::Str($d['error'])
                if ($e -eq 'NEEDS_OFFICE') { return Convert-WithOffice -Json $Json }
                if ($e -eq 'NEEDS_PDF') {
                    $q = [TableLens.J]::D($Json)
                    $name = [TableLens.J]::Str($q['name'])
                    $bytes = $null
                    if ($q.ContainsKey('data') -and [TableLens.J]::Str($q['data']).Length -gt 0) {
                        try { $bytes = [Convert]::FromBase64String([TableLens.J]::Str($q['data'])) }
                        catch { return [TableLens.J]::Err('PDF 数据不是有效的 base64。') }
                    }
                    elseif ($q.ContainsKey('path') -and [TableLens.J]::Str($q['path']).Length -gt 0) {
                        $p = [TableLens.J]::Str($q['path'])
                        if (-not (Test-Path $p)) { return [TableLens.J]::Err('找不到文件：' + $p) }
                        $bytes = [IO.File]::ReadAllBytes($p)
                        $name = [IO.Path]::GetFileName($p)
                    }
                    if ($null -eq $bytes) { return [TableLens.J]::Err('没有收到 PDF 内容。') }
                    $opt = @{ pages = '1' }
                    if ($q.ContainsKey('pages')) { $opt['pages'] = [TableLens.J]::Str($q['pages']) }
                    $ocr = Invoke-Ocr -Bytes $bytes -Name $name -Options $opt
                    $od = [TableLens.J]::D($ocr)
                    if ($null -eq $od -or -not $od.ContainsKey('ok') -or -not $od['ok']) { return $ocr }
                    return [TableLens.J]::S(@{
                            ok = $true
                            name = $name
                            via = 'PDF + 系统 OCR'
                            sheets = @(@{
                                    name = ('PDF ' + [TableLens.J]::Str($od['pagesUsed']) + ' 页')
                                    rows = $od['rows']
                                    rowCount = [TableLens.J]::Int($od['rowCount'], 0)
                                    colCount = [TableLens.J]::Int($od['colCount'], 0)
                                })
                        })
                }
            }
            return $r
        }
        'diff' { return [TableLens.DiffX]::Compare($Json) }
        'export' {
            $q = [TableLens.J]::D($Json)
            try {
                $bytes = [TableLens.Export]::Run($Json)
                $kind = [TableLens.J]::Str($q['kind']); $format = [TableLens.J]::Str($q['format'])
                if ($format.Length -eq 0) { $format = 'xlsx' }
                $base = [TableLens.J]::Str($q['name']); if ($base.Length -eq 0) { $base = 'TableLens' }
                $base = [IO.Path]::GetFileNameWithoutExtension($base)
                return [TableLens.J]::S(@{
                    ok = $true; data = [Convert]::ToBase64String($bytes)
                    name = $base + '.' + $(if ($format -eq 'csv' -or $kind -eq 'csv') { 'csv' } else { 'xlsx' })
                    size = $bytes.Length
                })
            }
            catch { return [TableLens.J]::Err('导出失败：' + $_.Exception.Message) }
        }
        default { return [TableLens.J]::Err('未知接口：' + $Action) }
    }
}
