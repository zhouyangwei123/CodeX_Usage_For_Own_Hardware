param([string]$Python='python',[string]$Node='node')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$out=Join-Path $repo 'artifacts\oled-usage-20261023\checks'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$fw=Join-Path $repo 'firmware\STM32G474\Core'
& $Python "$PSScriptRoot\build_primitives.py" "$fw\Src\oled.c" "$out\oled-primitives.c"
if($LASTEXITCODE -ne 0){throw 'Primitive extraction failed'}
# ARM GCC uses unsigned char by default; match that ABI in the native driver test.
& gcc -std=c99 -funsigned-char -Wall -Wextra -Werror -O2 -I "$fw\Inc" "$PSScriptRoot\native.c" "$fw\Src\usage_oled.c" "$out\oled-primitives.c" -o "$out\native.exe"
if($LASTEXITCODE -ne 0){throw 'Native usage tests compile failed'}
& "$out\native.exe" --selftest
if($LASTEXITCODE -ne 0){throw 'Native usage tests failed'}
Copy-Item -LiteralPath "$repo\host\release\CodexToolsHost.exe" -Destination $out -Force
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe "/out:$out\OledUsageTests.exe" "/r:$out\CodexToolsHost.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll "$PSScriptRoot\Runner.cs"
if($LASTEXITCODE -ne 0){throw 'Usage integration tests compile failed'}
& "$out\OledUsageTests.exe" $out "$out\native.exe"
if($LASTEXITCODE -ne 0){throw 'Usage integration tests failed'}
& $Node "$PSScriptRoot\demo.cjs"
if($LASTEXITCODE -ne 0){throw 'Demo raster tests failed'}
