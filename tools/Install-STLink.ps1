param(
 [Parameter(Mandatory=$true)][string]$ProgrammerPath,
 [string]$PackageDirectory = $PSScriptRoot,
 [switch]$Install
)
$ErrorActionPreference = 'Stop'
if (-not $Install) {
 Write-Output '此操作将备份原程序、安装Bootloader和应用、校验并保护启动区。'
 Write-Output '确认已接好唯一一台STM32G474RB后，使用 -Install 参数执行。'
 exit 0
}
$hostExe = Join-Path $PackageDirectory 'CodexToolsHost.exe'
$bootFile = Join-Path $PackageDirectory 'Bootloader-1.0.bin'
$appFile = Join-Path $PackageDirectory 'Application-0.9.bin'
foreach ($path in @($ProgrammerPath,$hostExe,$bootFile,$appFile)) {
 if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "文件不存在：$path" }
}
[Reflection.Assembly]::LoadFrom($hostExe) | Out-Null
$programmer = New-Object CodexToolsHost.Updates.CubeProgrammer($ProgrammerPath)
$installer = New-Object CodexToolsHost.Updates.StLinkFirmwareInstaller($programmer)
$backupRoot = Join-Path $env:LOCALAPPDATA 'CodexToolsHost\firmware-backups'
$backup = $installer.Install([IO.File]::ReadAllBytes($bootFile),[IO.File]::ReadAllBytes($appFile),$backupRoot)
Write-Output "安装、回读校验和启动区保护完成。备份位置：$backup"
Write-Output '请用KEY1按住上电测试DFU恢复，并验证正常启动的FW 0.9。'
