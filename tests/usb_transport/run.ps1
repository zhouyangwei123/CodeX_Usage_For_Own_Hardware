param([string]$SourceRoot, [switch]$RegressionsOnly)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $SourceRoot) { $SourceRoot = Join-Path $repo 'firmware\STM32G474' }
$out = Join-Path $repo 'artifacts\usb-transport'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$gcc = (Get-Command gcc -ErrorAction Stop).Source
$exe = Join-Path $out $(if ($RegressionsOnly) {'baseline-test.exe'} else {'usb-transport-test.exe'})
& $gcc -std=c99 -Wall -Wextra -Werror -Wno-unused-parameter -O2 -I $PSScriptRoot -I "$SourceRoot\Core\Inc" -I "$SourceRoot\USB_Device\App" "$PSScriptRoot\main.c" "$SourceRoot\Core\Src\protocol.c" "$SourceRoot\USB_Device\App\usbd_cdc_if.c" -o $exe
if ($LASTEXITCODE -ne 0) { throw 'USB transport test compilation failed' }
if ($RegressionsOnly) { & $exe --regressions-only } else { & $exe }
exit $LASTEXITCODE
