# USB 升级与 KEY1 恢复安装包

## 2026-10-22 实测后整理包

下载 [NewMarTool-Tested-20261022-Host0.5.0-FW0.9.zip](NewMarTool-Tested-20261022-Host0.5.0-FW0.9.zip)，完整解压，先读README并运行只读的 `Verify-Package.ps1`。EXE、Boot和App与实际测试的交付字节一致，补充了当前问题、测试证据及工具；原始安装包仍原样保留。

USB更新、KEY1恢复、受控不完整应用恢复、20次软件重连和5分钟通信通过；三项代码问题尚未修复，未完成长期验收。请优先阅读 [当前接手记录](../../docs/USB_HANDOFF-20261022.md) 和 [问题清单](../../docs/USB_REVIEW-20261022.md)。已测试的本板已有Boot1.0保护区，不要重复首次安装；日常应用更新可以只用USB，继续固件开发建议携带ST-Link备用。

下面是原安装包的说明，其“未烧录”描述为最初打包时状态；当前结果以上述实机记录为准。

版本：上位机 **0.5.0**、应用固件 **0.9**、启动程序 **1.0**。适用于本项目 STM32G474RBTx / 128 KiB 板卡。已完成离线测试，尚未在真实设备上烧录和验收。

下载同目录的 **NewMarTool-USB-Update-Host0.5.0-FW0.9.zip**，完整解压后使用。该版本包含串口稳定性修复；无需先安装旧的 0.8 固件。

## 第一次：用 ST-Link 安装

1. 安装 ST 官方 STM32CubeProgrammer 和相应 ST-Link / USB DFU 驱动。仅连接这一块待升级板和一个 ST-Link，给板供电并接好 SWD。
2. 退出原上位机和其他占用烧录器的软件。保留原上位机 EXE 和配置作为备份，不把个人配置放进本安装包。
3. 在解压目录打开 Windows PowerShell，执行下列命令。`-ProgrammerPath` 改为本机实际位置：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-STLink.ps1 -ProgrammerPath "C:\Program Files\STMicroelectronics\STM32Cube\STM32CubeProgrammer\bin\STM32_Programmer_CLI.exe" -Install
```

脚本会检查芯片 ID、128 KiB 容量和现有保护配置，备份 Flash 前 64 KiB及选项字节，写入启动程序和应用，逐字节回读校验，最后设置前 8 KiB 启动区的硬件写保护并复位。备份保存在 `%LOCALAPPDATA%\CodexToolsHost\firmware-backups`。这是原应用区域的备份，不是整片 Flash 备份。

**首次安装请使用脚本。单独烧录 `FirstInstall-Boot1.0-FW0.9.hex` 不会设置启动区写保护，也不等于完成本安装流程。** HEX 作为带地址镜像提供给熟悉手动烧录的开发者；保护条件见验证说明。脚本不修改 DBANK、读保护或启动选项，不全片擦除，不写配置页。已有写保护或身份不匹配时会停止，不自动解除保护。

安装成功后，确认新上位机显示设备固件 **0.9**。退出旧上位机再运行包内 `CodexToolsHost.exe`；需要替换原安装目录程序时保留原配置。

## 之后：通过 USB 更新应用

在新上位机的“诊断与设置 → 设备固件升级”中：

1. 选择发布包中的 `Application-*.bin`。
2. 选择已安装的 `STM32_Programmer_CLI.exe`。
3. 点击“开始 USB 升级”，保持 USB 供电。程序会备份原应用、写入、回读校验并请求重启。

USB 更新只接收匹配型号、分区和 CRC 的新格式应用 BIN。不要选择 `Bootloader-1.0.bin`、首次完整 HEX 或旧版 0.8 BIN。应用空间为 56 KiB，启动程序不通过普通 USB 更新。

## 应用损坏或串口连不上时

断开板的所有供电，按住 **KEY1（PC3 对应按键）**，重新插入 USB；待电脑识别 DFU 设备后松开。在同一升级页面选择有效应用 BIN 并升级，此时不需要原串口连接成功。应用镜像校验失败时也会自动进入恢复模式。

升级中断后可再次进入此模式重试。这里的恢复是重新刷入应用，不是自动回滚旧版本。若 DFU 未识别，先检查数据线、CubeProgrammer DFU 驱动及 KEY1；保留首次安装的 ST-Link 作为实机验收期间的备用恢复方式。

## 回家后的实机验收

按顺序确认：首次安装与 FW 0.9 启动 → KEY1 上电进入 DFU → 正常 USB 升级 → 应用升级中断后的 KEY1 恢复 → 按键、旋钮、屏幕和灯效 → 长时间运行及反复重连。首次安装启动程序时不要进行断电测试；中断恢复测试仅在启动区已保护且 ST-Link 可用时进行。

离线验证通过不代表这些硬件项目已经通过。完整测试范围、复现命令和官方参考见仓库 `docs/USB_UPDATE-20261022.md`；包内同时附有 `VALIDATION.md`。

文件 SHA-256 见 `SHA256SUMS.txt`；固件地址、长度和哈希见 `firmware-manifest.json`。CRC 用于检测损坏，不提供固件签名认证。
