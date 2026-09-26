param([string]$RunName='token-hud')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$out=Join-Path $repo ('artifacts\'+$RunName)
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'host\release\CodexToolsHost.exe') -Destination (Join-Path $out 'CodexToolsHost.exe') -Force
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe "/out:$out\TokenHudTests.exe" "/r:$out\CodexToolsHost.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'Runner.cs')
if($LASTEXITCODE -ne 0){throw 'Token HUD tests compile failed'}
& "$out\TokenHudTests.exe" $out
exit $LASTEXITCODE
