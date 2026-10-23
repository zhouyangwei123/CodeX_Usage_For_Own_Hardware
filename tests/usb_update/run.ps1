param([string]$PackageDirectory)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if(-not $PackageDirectory){$PackageDirectory=Join-Path $repo 'artifacts\usb-update\package'}
$applications=@(Get-ChildItem -LiteralPath $PackageDirectory -Filter 'Application-*.bin' -File)
if($applications.Count -ne 1){throw 'Expected exactly one application image'}
$application=$applications[0]
$out=Join-Path $repo 'artifacts\usb-update-tests'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$fw=Join-Path $repo 'firmware\STM32G474'
& gcc -std=c99 -Wall -Wextra -Werror -O2 -I "$fw\Core\Inc" "$PSScriptRoot\image_tests.c" "$fw\Core\Src\firmware_image.c" -o "$out\image-tests.exe"
if($LASTEXITCODE -ne 0){throw 'Image tests compile failed'}
& "$out\image-tests.exe" $application.FullName "$PackageDirectory\Bootloader-1.0.bin" | Tee-Object -FilePath "$out\image-results.txt"
if($LASTEXITCODE -ne 0){throw 'Image tests failed'}
& gcc -std=c99 -Wall -Wextra -Werror -O2 -I $PSScriptRoot -I "$fw\Core\Inc" "$PSScriptRoot\request_tests.c" "$fw\Core\Src\firmware_update.c" -o "$out\request-tests.exe"
if($LASTEXITCODE -ne 0){throw 'Request tests compile failed'}
& "$out\request-tests.exe" | Tee-Object -FilePath "$out\request-results.txt"
if($LASTEXITCODE -ne 0){throw 'Request tests failed'}
Copy-Item -LiteralPath "$repo\host\release\CodexToolsHost.exe" -Destination "$out\CodexToolsHost.exe" -Force
$fakeDir=Join-Path $out '中文 测试目录'
New-Item -ItemType Directory -Force -Path $fakeDir | Out-Null
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /r:System.Web.Extensions.dll "/out:$fakeDir\FakeProgrammer.exe" "$PSScriptRoot\FakeProgrammer.cs"
if($LASTEXITCODE -ne 0){throw 'Fake CLI compilation failed'}
& $csc /nologo /r:System.Core.dll /r:System.Web.Extensions.dll "/r:$out\CodexToolsHost.exe" "/out:$out\updater-tests.exe" "$PSScriptRoot\Runner.cs"
if($LASTEXITCODE -ne 0){throw 'Updater tests compilation failed'}
& "$out\updater-tests.exe" $PackageDirectory $out "$fakeDir\FakeProgrammer.exe" | Tee-Object -FilePath "$out\updater-results.txt"
if($LASTEXITCODE -ne 0){throw 'Updater tests failed'}
$wrapperPackage=Join-Path $out ('首次安装 测试包 '+$application.BaseName)
New-Item -ItemType Directory -Force -Path $wrapperPackage | Out-Null
Copy-Item -LiteralPath "$out\CodexToolsHost.exe" -Destination $wrapperPackage -Force
foreach($name in @('Bootloader-1.0.bin',$application.Name)) {
 Copy-Item -LiteralPath (Join-Path $PackageDirectory $name) -Destination $wrapperPackage -Force
}
$wrapper=Join-Path $repo 'tools\Install-STLink.ps1'
# Encode a PowerShell literal, retaining apostrophes and Chinese in local paths.
$wrapperLiteral="'"+$wrapper.Replace("'","''")+"'"
$fakeLiteral="'"+(Join-Path $fakeDir 'FakeProgrammer.exe').Replace("'","''")+"'"
$packageLiteral="'"+$wrapperPackage.Replace("'","''")+"'"
$command="[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding(`$false); & $wrapperLiteral -ProgrammerPath $fakeLiteral -PackageDirectory $packageLiteral -Install"
$encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
$previousPreference=$ErrorActionPreference
try {
 # The missing-device exception is expected; Windows PowerShell maps stderr to errors.
 $ErrorActionPreference='Continue'
 $wrapperResult=& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -EncodedCommand $encoded 2>&1 | Out-String
} finally { $ErrorActionPreference=$previousPreference }
if($wrapperResult -notmatch '首次安装需要且只能连接一个ST-Link'){throw $wrapperResult}
'PASS Windows PowerShell 5.1 wrapper and Unicode paths (safe device-selection rejection)' | Tee-Object -FilePath "$out\wrapper-results.txt"
exit 0
