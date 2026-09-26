# CodeX Usage

Windows 上位机与 STM32G474 桌面工具的联合仓库。上位机当前版本：0.4.1。

远端：https://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware

先阅读 [README_先看.md](README_先看.md)，开发接手阅读 [CODEX_接手说明.md](CODEX_接手说明.md)。

Windows 便携发布包见 [0.4.1 Release](https://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/v0.4.1)。最新验证结果见 [0.4.1 验证记录](docs/VALIDATION-0.4.1.md)。

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
当前不修改或烧录固件；设备不在手边时仅验证上位机与模拟通讯。

本地 `baseline` 分支保存导入时的工程，后续更改在功能分支进行。导入提交由 Codex 工具署名，不修改用户全局 Git 身份。

## 0.4.1 的主要变化

- 取消“始终置顶”后，点击浮窗可正常激活并前置。
- 单击托盘图标，或右击选择“找回浮窗（恢复并前置）”，可恢复被遮挡、隐藏或最小化的浮窗；不会重新开启置顶。
- 最小化不再覆盖已保存位置或继续绘制；后台显示与尺寸同步保持不抢焦点。双击托盘仍打开设置。

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
