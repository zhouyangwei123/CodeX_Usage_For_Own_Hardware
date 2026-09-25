# 用量清单、平滑玻璃浮窗与更新检查 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement task-by-task. Independent modules may use dispatching-parallel-agents with exclusive file ownership.

**Goal:** 在已发布 0.2.0 基础上交付本机 Token 用量清单、消除额度浮窗外轮廓锯齿、可切换玻璃/极简/经典样式，以及免登录 GitHub 更新检查。

**Architecture:** 原生 C# 读取本机统计，ccusage JSON 用于可选导入和对账。逐像素 Alpha 浮窗负责平滑外轮廓，独立更新服务只查询公开 Releases。三个模块通过 TrayApp/SettingsForm 集成，不改变额度 RPC 或固件协议。

**Tech Stack:** .NET Framework 4.8 / WinForms / GDI+ / Win32 UpdateLayeredWindow / GitHub REST。

## Global Constraints

- 本轮固件仅原样纳管。不得修改 `firmware/`、烧录设备或改写其构建输出。
- 上位机保持 .NET Framework 4.8 / WinForms、单 EXE 发布。
- 用户配置、API Key、Codex 认证数据和米家登录资料不可入库或输出到日志。
- 协议兼容：STATUS 保持已有字段；PC_METRICS v3 固定发送 28 字节。
- 不播放声音；通知默认静音。
- 不自动下载替换程序，不改变 GitHub 登录或 Codex 登录。
- 测试默认使用模拟设备；没有硬件时不得声称完成真实设备验收。
- 现有原工程源码保持不变，新代码在专用仓库与功能分支开发。

## Task 1: 原生用量服务与清单

Files: new `host/src/Usage/*.cs`, `host/src/UI/UsagePanel.cs`, `tests/usage/*.cs`, `tests/usage/run.ps1`.
Ownership: usage agent only; do not modify AppConfig/SettingsForm/TrayApp/Program/build files.

Public integration contract:
```csharp
// namespace CodexToolsHost.Usage
public sealed class LocalUsageService : IDisposable {
    public LocalUsageService(string codexHome);
    public event Action Changed;
    public UsageReport Report { get; }
    public void Start();
    public Task RefreshAsync();
    public void Dispose();
}
// namespace CodexToolsHost.UI; service owned by TrayApp
public sealed class UsagePanel : UserControl {
    public UsagePanel(LocalUsageService service);
}
```

- [ ] Write and run failing tests for cumulative-event dedupe, duplicate active/archive copies, parent-prefix forks, last-token-only events, partial final lines, truncation, malformed/negative counts and cancellation. Use synthetic JSONL, never copied prompts.
- [ ] Implement native aggregate/incremental scan of `sessions` and `archived_sessions`; resolve explicit CODEX_HOME then user profile `.codex`. Read metadata/token events only. Preserve file offsets/incomplete lines, coalesce refreshes; do not scan all content each tick. Known fork prefixes must not count as new usage. Flag uncertain coverage rather than silently inventing exact totals.
- [ ] Read current ccusage upstream JSON schema; implement optional JSON import as a separately labelled source. Lock any comparison CLI version in documentation. Do not auto-install or launch unpinned npm packages from the app.
- [ ] Build a readable native list with today/7 days/month/session views, input/cache/output/total, latest update, model and CSV export. Cache is a subset of input; output reasoning is not added twice. Unknown pricing remains unknown. Subscription quota is never added to local Token totals.
- [ ] Run focused tests and a bounded real read with summaries only, including repeat-refresh no-double-count/performance evidence; save report to `review/phase-two-usage-report.md`.

## Task 2: 逐像素透明浮窗与样式

Files: `host/src/UI/QuotaHudForm.cs`, `QuotaHudRenderer.cs`, new `LayeredWindowSurface.cs`, focused tests under `tests/hud_render/`.
Ownership: HUD agent only; root owns config and settings integration.

Integration: read `AppConfig.QuotaHudStyle` (root adds string property, values `glass`, `minimal`, `classic`, default `glass`). Preserve existing HUD constructor and presentation formatting.

