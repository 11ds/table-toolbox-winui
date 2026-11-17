param(
    [string]$Action = 'version',
    [string]$Json = '{}',
    [string]$B64 = ''
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'engine.ps1')
# Primary path: JSON arrives base64-encoded, so quotes / backslashes / non-ASCII
# never touch the command line.
if ($B64) { $Json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($B64)) }
# Fallback: legacy -Json passing turns double quotes into backticks under
# PowerShell 5.1; restore them when no real quote survived.
if (-not $B64 -and $Json.IndexOf('"') -lt 0 -and $Json.IndexOf([char]0x60) -ge 0) {
    $Json = $Json.Replace([char]0x60, '"')
}
$result = Invoke-Api -Action $Action -Json $Json
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Write-Output $result
