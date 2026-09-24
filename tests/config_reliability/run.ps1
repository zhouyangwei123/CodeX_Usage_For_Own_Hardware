$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$out = Join-Path $repo ('artifacts\config-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item -LiteralPath "$repo\host\release\CodexToolsHost.exe" -Destination "$out\CodexToolsHost.exe"
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe "/out:$out\ConfigTests.exe" "/r:$out\CodexToolsHost.exe" /r:System.dll (Join-Path $PSScriptRoot 'Runner.cs')
if ($LASTEXITCODE -ne 0) { throw 'Config test compilation failed' }
& "$out\ConfigTests.exe" "$out\fixture"
exit $LASTEXITCODE
