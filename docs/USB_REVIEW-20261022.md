# USB 更新上位机独立审查

2026-10-23补充：本报告保留最初发现与复现依据；0.5.1已修复下面三项，并将复现转换为安全回归。当前验证状态见 [USB_SAFETY-20261023.md](USB_SAFETY-20261023.md)，不能把原始缺陷输出当作新版测试结论。

范围：`e57097a983a8246f795d7301f72a61356ed38cc1` → `49517185aa68a38580eb5cf894cb0008c19493f1`。

已检查 `host/src/Updates/` 四个类、`FirmwareUpdatePanel`、`BridgeService`、`SerialLink`、`SerialPortSelection`，以及相关安装脚本、测试与说明。未修改产品源码、索引或分支；未打开串口、访问 ST-Link、执行真实烧录器或读取个人配置。下述复现仅使用生产 C# 类和模拟设备；UI 检查使用无硬件依赖的替代对象。

已有有效防护：在操作设备前校验镜像并复制输入数据；USB 更新限定应用地址、保留旧应用、独立读回比对；失败后不继续请求重启；CLI 有超时和错误输出检查；串口暂停同时影响发送、扫描和心跳。上述防护不能代替以下问题的修复，也不能证明硬件验收通过。

## 发现

### [P1] 进入 DFU 后没有核对是否仍为原先连接的设备

- 位置：`host/src/Updates/UsbFirmwareUpdater.cs:37-47`，尤其第 41 行；调用方 `host/src/UI/FirmwareUpdatePanel.cs:75-89`，`host/src/Core/BridgeService.cs:117-124`。
- 条件：上位机连接板 A，另一个已安装同类启动程序的板 B 已在 DFU；A 尚未完成重新枚举或没有进入 DFU。第一个枚举结果中只有 B。
- 后果：程序把 B 的序列号当作本次目标，备份并覆写 B 的应用，还可正常返回完成。固定随后命令的序列号只能防止选中以后漂移，不能证明选中的板就是 A。UI 提示“只连接一台”没有在代码中建立这种对应关系。`Send` 返回 true 也仅表示串口写入成功，并非设备已确认进入 DFU。
- 复现：`tests/usb_update/review_20261022/HostReviewRepro.cs` 直接编译生产升级类；回调表示对 A 发起切换，枚举返回唯一 B（`BBBB2222`）。结果：B 写入 1 次，所有设备命令固定 B，升级成功返回。见 `wrong-device-results.txt`。
- 建议：切换前记录可核对的设备身份，DFU 枚举后核对同一设备再读取或写入；无原串口连接的恢复流程也应明确绑定目标。补充“目标未出现、另一个 DFU 设备先出现”的拒绝测试。
- 现有测试缺口：`tests/usb_update/Runner.cs:90` 仅断言命令携带刚枚举出的 SN；没有来自切换前目标的身份输入。

### [P2] 托盘退出绕过升级期间的关窗保护

- 位置：`host/src/UI/FirmwareUpdatePanel.cs:51,59-60,84-89,99-102`；实际退出调用链为 `host/src/UI/TrayApp.cs:365-380` → `DisposeServices` 第 278 行直接 `Dispose` 设置窗口。
- 条件：后台升级仍在运行时，从托盘菜单选择退出。新增页面仅在设置窗口 `FormClosing` 时阻止关闭，未参与应用退出路径。
- 后果：窗口及面板可以在 `_busy=true` 时直接释放，应用随即退出。后台 Task 不再管理升级；若 CLI 已启动，它可能继续写入，但后续独立读回、重启和结果报告不再得到保证。这里没有执行真实写入时退出，因此不声称已实测损坏或遗留进程。
- 复现：生产 `FirmwareUpdatePanel` 加无硬件 `BridgeService` 替代对象；设置 `_busy=true` 后，`Form.Close` 被阻止，而托盘使用的 `Form.Dispose` 路径仍释放了窗口和面板。见 `tests/usb_update/review_20261022/ui-lifetime-results.txt`。
- 建议：把升级进行状态纳入应用级退出控制，在允许退出前等待操作结束，或按明确阶段安全结束操作；仅拦截设置窗口关闭不够。无需增设新的界面按钮。
- 现有测试缺口：`tests/usb_update/UiRunner.cs` 只渲染空闲页面，不运行升级与应用退出的交叉流程。

### [P2] 首次安装漏检 WRP1B，可先覆写启动区再发现应用无法写入

- 位置：`host/src/Updates/StLinkFirmwareInstaller.cs:40-55`，尤其第 41、45 行。读取了选项字节，但只检查 WRP1A；第 55 行已写启动程序。
- 条件：WRP1A 未启用，WRP1B 覆盖应用页而不覆盖启动页。例如 DBANK=1，WRP1B 的起止页均为 4，对应应用起点 `0x08002000`。
- 后果：现有检查全部通过，启动区域先被新启动程序覆盖，随后的应用写入才因原有保护失败。旧应用从 Flash 起点启动的布局已破坏，新布局的应用也未装好；原本可在写入前发现的不兼容配置变成需要人工恢复的半完成安装。已保存的备份有帮助，但不消除不必要的先行改写。
- 复现：生产首次安装类读取模拟选项字节，模拟设备仅拒绝被 WRP1B 保护的应用写入。结果：启动程序写入 1 次，应用写入被阻止 1 次，旧复位入口已被改写。见 `tests/usb_update/review_20261022/wrp-area-b-results.txt`。
- 建议：首次写入前检查所有将被擦写页的已有保护，包括 WRP1A、WRP1B；遇到冲突应先停止，不自动解除保护。PCROP 同样应纳入检查范围，但本次复现只证明了 WRP1B 的遗漏。
- 依据：[ST RM0440](https://www.st.com/resource/en/reference_manual/rm0440-stm32g4-series-advanced-armbased-32bit-mcus-stmicroelectronics.pdf) 的 WRP1 Area B option bytes 位于 `0x1FFF7820`，即当前读取数组的偏移 32；仓库内 ST 提供的 `stm32g474xx.h:5319-5324` 和 `stm32g4xx_hal_flash_ex.c:1261-1264` 也明确存在独立的 Area B 起止配置。
- 现有测试缺口：`tests/usb_update/Runner.cs:29-32,52-57` 只设置/检查 Area A，模拟写入仅复制字节，未执行页保护规则。

## 复现和边界

三项复现的程序和当时输出记录在 `tests/usb_update/review_20261022/`。输入应用和启动镜像取自完整解压的交付ZIP；新运行入口通过 `-PackageDirectory` 指定该目录，输出只写入忽略的 `artifacts/usb-review-*` 独立目录。最初测试时的完整原始目录仍保留在原电脑。

总体判断：建议先修复上述设备绑定、退出管理和安装前保护检查，再把功能审查标为通过。当前审查未证明实际 ROM DFU 枚举、驱动、按键进入恢复、烧录或长时间串口运行是否成功；这些应由主任务单独记录真实设备证据。


后续实机结果见 [异地开发接手与实机记录](USB_HANDOFF-20261022.md)。硬件通过的项目不消除上述三项离线复现的问题；源码未在本次整理中修复。
