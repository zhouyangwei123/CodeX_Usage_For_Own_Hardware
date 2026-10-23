param([switch]$Live)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = Join-Path $repo 'artifacts\usage-tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'host\src') -Recurse -Filter '*.cs' | ForEach-Object FullName)
$sources += Join-Path $PSScriptRoot 'UsageTests.cs'
$exe = Join-Path $output 'UsageTests.exe'
$references = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.Management.dll')
foreach($name in @('UIAutomationClient','UIAutomationTypes','WindowsBase')) {
 $dll=(Get-ChildItem -LiteralPath "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\$name" -Filter "$name.dll" -Recurse | Select-Object -First 1).FullName
 $references += "/r:$dll"
}
& $compiler /nologo /target:exe /main:UsageTests $references "/out:$exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'Usage focused compilation failed.' }
if ($Live) { & $exe --live } else { & $exe }
if ($LASTEXITCODE -ne 0) { throw 'Usage focused tests failed.' }