- [ ] Demonstrate current binary Region clipping cannot retain fractional edge alpha. Add offscreen image checks at 60/75/90/100% scales and empty/0/1/50/100% quota states, before implementation.
- [ ] Replace Region clipping with premultiplied per-pixel alpha via `UpdateLayeredWindow`, without `Form.Opacity` or color-key transparency. Release every HDC/HBITMAP in finally; ensure no GDI handles accumulate.
- [ ] Render smooth rounded perimeter, clear labels and narrow proportionate tracks; glass uses layered translucency, restrained highlights and gradient fill; minimal uses flat high-contrast colors; classic preserves recognizability with smooth edges. Keep API/OpenCode/network data, refresh click, dragging, topmost, scale and opacity settings functional. Text rendering on alpha surfaces must avoid ClearType fringes.
- [ ] Repaint only on changed data/settings or active refresh animation; hidden/stationary HUD must not run perpetual decorative animation. Use opaque minimal fallback if native composition fails/high contrast demands it; no undocumented blur APIs or desktop capture.
- [ ] Test rendering/state edge cases, lifecycle, native alpha upload on an offscreen window, GDI resource stability; save actual rendered PNGs and report `review/phase-two-hud-report.md`.

## Task 3: 更新服务与设置面板

Files: new `host/src/Updates/*.cs`, `host/src/UI/UpdateSettingsPanel.cs`, `tests/updates/*.cs`, `tests/updates/run.ps1`.
Ownership: updates agent only; root owns lifetime/config integration.

Public integration contract:
```csharp
// namespace CodexToolsHost.Updates
public sealed class ReleaseUpdateService : IDisposable {
    public ReleaseUpdateService(Version currentVersion, string cacheDirectory);
    public event Action Changed;
    public void Start(bool enabled);
    public void SetEnabled(bool enabled);
    public Task CheckAsync();
    public void Dispose();
}
// callback updates config preference and saves through existing atomic Save
public sealed class UpdateSettingsPanel : UserControl {
    public UpdateSettingsPanel(ReleaseUpdateService service, bool enabled, Action<bool> saveEnabled);
}
```

- [ ] Tests before implementation: semantic numeric versions, draft/prerelease exclusion, 404 vs latest, 304 cached response, timeout/error retaining last success, concurrent check coalescing, cancellation/disposal and foreign/unsafe links.
- [ ] Query only `https://api.github.com/repos/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/latest`, no token. Start delayed (30 seconds), check every 12 hours, use ETag, 15-second timeout, bounded response, cancellation and failure backoff. Keep last successful result/time separate from failed check status.
- [ ] Native settings panel shows current/latest version, check status/time, checkbox and manual check; opens verified HTTPS release page only on click. No automatic browser launch, binary replacement or messages to other people.
- [ ] Run focused tests and one real anonymous API check, save `review/phase-two-updates-report.md`.

## Task 4: 集成与交付

Files: AppConfig.cs, TrayApp.cs, SettingsForm.cs, Program.cs, AssemblyInfo.cs; docs and tests of new integration.
Ownership: root only.

- [ ] Add config fields `quotaHudStyle` and `updateChecksEnabled`, normalize unknown values safely, preserve existing fields/keys and atomic backup behavior; add targeted round-trip/default tests.
- [ ] TrayApp owns services exactly once and disposes them on exit; SettingsForm has a compatibility constructor and an overload accepting services. Add native 用量清单 navigation and update settings under 诊断与设置. Add HUD style/scale/opacity controls in 显示与灯效 and tray style selector.
- [ ] Build final binary; run existing selfcheck/mock/UI checks plus three focused suites. Verify firmware manifest and unchanged diff. Inspect native renderings; real DPI-switch/hardware acceptance stays explicitly unverified.
- [ ] Review full diff independently, resolve findings, then package 0.3.0 with allowlisted files, backup/deploy preserving config, commit/push/release and verify downloaded hash. Do not rerun a two-hour quota soak unless a change to that chain or new failure justifies it.

## Source references

- https://ccusage.com/guide/codex/
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow
- https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type
- https://docs.github.com/en/rest/releases/releases#get-the-latest-release
