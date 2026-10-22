param(
 [Parameter(Mandatory=$true)][string]$CubeIde,
 [Parameter(Mandatory=$true)][string]$ArmBin,
 [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = Join-Path $repo 'artifacts\usb-update'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$fw = Join-Path $repo 'firmware\STM32G474'
$buildLog = Join-Path $out 'application-build.log'
& $CubeIde --launcher.suppressErrors -nosplash -application org.eclipse.cdt.managedbuilder.core.headlessbuild -data (Join-Path $out 'cube-workspace') -import $fw -build '20260801_CodeX_Tools/Release' > $buildLog 2>&1
if ($LASTEXITCODE -ne 0) { Get-Content $buildLog -Tail 60; throw 'Application build failed' }
$includes = @('Core\Inc','Drivers\CMSIS\Device\ST\STM32G4xx\Include','Drivers\CMSIS\Include') | ForEach-Object { '-I'+(Join-Path $fw $_) }
$bootElf = Join-Path $out 'bootloader.elf'
& (Join-Path $ArmBin 'arm-none-eabi-gcc.exe') -mcpu=cortex-m4 -mthumb -mfpu=fpv4-sp-d16 -mfloat-abi=hard -Os -ffunction-sections -fdata-sections -Wall -DSTM32G474xx -DVECT_TAB_OFFSET=0 '-Wa,--defsym,NMT_BOOT_STAGE=1' $includes (Join-Path $repo 'firmware\bootloader\boot.c') (Join-Path $fw 'Core\Src\firmware_image.c') (Join-Path $fw 'Core\Src\system_stm32g4xx.c') (Join-Path $fw 'Core\Startup\startup_stm32g474rbtx.s') ('-T'+(Join-Path $repo 'firmware\bootloader\boot.ld')) -nostdlib -nostartfiles -lgcc '-Wl,--gc-sections' ('-Wl,-Map='+(Join-Path $out 'bootloader.map')) -o $bootElf
if ($LASTEXITCODE -ne 0) { throw 'Bootloader build failed' }
& (Join-Path $ArmBin 'arm-none-eabi-size.exe') $bootElf (Join-Path $fw 'Release\20260801_CodeX_Tools.elf')
& (Join-Path $ArmBin 'arm-none-eabi-objcopy.exe') -O binary $bootElf (Join-Path $out 'boot.raw.bin')
if ($LASTEXITCODE -ne 0) { throw 'Boot conversion failed' }
& (Join-Path $ArmBin 'arm-none-eabi-objcopy.exe') -O binary (Join-Path $fw 'Release\20260801_CodeX_Tools.elf') (Join-Path $out 'app.raw.bin')
if ($LASTEXITCODE -ne 0) { throw 'Application conversion failed' }
& $Python (Join-Path $PSScriptRoot 'package-usb-firmware.py') --boot (Join-Path $out 'boot.raw.bin') --app (Join-Path $out 'app.raw.bin') --out (Join-Path $out 'package')
if ($LASTEXITCODE -ne 0) { throw 'Firmware image sealing failed' }
Select-String -LiteralPath $buildLog -Pattern 'Build Finished|warning:' | ForEach-Object { $_.Line }
