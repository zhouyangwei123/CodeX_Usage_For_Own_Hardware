# CodeX Usage

本分支当前为[App0.12温和三档随机时钟试验](docs/OLED_GENTLE-20261026.md)，保留1Hz用量页；用户反馈此前[App0.11仅降频](docs/OLED_1HZ-20261023.md)未显著改善噪音。下面保留正式Host0.6.2与App0.10交付基线；本次试验未改写正式Release。

Windows 上位机与 STM32G474 桌面工具的联合仓库。当前版本：上位机 **0.6.2**、应用固件 **0.10**、启动程序 **1.0**。[正式发布与更新方法](docs/RELEASE-0.6.2.md)、[上位机下载](deliverables/host-0.6.2/README.md)。0.6.2修复分页日志遗漏，启动自动补回最近一小时真实用量；默认显示本机Token用量，可手动切换账号日汇总，新配置默认像素OLED皮肤。实测范围见[历史曲线修复](docs/USAGE_HISTORY-20261023.md)和[0.6.1 USB与OLED验收](docs/USB_OLED-20261023.md)。

0.10已通过USB写入并回读校验，启动区与保护设置保持。0.5.1/App0.9历史验证保留在[安全修复与验收](docs/USB_SAFETY-20261023.md)。悬停信息窗仍已移除；账号接口的日汇总不作为本机分钟活动的替代。

上一轮 [实机接手记录](docs/USB_HANDOFF-20261022.md) 与 [0.5.0整理包](deliverables/usb-update/README.md) 保留作历史依据。安全修复分支为`fix/usb-update-safety-20261023`，本轮分支为`feat/account-oled-pixel-20261023`，默认main仍是旧版。已安装Boot1.0的板无需重复首次安装；以后通过USB更新应用，按住KEY1上电可恢复。0.10保留已有串口稳定性修复。

上一阶段的 [串口稳定性验证](docs/USB_STABILITY-20261022.md) 保留作问题定位依据。以下历史说明中的固件冻结和校验边界对应原版。

远端：https://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware

先阅读 [README_先看.md](README_先看.md)，开发接手阅读 [CODEX_接手说明.md](CODEX_接手说明.md)。

正式发布包为 [0.6.2 Release](https://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/v0.6.2)。软件检查GitHub正式Release，不跟踪main或功能分支；下载与安装由用户手动完成。旧版验证保留在[0.4.2记录](docs/VALIDATION-0.4.2.md)和[0.5.1测试包及实测范围](deliverables/usb-update-0.5.1/README.md)。

- `host/`：WinForms 上位机，额度监控、电脑指标、按键/编码器配置与可选集成。
- `firmware/STM32G474/`：CubeIDE 固件工程，2026-09-24 原样导入。
- `docs/PROTOCOL.md`：当前串口协议。
- `docs/2026-09-24-proposal.md`：接手检查与分阶段优化方案。
- `docs/firmware-baseline.sha256`：固件逐文件校验清单。
- `tests/`、`tools/`：测试与交付工具。

## 构建上位机

在 Windows PowerShell 中执行 `./host/build.ps1`，发布程序生成于 `host/release/CodexToolsHost.exe`。
需要 Windows 内置 .NET Framework 4.8 编译器；LibreHardwareMonitor 依赖在构建时嵌入单文件。

## 数据与验证边界

个人配置保留在本机，不纳入 Git。历史说明位于 `docs/HISTORICAL_README.md`，其中早期路径或硬件信息可能已过时。
本分支保留真实测试过的程序字节，补充了 [问题清单](docs/USB_REVIEW-20261022.md) 与 [原始证据](docs/evidence/usb-update-20261022/README.md)。测试过程中出现的失败和未确定原因也一并保留；5分钟通信不能代替8小时运行、睡眠唤醒或反复物理拔插验收。

本地 `baseline` 分支保存导入时的工程，后续更改在功能分支进行。导入提交由 Codex 工具署名，不修改用户全局 Git 身份。

## 0.4.2 的主要变化

- 浮窗不保留最小化状态；取消置顶后允许普通应用遮挡，自动维持在桌面之上。
- 移除 0.4.1 新增的找回菜单与托盘单击行为，保留原有显示/隐藏选项及双击打开设置。
- 桌面层级变化由现有一秒刷新周期检查，不抢焦点、不自动开启置顶。点击非置顶浮窗仍能正常激活。

## 0.4.0 的主要变化

- 玻璃浮窗新增最近一小时 Token 曲线，10 秒一个点，统计各时刻最近 60 秒用量。
- 小箭头一键折叠曲线并记住状态；底部 API、Token 输入/输出、网络上下行三列始终可见。
- 区分扫描中、部分记录、读取失败与正常空闲；首次历史加载不会产生实时虚假峰值。
- 修复边缘展开或缩放时跨屏跳回主屏的问题；固件保持原样。

使用与统计口径见 [Token 活动说明](docs/TOKEN_ACTIVITY.md)。

## 0.3.0 的主要变化

- 额度浮窗改用逐像素透明边缘，提供柔光玻璃、极简清晰、经典双色；设置中可预览，托盘可直接切换。
- 新增本机 Token 用量清单、增量读取、日期/会话筛选与 CSV 导出；ccusage 20.0.22 JSON 可作为独立来源导入。
- 新增免登录 GitHub 更新检查，启动后 30 秒检查、正常情况下每 12 小时检查；可关闭，下载和安装由用户手动完成。
- 延续 0.2.0 的额度稳定性修复；固件保持原样。

使用细节及数据范围见 [第二阶段功能说明](docs/PHASE_TWO.md)。

## 0.2.0 的主要变化

- 修复额度空响应覆盖、其他额度类别串入、刷新排队和连接恢复问题。
- 额度可明确显示更新时间、陈旧状态与实际窗口时长。
- 配置界面改为五项左侧导航，按键和旋钮合并。
- 移除 Codex 的 DeepSeek/官方账号切换功能；独立 API 余额查询保留。
- 配置保存保留上一版备份，失败不会删除原文件。
- 固件原样纳管，不修改或烧录。

0.2.0 的连续两小时记录保留在 [历史验证记录](docs/VALIDATION.md)。早期方案见 [docs/NEXT_STAGE.md](docs/NEXT_STAGE.md)，实际交付以当前版本说明为准。
