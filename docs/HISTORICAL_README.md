# CodeX Tools —— STM32G474 硬件控制台 + Windows 上位机

## 1. 这是什么

一套为“使用 Codex 等 Agent 开发”设计的桌面小硬件方案：

- **STM32G474RBT3（原理图 Pin2Pin 使用 T6）**：USB CDC 与电脑通信，板上 OLED 显示 Codex 运行状态与额度，3 颗 WS2812S RGB 指示状态，8 路按键 + EC11 编码器（含按键）可自定义电脑操作。
- **Windows 上位机（单 EXE）**：读取本机 Codex 的额度与运行状态（`codex app-server`），查询多家 API 账户余额（DeepSeek/硅基流动/OpenRouter/自定义地址），把状态推送到设备显示；按键/编码器事件按用户配置执行白名单动作（快捷键、文本、音量、媒体键、聚焦窗口等）。

```
STM32 板 (OLED/RGB/按键/编码器)
   │  USB CDC (115200 8N1, A5 5A 帧协议)
   ▼
CodexToolsHost.exe (托盘常驻)
   ├─ codex app-server  ── Codex 状态 + 额度
   └─ 厂商余额接口 ── DeepSeek / 硅基流动 / OpenRouter / 自定义 (可选，需 API Key)
```

## 2. 已实现功能

| 功能 | 状态 | 说明 |
| --- | --- | --- |
| OLED 状态页 | 固件已实现 | Codex 状态（IDLE/RUN/WAIT/ERROR/DONE/OFF）、旋转动画、额度条；PRI/SEC 官方额度与 API 余额用双分隔线分区 |
| OLED 额度页 | 已合并进状态页 | Codex 主/次额度百分比条 + API 余额（原独立额度页已移除） |
| OLED 动画页 | 固件已实现 | 矩阵雨 / 正弦波 / 弹跳球（编码器长按或主机可切换） |
| OLED 跑马灯页 | 固件已实现 | 主机下发两行文本滚动 |
| OLED 关于页 | 固件已实现 | 固件版本、运行时间、主机连接状态 |
| RGB 指示灯 | 固件已实现 | 状态色 / 常亮 / 呼吸 / 彩虹 / 波动 / 闪烁 / 自检 / 关闭，参数由上位机下发；自检=红绿蓝白灭循环 |
| 按键（KEY1=PC3，KEY2~KEY8=PA0~PA6，编码器按键=PA7） | 固件+上位机 | 单击/双击/长按识别，事件上送，上位机按绑定执行动作；按下瞬间 OLED 显示 KEYx/ENC 本地反馈 |
| EC11 编码器 | 固件+上位机 | TIM1 编码器模式，旋转事件上送；默认旋转=音量，按下=切换 OLED 页 |
| 上位机配置界面 | 已实现 | 按键绑定、编码器动作、API 余额（多厂商）、RGB/OLED 默认参数 |
| Codex 额度/状态 | 已实测一次 | 通过 `codex app-server` 的 `account/rateLimits/read` + 状态通知 |
| API 余额 | 已实现 | DeepSeek `GET /user/balance`、硅基流动 `GET /v1/user/info`、OpenRouter `GET /v1/key` 预设 + 自定义地址，需用户填入对应 API Key |

## 3. 目录与关键文件

### 固件（在 CubeIDE 工程内，已就地修改）

工程位置：`C:\Users\Yiwen Zhu\Desktop\Floders\Code\CubeIDE_Workspace\20260801_CodeX_Tools`

新增：
- `Core/Inc/app_cfg.h` —— 硬件映射与应用参数（**上电前请核对**）
- `Core/Src/app.c` / `Core/Inc/app.h` —— 应用主逻辑、OLED 页面、状态机
- `Core/Src/oled.c` / `Core/Inc/oled.h` —— SSD1306/SH1106 I2C 驱动（128x64，5x7 字体）
- `Core/Src/rgb_ws2812.c` / `Core/Inc/rgb_ws2812.h` —— WS2812S 驱动（170MHz 内联汇编精确定时）
- `Core/Src/buttons.c` / `Core/Inc/buttons.h` —— 按键软件消抖与单击/双击/长按
- `Core/Src/encoder.c` / `Core/Inc/encoder.h` —— EC11 编码器（TIM1）
- `Core/Src/protocol.c` / `Core/Inc/protocol.h` —— A5 5A 帧协议（CRC8）

