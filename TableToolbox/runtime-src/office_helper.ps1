# TableLens office helper - isolated child process.
# Word COM must NEVER run inside the main engine process: creating a Word application
# object leaves WinRT OCR broken for the rest of the process lifetime (RecognizeAsync
# keeps returning zero words on images that OCR fine before the COM call).
# This helper runs in its own powershell.exe, writes a JSON file, and exits.
param(
    [Parameter(Mandatory = $true)][string]$In,
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'

function Esc([string]$s) {
    if ($null -eq $s) { return '""' }
    $sb = New-Object System.Text.StringBuilder
    $null = $sb.Append('"')
    foreach ($ch in $s.ToCharArray()) {
        $code = [int]$ch
        if ($ch -eq '"') { $null = $sb.Append('\"') }
        elseif ($ch -eq '\') { $null = $sb.Append('\\') }
        elseif ($code -eq 8) { $null = $sb.Append('\b') }
        elseif ($code -eq 12) { $null = $sb.Append('\f') }
        elseif ($code -eq 10) { $null = $sb.Append('\n') }
        elseif ($code -eq 13) { $null = $sb.Append('\r') }
        elseif ($code -eq 9) { $null = $sb.Append('\t') }
        elseif ($code -lt 32) { $null = $sb.Append(('\u{0:x4}' -f $code)) }
        else { $null = $sb.Append($ch) }
    }
    $null = $sb.Append('"')
    return $sb.ToString()
}

function Write-Result([string]$json) {
    [IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
}

$app = $null
$doc = $null
try {
    $app = New-Object -ComObject Word.Application
    $app.Visible = $false
    $app.DisplayAlerts = 0
    try { $app.Options.SaveInterval = 0 } catch { }
    $doc = $app.Documents.Open($In, $false, $true)

    $sb = New-Object System.Text.StringBuilder
    $null = $sb.Append('{"ok":true,"via":"Word","sheets":[')
    $n = [int]$doc.Tables.Count
    $written = 0
    for ($t = 1; $t -le $n; $t++) {
        $tbl = $doc.Tables.Item($t)
        $rc = [int]$tbl.Rows.Count
        $cc = [int]$tbl.Columns.Count
        if ($rc -le 0 -or $cc -le 0 -or $rc -gt 2000 -or $cc -gt 128) { continue }
        if ($written -gt 0) { $null = $sb.Append(',') }
        $written++
        $null = $sb.Append('{"name":').Append((Esc ('表格' + $t)))
        $null = $sb.Append(',"rowCount":').Append($rc).Append(',"colCount":').Append($cc)
        $null = $sb.Append(',"rows":[')
        for ($r = 1; $r -le $rc; $r++) {
            if ($r -gt 1) { $null = $sb.Append(',') }
            $null = $sb.Append('[')
            for ($c = 1; $c -le $cc; $c++) {
                if ($c -gt 1) { $null = $sb.Append(',') }
                $txt = ''
                try { $txt = [string]$tbl.Cell($r, $c).Range.Text } catch { $txt = '' }
                $txt = $txt -replace "[\r\n\a\x07]", ''
                $null = $sb.Append((Esc $txt))
            }
            $null = $sb.Append(']')
        }
        $null = $sb.Append(']}')
    }
    $null = $sb.Append(']}')
    if ($written -eq 0) {
        Write-Result '{"ok":false,"error":"这个 WPS/Word 文档里没有表格，无法转成电子表格。"}'
    }
    else {
        Write-Result $sb.ToString()
    }
}
catch {
    $msg = '本机 Word 打不开这个 WPS 文档：' + $_.Exception.Message + '。可在 WPS 里打开后另存为 .xlsx 再试。'
    Write-Result ('{"ok":false,"error":' + (Esc $msg) + '}')
}
finally {
    if ($null -ne $doc) { try { $doc.Close($false) } catch { } }
    if ($null -ne $app) { try { $app.Quit() } catch { } }
    [GC]::Collect()
}
