# CodeX Usage 接手说明

## 工程状态

- 仓库远端：`https://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware.git`。
- 导入基线：`a0d159f`，标签 `baseline-2026-09-24`。
- 上位机修复提交：`4b062f5`；界面与配置改进提交：`497c023`。
- 上位机 .NET Framework 4.8 / WinForms，发布版本 0.4.1，单 EXE。
- 原始固件 152 文件逐字节导入。遵守 `AGENTS.md` 的本轮固件冻结约束。

## 核心设计

`CodexStatusProvider` 的单个 supervisor 管理自有 app-server 子进程。周期与手动刷新共享一个进行中的任务；循环在请求结束后再等待下一轮。连接退出、停止、取消会清理 pending RPC。stdout/stderr 都有读取线程，stderr 使用固定大小缓冲区且不保存原文。

`QuotaParser` 区分完整响应、稀疏通知、字段缺失和显式 null，只接受 Codex 桶。窗口时长和采样时间在快照中保留。账户身份变化后失效旧数据；身份确认前不接收额度推送。

真实 Codex 会在初始化阶段发送 `account/updated`。这会改变账户版本，应在同一连接重新读取账户；不可把它当作断线，否则每次重连都会再次收到通知并形成循环。该行为已加入回归测试。

`AppConfig.Save` 使用唯一临时文件、落盘刷新、带备份的 File.Replace。失败只清理自己的临时文件，不删除原配置。旧 `toggleDeepSeek` 绑定迁移为 `none`；不能恢复已经移除的 Codex 全局配置写入功能。

## 验证入口

```powershell
./host/build.ps1
./tests/quota_reliability/run.ps1
./artifacts/quota-tests/quota-tests.exe --cycles 100
./tests/config_reliability/run.ps1
./tests/protocol_pc/run.ps1
./tools/verify-firmware.ps1
./tools/start-host-soak.ps1 -Seconds 7200 -RunName host-soak
./tests/usage/run.ps1
./tests/updates/run.ps1
./tests/hud_render/run.ps1
./tests/hud_render/run.ps1 -Lifecycle
./tests/phase_two_integration/config.ps1
./tests/phase_two_integration/runtime.ps1 -Seconds 90 -RunName phase-two-runtime
./tools/test-token-activity.ps1
./tests/token_hud/run.ps1
./tests/hud_activation/run.ps1
```

GUI EXE 自检需使用 `Start-Process -Wait -PassThru -WindowStyle Hidden` 并检查退出码及 JSON：

- `--selfcheck-output <json>`：已有功能自检。
- `--mock-test-output <json>`：模拟设备闭环。
- `--ui-batch-selfcheck-output <json>`：页面、绑定迁移、隐藏 API 页状态回显和总览数据行为；并生成离屏预览。
- `--phase-two-selfcheck-output <json>`：用量/更新页面挂接、HUD 外观保存/回滚/同步、服务所有权及新版原生界面预览。

不要用 `--device-probe-output` 代替无硬件测试。持续验证脚本使用实际账户、实际 PC 指标和 MockDeviceLink，不接触物理串口，不播放声音，不使用真实 API 余额 Key。

测试 EXE 和被测 CodexToolsHost.exe 必须放在同一个独立目录，并检查 `Assembly.Location`/SHA-256，避免运行时加载旧的同名程序。

## 发布与实际运行

`host/release/` 是构建产物，Git 忽略。发布时保留第三方说明，并记录 EXE 的 SHA-256。上位机版本与固件版本分开。

此电脑继续沿用旧启动位置的发布 EXE，以保留现有使用习惯；新源码以本仓库为准，旧源码目录仅保留为参考。部署前备份旧 EXE 和用户配置，确认密钥保持不变，再启动新版。

当前项目内 `artifacts/` 是本地验证记录，包含运行日志、离屏图及发布测试快照，默认不上传。公开验证摘要不得包含账户标识、API Key、认证日志或私人会话内容。

0.4.1 验收结果以 `docs/VALIDATION-0.4.1.md` 为准，0.4.0、0.3.0 与 0.2.0 的历史验证记录继续保留。不将旧版本持续时长套用到新版。固件和物理设备验收始终独立记录。

0.3.0 配置版本为 10，新增 `quotaHudStyle` 与 `updateChecksEnabled`，旧字段和密钥保留。`TrayApp` 独占两个新服务的生命周期；设置面板只订阅、取消订阅。HUD 不得重新使用 Region/Form.Opacity/颜色键；原生回退、无空闲装饰动画和 GDI 资源释放必须继续测试。

`UsageScanner` 只读增量日志，缓存数值事件，明确部分覆盖；`CcusageImport` 锁定 20.0.22 的 Codex 专用 schema，不猜测旧版字段含义。`ReleaseUpdateService` 只查询公开版本，不自动下载、替换程序或打开浏览器。功能与范围见 `docs/PHASE_TWO.md`。

0.4.0 配置版本为 11，新增 `quotaHudChartExpanded`。`UsageActivity.Create(report, now)` 是纯数值投影，使用事件原始时间、前 60 秒滚动窗口和 10 秒图表端点，不读取磁盘或查询接口。`UsageReport.RecentActivity` 只保留时间及输入/输出数值。新布局由 `QuotaHudActivityRenderer` 负责，`QuotaHudActivity` 负责共享服务订阅、折叠保存与视图定时器；不让 HUD 拥有/释放 `LocalUsageService`。旧构造和旧渲染入口仅用于兼容已有调用，正式托盘传入共享服务使用新布局。详细口径见 `docs/TOKEN_ACTIVITY.md`。

0.4.1 区分主动找回 `ShowFromTray()` 与后台显示 `ShowPassive()`。`WS_EX_NOACTIVATE` 和 `WM_MOUSEACTIVATE` 的策略必须随置顶选项同步；不要为了找回临时启用 TopMost。最小化时跳过位置持久化、绘制和视图计时器。Framework 的 `MinimumSize` setter 会激活已有 HWND，固定跟踪尺寸改由 `WM_GETMINMAXINFO` 提供，避免被动尺寸同步抢焦点。激活测试在独立的非显示桌面上验证，不操作用户桌面。
