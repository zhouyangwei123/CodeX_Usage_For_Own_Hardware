param([string]$Python='python')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$out=Join-Path $repo 'artifacts\oled-refresh-20261023\checks'
New-Item -ItemType Directory -Force -Path $out|Out-Null
$fw=Join-Path $repo 'firmware\STM32G474\Core'
& $Python "$PSScriptRoot\build_scheduler.py" "$fw\Src\app.c" "$out\scheduler.inc"
if($LASTEXITCODE -ne 0){throw 'Scheduler extraction failed'}
& gcc -std=c99 -funsigned-char -Wall -Wextra -Werror -Wno-unused-function -O2 -I "$PSScriptRoot\include" -I "$fw\Inc" -I $out "$PSScriptRoot\scheduler_tests.c" -o "$out\scheduler.exe"
if($LASTEXITCODE -ne 0){throw 'Scheduler compilation failed'}
& "$out\scheduler.exe"
if($LASTEXITCODE -ne 0){throw 'Scheduler regression failed'}
& gcc -std=c99 -funsigned-char -Wall -Wextra -Werror -O2 -I "$PSScriptRoot\include" -I "$fw\Inc" "$PSScriptRoot\driver_tests.c" -o "$out\driver.exe"
if($LASTEXITCODE -ne 0){throw 'Driver compilation failed'}
& "$out\driver.exe"
if($LASTEXITCODE -ne 0){throw 'Driver regression failed'}
