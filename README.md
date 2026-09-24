# CodeX Usage

Windows 上位机与 STM32G474 桌面工具的联合仓库。上位机当前版本：0.2.0。

远端：https://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware

先阅读 [README_先看.md](README_先看.md)，开发接手阅读 [CODEX_接手说明.md](CODEX_接手说明.md)。

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

## 0.2.0 的主要变化

- 修复额度空响应覆盖、其他额度类别串入、刷新排队和连接恢复问题。
- 额度可明确显示更新时间、陈旧状态与实际窗口时长。
- 配置界面改为五项左侧导航，按键和旋钮合并。
- 移除 Codex 的 DeepSeek/官方账号切换功能；独立 API 余额查询保留。
- 配置保存保留上一版备份，失败不会删除原文件。
- 固件原样纳管，不修改或烧录。

下一阶段计划见 [docs/NEXT_STAGE.md](docs/NEXT_STAGE.md)。
