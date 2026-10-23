param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$package = [IO.Path]::GetFullPath($PackageDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$app = Join-Path $package 'Application-0.9.bin'
$boot = Join-Path $package 'Bootloader-1.0.bin'
foreach ($inputFile in @($compiler,$app,$boot)) {
    if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) { throw "Required file missing: $inputFile" }
}
$out = Join-Path $repo ('artifacts\usb-review-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out | Out-Null
$hostExe=Join-Path $repo 'host\release\CodexToolsHost.exe'
Copy-Item -LiteralPath $hostExe -Destination $out

<# Compile one offline reproduction. name selects a source under this directory;
   extraSources and references add compile inputs only. Returns the output EXE
   path; a nonzero compiler result throws before any reproduction is executed. #>
function Build-Reproduction([string]$name,[string[]]$extraSources,[string[]]$references) {
    $exe = Join-Path $out ($name + '.exe')
    $arguments = @('/nologo','/target:exe',('/out:' + $exe),('/r:'+$hostExe)) + $references + $extraSources + (Join-Path $PSScriptRoot ($name + '.cs'))
    & $compiler @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $name" }
    return $exe
}

foreach ($name in @('HostReviewRepro','WrpAreaBRepro')) {
    $exe = Build-Reproduction $name @() @()
    $caseOutput = Join-Path $out $name
    New-Item -ItemType Directory -Path $caseOutput | Out-Null
    & $exe $app $boot $caseOutput | Tee-Object -FilePath (Join-Path $out ($name + '.txt'))
    if ($LASTEXITCODE -ne 0) { throw "Regression failed: $name" }
}
$ui = Build-Reproduction 'UiLifetimeRepro' @() @('/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll')
$child = Start-Process -FilePath $ui -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $out 'UiLifetimeRepro.txt') -RedirectStandardError (Join-Path $out 'UiLifetimeRepro.stderr.txt')
if ($child.ExitCode -ne 0) { throw 'UI update lifetime regression failed.' }
Get-Content -LiteralPath (Join-Path $out 'UiLifetimeRepro.txt')
Write-Output "All three safety regressions passed offline. Evidence: $out"