修改：
- `Core/Src/main.c` —— 调用 `app_init()`，主循环 `app_tick()`
- `USB_Device/App/usbd_cdc_if.c` —— 接收字节送入协议解析、发送完成回调
- `USB_Device/App/usbd_desc.c` —— USB 产品名改为 “CodeX Tools Bridge”

编译结果（已验证）：`0 errors, 0 warnings`，固件 `Debug/20260801_CodeX_Tools.elf`（约 52 KB）。

### 上位机

位置：`D:\Workspace\CodeX\CodeX_20260306\26-08-01 CodeX_Tools\host`

- `build.ps1` —— 用系统 csc（.NET Framework 4.8）编译单文件 EXE
- `dist\CodexToolsHost.exe` —— 已编译产物
- `src\` —— C# 源码（协议、串口、Codex/多厂商 API 数据源、动作分发、托盘、设置界面）
- 配置：运行后生成 `CodexToolsHost.json`（程序目录，不可写时用 `%LOCALAPPDATA%\CodexToolsHost`）

## 4. 快速上手

### 烧录固件

1. 用 STM32CubeIDE 打开工程 `20260801_CodeX_Tools`（工作区 `C:\Users\Yiwen Zhu\Desktop\Floders\Code\CubeIDE_Workspace`）。
2. 先按第 5 节核对硬件映射。
3. 编译（Project → Build 或本仓库命令行 headless 构建），用 ST-Link 下载。
4. 上电后 OLED 应显示 “CodeX Tools / FW 0.1”，PA15 心跳灯慢闪（主机未连接时为快闪）。

### 运行上位机

1. 双击 `host\dist\CodexToolsHost.exe`（托盘常驻，图标为咖啡杯）。
2. 首次运行建议打开托盘菜单“打开设置”：在“API 余额”页选择厂商并填入对应 API Key（可选）、检查按键绑定。
3. 插入设备后托盘气泡提示“设备已连接”，OLED 状态页显示 Codex 状态与额度。

命令行验证：

```powershell
# 自检（协议 CRC、帧往返、配置存取）
CodexToolsHost.exe --selfcheck-output <路径.json>

# 模拟设备闭环测试（无需硬件：验证连接握手、状态下发格式、按键/编码器事件分发）
CodexToolsHost.exe --mock-test-output <路径.json>

# 读取当前 Codex 额度并写入 JSON（真实调用 app-server）
CodexToolsHost.exe --probe-output <路径.json>

# 连接真实设备做闭环探针（INFO、OLED 探测、RGB 自检下发、按键注入回传）
CodexToolsHost.exe --device-probe-output <路径.json> --device-port COM7

