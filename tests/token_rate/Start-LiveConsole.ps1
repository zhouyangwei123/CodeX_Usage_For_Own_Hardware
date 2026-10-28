param([string]$Thread, [string]$Python='python')
$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot 'live_console.py'
if($Thread){ & $Python -X utf8 $script --thread $Thread }
else { & $Python -X utf8 $script }
