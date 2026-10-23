# Host0.6.1 / App0.10 USB与OLED实机检查

后续复用：UsbSafetyBench可在最后附加原固件版本（缺省0.9）；LiveUsageBench可传`--firmware=0.11`指定目标版本（缺省0.10）。`--require-local-history`要求本机确有真实过去曲线并发送了非零曲线。App0.11的1Hz试验结果另见[本轮说明](../../../docs/OLED_1HZ-20261023.md)，不改写以下历史验收范围。

此夹具在上一轮已验证代码基础上新增，旧`bench_20261023`保持历史原样。必须先正常退出上位机，设备固定为本轮COM6及USB序列号20553872524E，不移除身份检查来绕过不匹配。

- `UsbSafetyBench.cs`调用实际生产更新器，由原App0.9进入ROM DFU，以错误预期身份检查零内存操作拒绝，再绑定正确身份完成应用备份、写入和回读；重启前经同一USB连接再次读回整个8KiB启动区和44字节选项，逐字节确认保持。实际写入阶段检查托盘退出和ExitThread被阻止。最后必须重新取得与目标镜像版本一致的INFO。
- `SerialLinkBench.cs`引用实际发布的SerialLink，执行10轮Reconnect、10轮Pause/Start及5分钟持续PING/INFO，同时每秒发送STATUS、28字节PC_METRICS、最大128字节本机用量和账号日汇总，并轮换4/0/1/2页。另发送过期、缺失和部分记录样例，检查链路、OLED在线状态与运行时间。曲线输入是明确的合成测试数据，不是个人用量。
- `LiveUsageBench.cs`通过真实CodexStatusProvider和LocalUsageService读取账号/本机数据，经生产BridgeService与SerialLink发送到设备，检查两种来源均已知、来源切换、28字节PC帧和输入/输出合计。仅独立API余额使用固定测试替身；结束后日常上位机恢复真实供应商配置。记录只保留可用性与检查结果，不记录个人Token数值。

自动检查覆盖协议、启动保护、传输与OLED驱动在线状态；不能以软件发送成功证明肉眼可读性、实际旋钮手感或长时间可靠性。物理按键恢复沿用Boot1.0先前实测，本轮不把它冒充重新实测。输出放忽略的`artifacts/oled-usb-20261023/`，个人配置和完整芯片备份不提交。

编译需.NET Framework C#编译器；EXE与实际发布的CodexToolsHost.exe置于同一独立目录。USB夹具引用System.Core、System.Management、System.Web.Extensions、System.Windows.Forms、System.Drawing；串口夹具不需要后两项。USB升级参数依次为CubeProgrammer CLI路径、封装镜像目录、输出目录、COM号、预期USB序列号；串口夹具参数为输出JSON路径。
