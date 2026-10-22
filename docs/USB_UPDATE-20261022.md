# USB 更新与按键恢复验证（2026-10-22）

后续实机记录与未修复问题见 [USB_HANDOFF-20261022.md](USB_HANDOFF-20261022.md)。下面保留最初实现时的设计、构建和离线验证记录；其中“未烧录”和“待实机”是当时状态，不代表后续结果。实际USB更新使用CubeProgrammer2.23.0，首次安装使用2.18.0；旧记录里的2.17.0未作为本轮实测工具版本采用。

原实现阶段状态：代码、固件与上位机构建、离线测试完成；当时未对真实板卡烧录。交付版本 Host 0.5.0 / App 0.9 / Boot 1.0，延续 `fix/usb-serial-stability` 分支。用户明确要求修改固件、实现 USB 升级与按键恢复并推送分支；仓库历史固件冻结约定不代表本次授权范围。

## 实现与边界

- 前 8 KiB 为独立启动程序，首次 ST-Link 安装后设置 WRP1A 硬件写保护。正常启动先校验应用；KEY1（PC3，低电平有效）上电按住、软件升级请求、应用校验失败均进入芯片 ROM USB DFU。
- 应用基址改为 `0x08002000`，中断向量同步迁移。软件升级请求使用保留 RAM 顶端 8 字节和系统复位；仅接受软件复位且魔数/反码同时匹配的请求。启动程序清除标志，ROM GO 返回启动程序时先进行系统复位，避免沿用 DFU 外设状态。
- 应用容量 56 KiB，截止 `0x08010000`。启动程序与应用均限制在前 64 KiB，兼容该容量芯片两种 DBANK 页面大小并避开第二 Bank 的映射差异；不改变 DBANK，不访问业务配置页。
- 镜像在偏移 `0x200` 保存 32 字节头，包含类型、布局、地址、长度、CRC32、版本与目标标识；代码从 `+0x400` 开始。构建后封装工具填写实际长度和 CRC，三种语言的检查覆盖同一格式。CRC32 IEEE 计算时 CRC 字段视为零；CRC 检测意外损坏，不是签名认证。
- 启动程序校验栈地址、Thumb 入口范围、长度、型号和 CRC；应用不完整时留在 DFU，允许重新写入。未实现双镜像自动回滚。
- 上位机新增固件升级页。进入升级后暂停串口扫描和心跳；设备枚举必须唯一且带序列号，后续命令固定此序列号。先验证已安装启动程序和写保护，备份应用，再写入、独立回读比对、请求重启。失败时不宣称完成，不自动全片擦除或解除保护。
- 首次安装脚本检查设备 ID `0x469`、Flash 容量 128 KiB、RDP `0xAA`、BFB2=0、BOOT_LOCK=0、WRP1A 未启用；备份前 64 KiB和选项字节；两镜像校验成功后设置 WRP1A。DBANK=1 时页面 2 KiB、保护页 0–3；DBANK=0 时页面 4 KiB、保护页 0–1。不修改读保护、Bank 模式和启动选项。后续 USB 升级要求这些保护条件成立。

初装中断仍可能需要 ST-Link。保护完成后的应用更新不触碰启动区；物理按键、电气条件、ROM 枚举和驱动需实机验证。新布局没有把所有 128 KiB 都分配给应用，当前封装应用为 45,384 字节，容量上限 57,344 字节。

## 离线验证

