# Host0.5.1 / App0.9 / Boot1.0

本包已完成离线回归及本机USB备份、实际写入、回读和重连验证，作为开发分支测试版交付。实测范围为一块STM32G474RB / 128KiB板卡，不包含8小时、睡眠或升级中断电验收。

`NewMarTool-Host0.5.1-FW0.9.zip` 包含新版上位机、原App0.9/Boot1.0镜像、首次安装脚本、校验清单与验证说明。此次三项修复位于上位机，固件字节未变。已安装Boot1.0/App0.9的板卡只需更新EXE，**不要重复首次完整安装或解除启动区保护**。

0.5.1修复：升级前绑定USB序列号，枚举出另一块板时不读取或改写；升级中阻止托盘退出；首次安装写入前检查WRP1B/PCROP。额度浮窗不再弹出白色悬停说明。

USB升级入口在“诊断与设置 → 设备固件升级”。正常连接时自动取得目标身份；恢复模式中先“识别设备”，在确认框核对序列号。选择有效 `Application-*.bin` 与已安装的 `STM32_Programmer_CLI.exe`。Windows识别DFU设备不代表工具一定兼容该驱动；应使用完整CubeProgrammer配套的WinUSB DFU驱动。

本机通过的工具是STM32CubeProgrammer 2.23.0，DFU驱动为WinUSB 1.3.0.0。安装目录为 `D:\Software\Code\ST\STM32CubeProgrammer\`，升级时选择其中 `bin\STM32_Programmer_CLI.exe`。驱动安装需要Windows管理员权限；本包不附带第三方驱动。

最终上位机EXE SHA-256：`ef2cb6efa403a30759ea9ed23fda571741325071b79118ff8352e005dc27be2b`，与本机运行版本一致。原封装清单中的 `hardware_tested:false` 是构建时标记；实际验证范围以 `VALIDATION.md` 和仓库验收记录为准。

初装新板仍按原安装流程使用 `Install-STLink.ps1`，读回一致后设置启动区保护。本版会拒绝已有冲突保护，不自动解除。完整说明见仓库 [USB安装说明](../usb-update/README.md)，本轮结果见 [安全修复与验收](../../docs/USB_SAFETY-20261023.md)。

OLED实际像素预览、账号总量曲线及USB诊断计划在 [下一步规划](../../docs/NEXT_DEMO-20261023.md)。这些后续功能尚未写入App0.9。
