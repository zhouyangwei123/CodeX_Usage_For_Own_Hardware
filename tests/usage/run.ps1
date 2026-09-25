param([switch]$Live)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = Join-Path $repo 'artifacts\usage-tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'host\src\Usage') -Filter '*.cs' | ForEach-Object FullName)
$panel = Join-Path $repo 'host\src\UI\UsagePanel.cs'
if (Test-Path -LiteralPath $panel) { $sources += $panel }
$sources += Join-Path $PSScriptRoot 'UsageTests.cs'
$exe = Join-Path $output 'UsageTests.exe'
& $compiler /nologo /target:exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "/out:$exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'Usage focused compilation failed.' }
if ($Live) { & $exe --live } else { & $exe }
if ($LASTEXITCODE -ne 0) { throw 'Usage focused tests failed.' }