| 项目 | 结果与限制 |
|---|---|
| ARM 应用/启动程序编译 | 成功，0 警告；启动镜像 1,920 B，应用镜像 45,384 B |
| 生产 C 镜像校验器 | 9 项通过；710 个部分写入切点不能接受与目标不同的数据；1,024 个分散位翻转均被拒绝 |
| 生产 C 升级请求处理 | 3 项通过：错误令牌、时间回绕、200 ms 复位标志交接 |
| 上位机/首次安装流程 | 45 项通过：实际镜像、目标选择、保护检查、备份、失败停止、回读校验、两种页大小、进程错误及超时 |
| Windows PowerShell 5.1 安装入口 | 中文/空格路径与 .NET 调用通过；假烧录器无设备时安全停止，没有真实烧录 |
| USB 传输回归 | 22 项通过，包含 100,000 次传输与 1,000 次模拟重新枚举 |
| 原协议回归 | 16 项通过 |
| 上位机内置检查 | 自检、模拟连接闭环及 UI 批量自检退出码均为 0 |
| 升级页面 | 离屏渲染检查可读，无截断；不是物理设备操作验收 |
| 打包核对 | HEX 校验和/地址范围、HEX 与 BIN 一致性、ZIP 内容和 SHA-256 核对通过 |

镜像与请求测试直接编译生产 C 源码。上位机测试引用真实构建的 EXE，仅替换外部设备/烧录器响应；另启动真实假程序进程验证中文、引号、空参数、尾部反斜杠、错误输出、非零退出码和超时。测试不模拟 USB 电气和 ROM 固件执行，不能证明硬件 USB 切换或长时间运行稳定。

本机 CubeProgrammer 2.17.0 的帮助确认支持 USB 序列号固定、上传回读、写入验证及运行地址命令；只读枚举无 DFU 设备时退出码为 0。没有执行真实写入或选项字节变更。

## 重现构建与检查

Windows 需要 .NET Framework C# 编译器、MinGW gcc、Python、STM32CubeIDE 与 GNU Arm 工具链。路径按本机安装位置填写。

```powershell
./host/build.ps1
./tools/build-usb-firmware.ps1 -CubeIde '<stm32cubeidec.exe>' -ArmBin '<GNU Arm tools/bin>' -Python '<python.exe>'
./tests/usb_update/run.ps1
./tests/usb_transport/run.ps1
./tests/protocol_pc/run.ps1
```

新布局必须经过 `package-usb-firmware.py` 封装；不要直接把 CubeIDE 输出的未封装 ELF/BIN 当成可升级镜像。原始 ELF 的校验字段是占位值。封装后的文件输出在 `artifacts/usb-update/package`，测试记录在 `artifacts/usb-update-tests`。安装说明和完整包位于 `deliverables/usb-update`。

## 待实机验收

1. ST-Link 安装成功、回读一致、WRP 范围正确；断电上电后 INFO 为 0.9，按键/旋钮/OLED/RGB 正常。
2. KEY1 按住上电识别 ROM DFU；正常上位机命令也能进入 DFU；升级后重启并重新连接。
3. 启动区已保护后，中断应用更新并重新上电；能进入恢复模式并重新写入有效应用。无效镜像被上位机拒绝，不更改 Flash。
4. 延续串口稳定性验收：持续运行至少 8 小时、睡眠唤醒 20 轮、USB 拔插 20 轮、上位机退出/启动及手动重连 20 轮，记录失败与恢复耗时。

## 参考依据

- ST [AN2606](https://www.st.com/resource/en/application_note/an2606-stm32-microcontroller-system-memory-boot-mode-stmicroelectronics.pdf)：STM32G47xxx/G48xxx ROM bootloader，USB PA11/PA12、HSI48/CRS、VDDUSB、系统存储器入口及软件跳转要求。
- ST [AN3156](https://www.st.com/resource/en/application_note/an3156-usb-dfu-protocol-used-in-the-stm32-bootloader-stmicroelectronics.pdf)：ROM USB DFU 协议。
- ST [RM0440](https://www.st.com/resource/en/reference_manual/dm00355726.pdf)：Flash 分区、选项字节、DBANK、写保护和复位标志。
- 本机 CubeProgrammer 2.17.0 的 `STM32_Prog_DB_0x469.xml` 与 CLI 帮助：容量地址、选项字节地址/位域及命令参数。部署时仍以实际芯片识别和回读为准。
