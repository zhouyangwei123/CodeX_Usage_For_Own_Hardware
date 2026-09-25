$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$out = Join-Path $repo ('artifacts\phase-two-config-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'host\release\CodexToolsHost.exe') -Destination (Join-Path $out 'CodexToolsHost.exe')
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe "/out:$out\ConfigRunner.exe" "/r:$out\CodexToolsHost.exe" /r:System.dll (Join-Path $PSScriptRoot 'ConfigRunner.cs')
if ($LASTEXITCODE -ne 0) { throw 'Configuration runner compilation failed.' }
& "$out\ConfigRunner.exe" "$out\fixture"
exit $LASTEXITCODE
