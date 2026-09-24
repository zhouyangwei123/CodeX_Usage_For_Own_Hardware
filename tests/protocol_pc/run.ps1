$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Proj = [IO.Path]::GetFullPath((Join-Path $Root '..\..\firmware\STM32G474'))
$GccCommand = Get-Command gcc -ErrorAction SilentlyContinue
$Gcc = if ($GccCommand) { $GccCommand.Source } else { 'D:\Software\Code\mingw64\bin\gcc.exe' }
if (-not (Test-Path $Gcc)) { throw 'MinGW gcc 未找到' }

$Out = Join-Path $Root 'protocol_test.exe'
& $Gcc -Wall -Wextra -O1 -I "$Proj\Core\Inc" -I $Root "$Root\main.c" "$Proj\Core\Src\protocol.c" -o $Out
if ($LASTEXITCODE -ne 0) { throw '协议测试编译失败' }

& $Out
exit $LASTEXITCODE
