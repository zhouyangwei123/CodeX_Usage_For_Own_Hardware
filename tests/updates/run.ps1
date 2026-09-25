param([switch]$Live)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$out = Join-Path $repo ('artifacts\update-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force | Out-Null
$sources = @((Join-Path $PSScriptRoot 'Runner.cs'))
$sources += @(Get-ChildItem -LiteralPath (Join-Path $repo 'host\src\Updates') -Filter '*.cs' | ForEach-Object FullName)
$panel = Join-Path $repo 'host\src\UI\UpdateSettingsPanel.cs'
if (Test-Path -LiteralPath $panel) { $sources += $panel }
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:exe /warnaserror+ "/out:$out\UpdateTests.exe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Update test compilation failed' }
$arguments = @((Join-Path $out 'fixtures'))
if ($Live) { $arguments += '--live' }
& "$out\UpdateTests.exe" $arguments
exit $LASTEXITCODE
