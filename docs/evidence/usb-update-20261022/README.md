# 2026-10-22 精选原始证据

这里逐字节复制本轮测试输出，未将失败值改写为成功。综合解释见 [接手记录](../../USB_HANDOFF-20261022.md)。部分JSON保留当时的本机路径和测试进程编号，它们用于说明实际执行对象，不是另一台电脑的配置值。

- `initial-installer-result.json`、`initial-install-verification.json`：初次安装结果及独立读回。`optionChangesOnlyWRP1A=false`是真实结果；保留位也发生变化，之后按有效位掩码确认其他有意义设置保持。
- `usb-update-result.json`、`usb-update-verification.json`、`serial-after-usb-rebuilt.json`：USB写入本机重建版本、独立读回和重新通信。
- `key1-physical-dfu.txt`：用户实际按住KEY1上电后检测到同一DFU设备。
- `partial-application-result.json`：受控的不完整应用恢复，不是写入期间物理断电。
- `usb-recovery-result.json`、`final-memory-verification.json`：恢复交付版本以及最终128KiB比对。
- `serial-production-reconnect-and-soak.json`、`serial-bench-production.txt`：20次软件重连及5分钟连续通信。
- `serial-bench-*-stall.json`、`serial-bench-diagnostic*.txt`：测试夹具预检查停滞；不是产品修复成功证据。
- `host-*.json`：交付EXE四类离线自检。
- `final-device-and-process-state.json`：结束时COM8正常、测试进程已退出。

本目录不包含个人配置、登录文件、Flash完整备份或烧录器生成的整个备份目录。目录中的“passed”仅适用于对应记录的测试范围。
