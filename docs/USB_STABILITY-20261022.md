# USB 串口稳定性修复验证（2026-10-22）

状态：代码修复、回归验证及固件/上位机构建完成；尚未烧录设备，尚未完成真实硬件长期验收。

## 依据与范围

用户确认原版失联时硬件仍能响应或变化，完整断电再上电可恢复连接。前序独立探测能打开 COM6 并写入三次 PING，但接收字节为零。原版上位机为 0.4.2，二进制校验值与发布记录一致；原进程缓存的设备 INFO 版本为 0.7，这不是设备 Flash 的逐字节校验。

基于提交 `e57097a983a8246f795d7301f72a61356ed38cc1` / v0.4.2 修复。本次用户已要求解决已定位的稳定性问题，范围包括必要的固件源码修复；仓库原有“本轮固件仅原样纳管”描述对应历史导入工作。原始 `docs/firmware-baseline.sha256` 保留用于比较，本修复不再宣称固件逐字节保持导入基线。

## 实现

固件版本 0.8：

- 删除协议层独立的 `tx_busy` 和完成回调，统一以 ST CDC 的 `TxState` 判断在途传输。ST 库在发送完成及重新初始化时更新该状态，协议层不再因应用回调丢失或抢先执行而永久卡住。
- CDC 仅在设备已配置、类对象存在且传输空闲时接受帧；接受后复制至专用 USB 发送缓冲区。短临界区保护类对象寿命、缓冲区及提交过程，并恢复调用者原先的中断屏蔽状态。临界区没有等待。
- USB 发送忙或失败时，协议队列保留该帧；成功接受后才出队。在途缓冲区不会被新帧或队列溢出覆盖。
- 超过协议上限 128 字节的接收帧被拒绝，避免原先对 128 字节数组的越界写。
- 补齐 Release 缺失的 `Core/My_Def` 头文件目录，并将 Debug 对应项改为相对路径。硬件引脚、时钟、灯效和业务数据格式未改动。

上位机版本 0.4.3：

- 自动模式依据 Windows 串口映射跳过 `BthModem` 蓝牙虚拟端口，手动指定端口仍可使用。
- 打开/握手失败时显示端口号、失败阶段及异常原因。
- 本机实测识别 COM4、COM5 为蓝牙端口，自动候选为 COM6。
- 没有引入强杀驱动调用、无界重试线程或强制复位硬件。底层驱动永久阻塞不属于本次已验证修复范围。

## 验证

| 验证 | 结果 |
|---|---|
| 同一组5项关键回归用于原版 | 5项失败，复现缺陷 |
| 原版 protocol.c 与 CDC 包装层共同替换为修复版 | 同5项全部通过 |
| 新增 USB 传输测试 | 22项通过 |
| 压力场景 | 100000次传输、1000次模拟USB重新枚举，通过 |
| 既有协议测试 | 16项通过 |
| 上位机 EXE 自检 | 86个布尔检查全部通过，退出码0 |
| 模拟设备闭环 | 4个布尔检查全部通过，退出码0 |
| STM32 Release 构建 | 0错误、0警告 |
| 固件规模 | text 44028 B，data 560 B，bss 13888 B |

新增测试直接编译生产 `protocol.c` 与 `usbd_cdc_if.c`，替换 USB 驱动和 Cortex 中断原语；不以重写的队列实现代替生产实现。覆盖回调丢失、完成抢先、发送忙/失败重试、重新枚举、在途数据保护、未配置/类对象为空、挂起/恢复、中断屏蔽保持、非法长度及后续合法接收。USB 端点传输由测试桩模拟，因此这些结果不能代替真实 USB/MCU 的电气与时序验证。

构建环境：本机 STM32CubeIDE（安装目录名 1.15.1，启动器显示 1.16.0）、GNU Arm 工具链 12.3.rel1；Windows .NET Framework C# 编译器；MinGW gcc。

## 重现命令

在仓库根目录执行：

```powershell
./tests/usb_transport/run.ps1
./tests/protocol_pc/run.ps1
./host/build.ps1
```

对照测试先从基线导出 `firmware/STM32G474`，再运行：

```powershell
./tests/usb_transport/run.ps1 -SourceRoot <基线固件目录> -RegressionsOnly
```

基线预期退出码1，修复版预期退出码0。实机验证不要使用旧版调查脚本替代新版回归测试。

固件可导入 STM32CubeIDE 项目 `firmware/STM32G474`，选择 `20260801_CodeX_Tools/Release` 构建。本机已验证的无界面命令如下：

```powershell
& 'D:\Software\Code\ST\STM32CubeIDE_1.15.1\STM32CubeIDE\stm32cubeidec.exe' --launcher.suppressErrors -nosplash -application org.eclipse.cdt.managedbuilder.core.headlessbuild -data 'D:\Local_Work_Temp\NewMarTool_Build_20261022\workspace' -import "$PWD\firmware\STM32G474" -build '20260801_CodeX_Tools/Release'
```

测试记录位于 `artifacts/usb-transport/` 与 `artifacts/host-checks/`。已构建固件为 `firmware/STM32G474/Release/20260801_CodeX_Tools.elf`，使用同一工具链 objcopy 转换 HEX/BIN。

## 部署与验收

本次未覆盖安装目录 EXE、未修改用户配置、未烧录或改变设备选项字节。STM32CubeProgrammer 枚举结果：当前没有 ST-Link、J-Link、DFU 连接。

烧录前识别实际 MCU，读取并保存设备现有 Flash 备份；源工程目标为 STM32G474RBTx，128 KiB Flash，应用地址 0x08000000。应以实物识别结果确认目标。优先使用携带地址的 HEX/ELF，写入后执行校验，再复位并确认设备 INFO 报告 FW 0.8。

上位机替换前退出原进程、备份原 EXE，沿用原配置文件，不将配置复制进发布包。只更新上位机不包含本次核心固件修复。

真实硬件待验收：正常持续运行至少8小时；睡眠/唤醒20轮；USB断开/重新连接20轮；上位机退出/启动及手动重连20轮。记录每次恢复耗时、握手失败原因、最终连接状态，检查旋钮/按键/OLED/RGB仍正常。以上数字是建议验收计划，尚未执行。
