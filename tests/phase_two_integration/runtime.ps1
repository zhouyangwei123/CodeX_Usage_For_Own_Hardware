param([int]$Seconds = 180, [string]$RunName = 'phase-two-runtime', [switch]$Unpinned)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$out = Join-Path $repo ('artifacts\' + $RunName)
New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'host\release\CodexToolsHost.exe') -Destination (Join-Path $out 'CodexToolsHost.exe') -Force
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe "/out:$out\Runtime.exe" "/r:$out\CodexToolsHost.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'Runtime.cs')
if ($LASTEXITCODE -ne 0) { throw 'Runtime integration compilation failed.' }
(Get-FileHash -LiteralPath (Join-Path $out 'CodexToolsHost.exe') -Algorithm SHA256).Hash | Set-Content -LiteralPath (Join-Path $out 'assembly.sha256')
$mode=if($Unpinned){'unpinned'}else{'default'}
& "$out\Runtime.exe" $out $Seconds $mode
exit $LASTEXITCODE
