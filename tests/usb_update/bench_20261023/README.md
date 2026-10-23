# 0.5.1 本机实机验证夹具

这些程序引用实际构建的 `CodexToolsHost.exe`，不替换生产更新器、串口实现或镜像校验器。结果见 `docs/USB_SAFETY-20261023.md`，输出放入忽略的 `artifacts/safety-20261023/`。

- `SerialLinkBench.cs`：固定本轮COM6及完整USB身份；10次Reconnect、10次Pause/Start和300秒PING/INFO。执行前退出上位机，不移除身份核对来绕过不匹配。
- `UsbSafetyBench.cs`：参数为CLI路径、封装固件目录、输出目录、COM号、预期USB序列号。从真实CDC身份进入DFU，先用错误预期身份验证拒绝所有内存操作，再绑定正确身份执行真实备份、写入、回读和重启。写入阶段调用真实托盘退出入口与ExitThread，检查升级状态阻止退出，最后检查INFO重连。

升级夹具会写入应用，不能在其他程序占用设备时运行。需使用已安装Boot1.0并保护启动区的本项目128KiB板卡、匹配封装应用和完整DFU驱动。首次安装冲突的模拟回归在 `tests/usb_update/Runner.cs`，不在实机上故意改保护复现。

使用Windows .NET Framework C#编译器；串口夹具引用System.Management、System.Web.Extensions、System.Core和生产EXE，升级夹具另引用System.Windows.Forms。生成EXE与生产EXE放在同一独立目录。失败先保留日志，不把缺驱动、超时和未连接当作通过。
