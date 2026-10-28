param([Parameter(Mandatory=$true)][string]$Thread, [string]$Python='python')
$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot 'live_console.py'
& $Python -X utf8 $script --thread $Thread