# 用配置里保存的 Key 实测所选厂商的余额接口（不会输出 Key 本身）
CodexToolsHost.exe --deepseek-probe-output <路径.json>
```

模拟设备测试会检查：设备连接握手、INFO 解析、STATUS 帧格式（与固件一致的 22 字节头 + ASCII 文本）、RGB/OLED 配置下发、按键与编码器事件分发不崩溃。设备探针还会向固件发送按键注入帧（`MSG_BTN_INJECT`），验证“固件→USB→上位机”按键事件全链路。

固件协议模块还可在 PC 上直接测试（真实编译 `Core/Src/protocol.c`，独立 CRC 参考实现交叉验证）：

```powershell
powershell -File "tests\protocol_pc\run.ps1"
```

覆盖：组帧/CRC、有效帧解析、坏 CRC 回 ACK、干扰前缀、分片投递、100 帧突发、8 帧 TX 队列丢旧保新且不覆盖在途帧（16 项全部通过）。

## 5. 硬件核对清单（必须确认）

以下映射依据 `.ioc` 与原理图截图 OCR 推断，**上电前请对照立创 EDA 原理图逐项确认**：

| 项目 | 当前假设 | 核对点 |
| --- | --- | --- |
| RGB 数据脚 | PA8（原 HRTIM1_CHA1） | 原理图 “IN RGB” 是否接 PA8；若不是，改 `app_cfg.h` 中 `RGB_PORT/RGB_PIN` |
| RGB 型号 | 3 颗 WS2812S（GRB） | 若为普通 RGB LED 需换驱动；数量改 `RGB_LED_COUNT` |
| 编码器按键 | PA7（按钮索引 8） | 用户已确认；EC11 开关接 PA7 |
| OLED | I2C3 (PC8/PC9)，地址 0x3C | 若地址 0x3D 或驱动为 SH1106，改 `OLED_I2C_ADDR` / `OLED_DRIVER_TYPE` |
| 按键 | KEY1=PC3，KEY2~KEY8=PA0~PA6 低电平有效 | 用户已确认引脚映射；确认是接地按键 |
| LED0 | PA15 低电平点亮 | 若高电平点亮，反转 `app.c` 心跳的 `RESET/SET` |

## 6. 动作白名单

上位机只执行以下白名单动作（不开放任意命令执行）：

`none`、`hotkey`（如 `Ctrl+Shift+K`）、`text`（输入文本）、`launch`（启动程序）、`focus`（聚焦窗口标题）、`launchCodex`、`volumeUp/volumeDown/volumeMute`、`mediaPlayPause/mediaNext/mediaPrev`、`scrollUp/scrollDown`、`cycleOled`、`rgbStatus/rgbSolid/rgbBreath/rgbRainbow/rgbWave/rgbBlink/rgbOff`、`refreshQuota`、`showSettings`

## 7. 验证边界（诚实说明）

已验证：
- 固件 headless 编译通过：0 error / 0 warning（CubeIDE 1.15.0，G4 FW 1.6.3）。
- 2026-08-01 实机闭环（ST-Link 烧录 + COM7 探针）：OLED 探测 0x3C/SSD1306 正常、编码器旋转切页正常（用户实测）、RGB=3 颗、按键注入回传 2/2、协议帧全部有效。
- DeepSeek 官方 `/user/balance` 实测成功：`OK 25.75 CNY`（已修复 .NET JavaScriptSerializer 数组解析兼容问题）。
- 上位机编译通过，`--selfcheck` 全部通过（CRC8 已知向量 F4、帧编解码往返、配置存取往返）。
- 上位机 `--mock-test-output` 模拟设备闭环通过：连接、INFO、STATUS 帧校验、RGB/OLED 下发、按键/编码器事件分发均正常。
- 多厂商余额解析自检通过：DeepSeek（balance_infos）、硅基流动（data.totalBalance）、OpenRouter（limit_remaining 有限/无限）、自定义（available_balance）5 组官方文档示例响应全部解析正确；无法识别的响应会明确报错。
- DeepSeek 在线实测（2026-08-01）：重构为通用 API 适配后仍返回 `OK 18.00/17.98 CNY`，Key 未外泄。
- 固件协议模块 PC 可执行测试 16/16 通过（真实编译固件 `protocol.c`，非移植副本）。
- 额度链路实测一次：`primaryRemainingPercent=0`、`isStale=false`（探测当时主额度已用尽，接口本身工作正常）。

未验证（需要你的硬件/账号配合）：
- **RGB 视觉确认**：✅ 已实测通过（2026-08-01）：上电自检红/绿/蓝分灯循环显示正确，时序为运行时自校准（T0H≈266ns、T1H≈905ns）。
- **按键实按**：✅ 已实测：物理按键事件经 USB 上报（探针捕获 KEY2/KEY8 的按下/释放/单击），OLED 本地显示 KEYx/ENC 反馈。
- 按键绑定到实际窗口（如“呼出 Codex”）依赖目标窗口标题，可能需要按你本机情况调整。
- 若 OLED 不亮：先检查地址（0x3C/0x3D）与驱动（SSD1306/SH1106），再检查 I2C 上拉电阻。

## 9. 2026-08-01 联调修复记录

- **RGB 恒白（最终修复）**：多层根因——(1) Debug -O0 下 DWT 软延时的调用开销使 0 码高电平落入“1”区间（全白）；(2) 改为内联汇编后，理论循环模型与实际执行开销（O2 编译路径）存在偏差，多版“理论达标”实测仍越界；(3) 最终方案是**运行时自校准**：用 GPIO IDR 回读 + 两点采样（预热后取 100/200 轮）实测每轮循环的真实周期数与固定开销，再按手册目标（0 码 270ns、1 码 920ns、0 码低 850ns、1 码低 300ns）反推循环数；同时用 USB SOF（主机锁定 1ms 基准）实测主频 169MHz。校准后 OLED 关于页显示 T0H≈266ns、T1H≈905ns，上电自检红/绿/蓝循环正确。
- 附加结论：灯珠 VDD 实测 4.2V（输入二极管压降）→ VIH≈2.98V，3.3V 推挽信号本身达标，无需电平转换；1.5K 上拉保留无害。
- **按键无反应**：原因之一为主机离线时普通按键无任何本地反馈（仅编码器有兜底切页）。新增按下瞬间 OLED 全屏显示 KEYx/ENC；新增 `MSG_BTN_INJECT` 协议帧用于闭环测试；默认新增 btn2=刷新额度、btn3=打开设置。
- **DeepSeek n/a**：`JavaScriptSerializer` 数组运行时类型为 `object[]` 而非 `ArrayList`，原强转失败。改用 `IList` 兼容两者，并补 TLS1.2、错误详情、CLI 探测。
- 探针新增按键事件统计与注入测试；设置界面 RGB 模式新增 `test`（自检循环）。

## 10. 2026-08-01 第二轮调整

- 上电默认恢复状态模式（离线暗蓝），不再显示 RGB 自检循环。
- 动画页改为 3D 旋转线框几何体：立方体 / 八面体 / 三环球体（编码器长按或主机 param 切换）。
- 按键反馈改为右上角 3x3 小图标（400ms），不再全屏打断；按键响应时间减半（消抖 10ms、长按 400ms、双击窗口 160ms）。
- 修复“呼出设置卡住”：隐藏主窗体句柄未创建时，串口线程直接创建 SettingsForm 导致无消息泵假死；现强制启动时创建句柄并按 InvokeRequired 封送到 UI 线程。

## 11. 2026-08-01 第三轮调整

- RGB 配置写入 Flash 持久化（最后一页 2KB，magic+CRC）：开机直接使用上次配置（当前为粉色波浪 #FF80C0/#408080）；配置变更即保存，自检模式不写入。亮度整体减半（设置值 ÷2，最大 127），避免刺眼。
- 按键只保留单击：去掉长按/双击识别（误判长按导致单击失效的问题），按下即上报、释放立即触发单击；上位机绑定列表同步只保留 click 项。
- 删除跑马灯页（固件+上位机同步），OLED 页面现为：0 状态 / 1 额度 / 2 动画 / 3 关于。
- 动画页：左侧火柴人格斗小人（防守/右拳/左拳/踢腿循环），右侧 3D 旋转几何体（立方体/八面体/球体）。
- 修复 `rgb_set_config` 将 TEST 模式误钳制为 OFF 的问题。

## 12. 2026-08-01 第四轮调整

- OLED 状态页：在 CodeX 官方额度（PRI/SEC）与 API 余额之间增加第二条分隔线；API 行由 “DSK” 改为 `API <币种><余额>`，OpenRouter 等无上限额度显示 `API U/L`（STATUS flags bit0）。
- 上位机余额查询泛化为多厂商适配（保留 `deepSeekApiKey/deepSeekBaseUrl` 旧配置兼容）：
  - DeepSeek：`GET {base}/user/balance`（balance_infos[].total_balance）
  - 硅基流动：`GET {base}/v1/user/info`（data.totalBalance，币种默认 CNY）
  - OpenRouter：`GET {base}/v1/key`（data.limit_remaining，null 视为无限额度）
  - 自定义：按用户填写的完整地址请求，从 total_balance / totalBalance / limit_remaining / available_balance / balance 等字段识别。
- 设置界面新增“API 余额”页：厂商下拉自动填入官方查询地址，支持“测试余额”按钮与自定义地址。

## 8. 后续建议

- 将 USB 从 CDC 升级为复合 HID（Keyboard + Consumer Control + Custom HID），使按键/音量在无上位机时也能工作。
- 通过 app-server 的审批/选项通知实现硬件侧“确认/拒绝”按钮。
- OLED 增加中文小字体或图标库。

协议细节见 [PROTOCOL.md](PROTOCOL.md)。
