param([string]$RunName = 'quota-tests')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$out = Join-Path $repo ('artifacts\' + $RunName)
New-Item -ItemType Directory -Path $out -Force | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $repo 'host\src'
$sources = @('Model\QuotaSnapshot.cs','Quota\ICodexStatusSource.cs','Quota\IJsonLineTransport.cs','Quota\JsonLineRpcClient.cs','Quota\ProcessJsonLineTransport.cs','Quota\CodexLocator.cs','Quota\CodexStatusProvider.cs','Protocol\FrameCodec.cs') | ForEach-Object { Join-Path $src $_ }
$sources += Join-Path $PSScriptRoot 'Runner.cs'
if (Test-Path -LiteralPath "$src\Quota\QuotaParser.cs") { $sources += "$src\Quota\QuotaParser.cs" }
& $csc /nologo /target:exe /optimize+ "/out:$out\quota-tests.exe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Quota test compilation failed' }
& "$out\quota-tests.exe"
exit $LASTEXITCODE
