param([string]$RunName='hud-activation')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$out=Join-Path $repo ('artifacts\'+$RunName)
New-Item -ItemType Directory -Force -Path $out | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'host\release\CodexToolsHost.exe') -Destination (Join-Path $out 'CodexToolsHost.exe') -Force
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe "/out:$out\HudActivationTests.exe" "/r:$out\CodexToolsHost.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'Runner.cs')
if($LASTEXITCODE -ne 0){throw 'Activation harness compile failed'}
& "$out\HudActivationTests.exe" $out | Tee-Object -FilePath (Join-Path $out 'results.txt')
exit $LASTEXITCODE
