param([int]$Seconds = 7200, [string]$RunName = 'host-soak')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo ('artifacts\' + $RunName)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$hostExe = Join-Path $out 'CodexToolsHost.exe'
Copy-Item -LiteralPath (Join-Path $repo 'host\release\CodexToolsHost.exe') -Destination $hostExe -Force
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe "/out:$out\LiveSoak.exe" "/r:$hostExe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll (Join-Path $repo 'tests\quota_reliability\LiveSoak.cs')
if ($LASTEXITCODE -ne 0) { throw 'Soak runner compilation failed' }
(Get-FileHash -LiteralPath $hostExe -Algorithm SHA256).Hash | Set-Content -LiteralPath (Join-Path $out 'assembly.sha256')
& "$out\LiveSoak.exe" $Seconds $out
exit $LASTEXITCODE
