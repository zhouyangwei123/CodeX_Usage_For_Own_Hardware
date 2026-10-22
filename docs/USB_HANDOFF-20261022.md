# 2026-10-22 实机测试与异地开发接手

当前开发分支：`review/usb-ota-20261022`。产品代码固定在 `49517185aa68a38580eb5cf894cb0008c19493f1`；本次追加的是记录、测试工具源文件和整理包，未修复下面的问题。Host **0.5.0** / App **0.9** / Boot **1.0**。这是已完成部分实机验证的开发版本，整体功能审查尚未通过。

## 明天从哪里继续

默认 `main` 仍是原0.4.2。换电脑后执行：

```powershell
git clone --branch review/usb-ota-20261022 https://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware.git
cd CodeX_Usage_For_Own_Hardware
```

已有仓库时先保存本机修改，再 `git fetch origin`，切换到 `review/usb-ota-20261022`。不要用强制重置覆盖未提交工作。

- 直接使用：解压 [已测试版本整理包](../deliverables/usb-update/NewMarTool-Tested-20261022-Host0.5.0-FW0.9.zip)，先阅读包内README，运行 `Verify-Package.ps1` 核对全部文件，再使用 `CodexToolsHost.exe`。
- 开发代码：`host/`、`firmware/STM32G474/`、`firmware/bootloader/`。构建与离线检查入口见 [USB更新设计与构建说明](USB_UPDATE-20261022.md)。新应用必须经过镜像封装，不能直接使用未封装的CubeIDE输出。
- 检查问题：[三项审查发现](USB_REVIEW-20261022.md)；离线复现源文件和当时结果在 [review_20261022](../tests/usb_update/review_20261022/README.md)。
- 实机测试源文件：[bench_20261022](../tests/usb_update/bench_20261022/README.md)。保留的是当时实际执行的程序；其中部分固定了COM8、本机路径和设备序列号，不是跨设备通用执行入口。
- 精选原始记录：[evidence/usb-update-20261022](evidence/usb-update-20261022/README.md)。完整芯片备份留在原电脑，未把未知的配置内容、个人配置或认证信息推送到Git。

## 当前板卡与是否携带ST-Link

本轮用户确认这是一块仅低压供电的桌面工具，没有功率硬件在线。原应用0.7已更新为Boot1.0/App0.9；最后全部128KiB Flash与首次成功安装后的基线相同，启动区8KiB保持写保护。串口为当时电脑的COM8，USB序列号 `20553872524E`。换电脑后COM号可能改变，应以此设备身份核对，不能只照抄COM8。本轮没有访问另一块COM5设备。

**只更新这块板的应用固件，可以不接ST-Link；明天继续固件开发，建议带上ST-Link和连接线作为备用。** 已验证的USB路径覆盖正常更新、KEY1按住上电和不完整应用恢复；不覆盖修改启动程序、修改保护或启动设置、USB完全无法识别，以及调试程序停在哪里。这些工作仍可能需要ST-Link。不要对已经保护启动区的本板重复运行首次完整安装，也不要为了重试而全片擦除或自动解除保护。

