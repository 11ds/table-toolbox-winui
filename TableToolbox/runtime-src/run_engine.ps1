param(
    [string]$Action = 'version',
    [string]$Json = '{}'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'engine.ps1')
$result = Invoke-Api -Action $Action -Json $Json
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Write-Output $result
