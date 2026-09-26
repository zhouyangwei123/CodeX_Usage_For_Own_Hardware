# 0.4.1 浮窗激活修复验证

日期：2026-09-26。范围为上位机窗口行为，不修改固件、协议、Token 统计或配置 schema（仍为 11）。

## 根因与修复

对运行中的 0.4.0 自有 HUD 句柄做只读检查，得到 `visible=True, minimized=False, noActivate=True, topMost=False`。因此这次现场并非真的最小化，而是取消置顶后仍保留禁止点击激活的窗口策略。另用原生最小化复现了无法从托盘正确还原、最小化坐标覆盖正常位置的问题。

修复使 `WS_EX_NOACTIVATE` / `WM_MOUSEACTIVATE` 随置顶策略同步。主动找回采用无激活的原生还原和层级提升，仅非置顶模式再明确激活；被动同步保持最小化状态且不抢焦点。最小化时不保存图标坐标、不绘制或运行视图计时器。

Framework 的 `MinimumSize` setter 会激活已有 HWND；现改用 `WM_GETMINMAXINFO` 提供固定跟踪尺寸，保留窗口尺寸限制。托盘增加“找回浮窗（恢复并前置）”，单击托盘执行找回，双击仍打开设置。

参考 Windows 原生契约：[扩展窗口样式](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)、[WM_MOUSEACTIVATE](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate)、[ShowWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow)、[SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)。

## 复现与回归

- 新测试在独立的原生 Windows 桌面创建自有窗口，**从未切换显示桌面**，不移动鼠标、不操作用户应用、不播放声音。主 STA 继续处理框架清理消息，避免测试线程结束时等待。
- 原 0.4.0 首轮 34 项检查中 17 项失败，覆盖点击策略、原生最小化、坐标保存、窗口尺寸、视图计时器及托盘找回入口。
- 最终 **49 项全部通过**：两种 HUD 入口、运行时取消/恢复置顶、点击激活策略、被遮挡/隐藏/最小化后的主动找回、正常尺寸和位置、非置顶不被重开、置顶恢复不抢焦点、被动最小化保持、后台显示/尺寸同步不抢焦点、固定 native tracking 范围、托盘入口。
- 独立审查复现一项 P2：`WindowState=Normal` 和 `BringToFront` 会意外激活置顶窗口。改为原生不激活还原/提升后，审查者独立重跑最终 EXE，49 项全部通过，无剩余 P1/P2/P3。
- 最终 EXE 的完整 selfcheck 与 phase-two-selfcheck、77 项 Token HUD 检查通过。较早修复版还运行了 mock-test、ui-batch-selfcheck、87 项旧 HUD 生命周期，GDI 30 次窗口循环为 18→18。
- 固件 152 个文件 SHA-256 与基线一致；本次 Git 固件差异为空。

## 最终 EXE 联合运行

在 **取消置顶** 模式运行最终 EXE **90.016 秒**：实际本机日志/额度/PC 指标、匿名更新检查、离屏原生 HUD、MockDeviceLink 同时工作。

- 3 次额度更新，预热后失效 0、自动重连 0。
- 422 个日志完成扫描，Token 视图更新 10 次、361 个图表端点；折叠/展开两次正常。
- 原生透明合成启用，`topMost=false` 保持，PC 帧 28 字节。
- 实际历史覆盖提示仍标为部分记录，不冒充完整账单。

EXE SHA-256：`8EB54BF365A023982131FAE6691026D16879F2BDD9FD33331130420B5E6DFC6D`。

ZIP SHA-256：`2DD400CE6B645D69B7A1E1AE8305542752A83F8F898D3EB987E4E56219C3CED2`。六个白名单文件打包、独立解压校验通过，不含配置、认证或用户日志。

本地证据：`artifacts/hud-activation-final/results.txt`、`artifacts/hud-activation-final-selfchecks/`、`artifacts/hud-activation-runtime-final/`、`review/hud-activation-review.md`。不将早先 EXE 的运行时长当作最终二进制验证。

尚未做真实设备联调、用户桌面上的 Win+D/锁屏/睡眠全组合或长期运行验收；恢复、最小化、焦点和样式的原生行为已在隔离桌面中验证。
