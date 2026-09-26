$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo 'artifacts\token-activity-tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'host\src\Usage') -Filter '*.cs' | ForEach-Object FullName)
$sources += Join-Path $repo 'tests\token_activity\TokenActivityTests.cs'
$exe = Join-Path $output 'TokenActivityTests.exe'
& $compiler /nologo /target:exe /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll "/out:$exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'Token activity compilation failed.' }
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Token activity tests failed.' }