换电脑需安装完整 [STM32CubeProgrammer](https://www.st.com/en/development-tools/stm32cubeprog.html) 及DFU驱动。今天USB更新实际使用 **2.23.0**；本机路径 `D:\Software\Code\ARM\STM32CubeProg\bin\STM32_Programmer_CLI.exe`，不是另一台电脑的固定路径。Windows的DFU驱动安装入口在其 `Drivers\DFU_Driver\STM32 Bootloader.bat`，需要管理员权限，见 [ST官方安装说明](https://dev.st.com/stm32cube-docs/prog/2.23.0/en/docs/markup/CubeProg_How_To_Start/CubeProg_Installation.html#dfu-driver)。ST安装程序与驱动未重新分发到本仓库。

首次完整安装实际用的是本机CubeProgrammer **2.18.0**，本次没有证明2.23.0在SWD首次安装路径上的全部行为。工具版本和路径必须读取实际结果，不能从CubeIDE目录名推定。

## 已验证的结果

| 项目 | 证据与结论 |
|---|---|
| 初次安装 | 交付Host更新代码调用ST-Link，13步成功；Boot/App独立读回匹配，启动区保护已设置 |
| 软件进入DFU | 生产SerialLink发出请求，收到成功ACK；确认同一USB序列号进入ROM |
| USB更新 | 先从交付App改写为本机重建App；7步完成备份、写入、读回和重启；自动回到COM8，10秒39/39回显 |
| KEY1上电 | 用户实际断电并按住KEY1接回USB后，同一设备进入DFU |
| 不完整应用恢复 | 仅写入应用第一部分，实际读回CRC不一致；重启自动进入DFU；随后用交付更新代码恢复完整App |
| 最终镜像 | 交付App45384字节完全匹配；完整128KiB Flash和选项镜像与初装完成后基线相同 |
| 软件重连 | 10次Reconnect、10次Pause/Start均取得新握手、INFO与回显 |
| 连续通信 | 300.003秒无意外断线；全程1184/1184回显，最大往返7ms，319份INFO；运行时间没有回退 |
| 结束状态 | FW0.9正常运行；仅释放本任务连接，之后独占打开检查成功；测试及烧录进程均已退出 |
| 离线检查 | USB22项、原协议16项、镜像9项、更新请求3项、上位机更新45项及四类交付EXE自检通过；硬件与离线证据分开保存 |

受控“不完整应用”是在应用起点 `0x08002000` 写入2048字节，**没有在写入中实际拔线或断电**。测试直接调用交付EXE中的连接与更新代码；没有完成整个设置界面的点击升级流程，也没有覆盖原电脑日常安装的上位机EXE。

仍未验证：8小时连续运行、系统睡眠/唤醒、反复物理拔插、写入中真实断电、全部按键/旋钮/屏幕/灯效行为。函数注释未逐项完成验收。本轮结果不代表全部代码或设备验收完成。

## 已记录的问题与异常现象

1. **P1，待修复：目标可能选错。** 切换前串口设备A未被绑定到切换后的DFU设备；如果只有板B先出现，更新器可以改写B并返回成功。离线复现已证实。本轮实测额外固定了本板序列号，不代表产品已修好。
2. **P2，待修复：托盘退出绕过升级限制。** 直接释放设置窗口会绕过FormClosing检查；后台更新的后续读回、重启和结果处理不能保证完成。仅离线复现，没有在真实写入中执行退出。
3. **P2，待修复：首次安装漏查WRP1B。** 应用页原有保护可能在启动程序已经写入后才被发现。离线复现已证实；本板该保护未启用，不能以本板通过消除缺陷。
4. **已排除阻塞：Windows缺DFU驱动。** 最初设备已进入ROM，但Windows报Code28、CLI找不到USB；安装完整工具和驱动后恢复。驱动为STMicroelectronics 1.3.0.0，设备状态正常。
5. **原因未确定：测试前立即关闭串口停滞。** .NET SerialPort在独占打开后未通信就立即Close，三次测试停滞，设置有限读写超时仍出现。当时尚未调用生产SerialLink。改用Windows原生独占句柄作占用检查后，生产连接测试通过。保留失败记录，不能把原因归咎于固件或宣称已修复驱动。
6. **工具读回限制：** 2.23.0普通上传命令读取第二段Flash失败；限定地址的直接读取成功取得完整64KiB。不能将失败上传视为成功备份。
7. **选项字节比较：** 初次设置启动区保护时，工具还归一化了部分保留位；原始字节不只是WRP1A发生变化。按ST提供的有效位掩码核对，其他有意义设置保持；失败的原始字节断言与修正后的有效位比较均保留。

优先修复1–3项并扩充对应离线拒绝测试，再进行新的实机更新验收。不要把本记录、Git推送或打包视为这些问题已经修复。

## 镜像与工具身份

| 文件 | 字节数 / SHA-256 |
|---|---|
| 交付CodexToolsHost.exe | 6815232 / `370E4B0969BF998CEB80304A52BA53ACAD9A1422C643ACDBBC897AF948CF79A6` |
| 交付Bootloader-1.0.bin | 1920 / `FC479FE98C0F74219AAF1D83BC6084BB1F490B56CCA9C763EBC4F48A36F3046C` |
| 交付Application-0.9.bin | 45384 / `4D215913ECE13D0CAFBC0CF0AF8F5F3F02ED8C2C8A38C2B63BF7CCE36577C812` |
| 本机重建App，仅作为改写测试输入 | 45864 / `E1ECEBDC7F79C9795A8BE8714E7F702DEB9C8E9D85530AFA0C49045AD0D4C2DA` |
| CubeProgrammer2.23.0 CLI | `269DB404ED3F2CDD7CFCCCC257AB0A907F2364C3D44E39EDAB6B007CB5F668ED` |

整理包中的EXE、Boot和App保持交付原字节；更新了说明并附上测试记录及核对工具。本机重建App不是最终留在板上的版本。固件字段、地址和保护解释依据仓库的ST源文件，以及 [AN2606](https://www.st.com/resource/en/application_note/an2606-stm32-microcontroller-system-memory-boot-mode-stmicroelectronics.pdf)、[AN3156](https://www.st.com/resource/en/application_note/an3156-usb-dfu-protocol-used-in-the-stm32-bootloader-stmicroelectronics.pdf)、[RM0440](https://www.st.com/resource/en/reference_manual/rm0440-stm32g4-series-advanced-armbased-32bit-mcus-stmicroelectronics.pdf)；未为当前结果编造新的专业术语。
