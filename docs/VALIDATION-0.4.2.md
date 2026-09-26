# 0.4.2 浮窗窗口策略简化

2026-09-26。仅调整上位机窗口策略，配置版本仍为 11，固件和协议不变。

## 行为

- 移除 0.4.1 新增的“找回浮窗”菜单与托盘单击处理。既有显示/隐藏、双击设置、置顶、样式与曲线折叠入口保留。
- 拦截系统最小化命令。直接原生或托管最小化调用，在当前消息完成后无激活归位，不停留在最小化状态。
- 非置顶时复用现有一秒显示周期，检查浮窗是否低于 Explorer 桌面；仅在需要时移至桌面上方，保持普通窗口的覆盖关系，不抢焦点、不切换 TopMost、不修改桌面窗口或所有者。
- 注册 DWM 的 Peek 排除属性。此属性不等于 Win+D 保证；桌面层级检查另行处理。没有添加计时器、全局钩子、Explorer 注入或新配置项。

实现依据：[SC_MINIMIZE](https://learn.microsoft.com/en-us/windows/win32/menurc/wm-syscommand)、[ShowWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindow)、[SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)、[DWM 属性](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)。桌面识别使用 Explorer 的 Progman 或同进程、承载 SHELLDLL_DefView 的 WorkerW；这部分 shell 窗口结构需要随 Windows 变化维护。

## 验证

- 改写原生窗口回归契约后，旧 0.4.1 的 53 项检查中 14 项失败。
- 最终 EXE 在独立、从未切换显示的测试桌面 **77/77** 通过；独立审查者重复验证相同 EXE **77/77**。
- 覆盖系统/直接原生/托管最小化、保存位置、正常尺寸、被动显示不抢焦点、取消/恢复置顶、桌面覆盖后的层级修正、普通应用仍覆盖、TopMost 分界、主动隐藏不被维护逻辑撤销、删除额外托盘功能。
- 审查发现一个 P2：恢复排队期间应用设置会使用最小化坐标。测试先复现两种 HUD 都失败，再由统一的正常状态处理修复；最终测试通过，无剩余审查问题。
- Token HUD 77 项、旧 HUD 生命周期 87 项通过；30 次生命周期后 GDI 为 18→18。
- 最终 EXE 的 selfcheck、mock-test、ui-batch-selfcheck、phase-two-selfcheck 均通过。
- 固件 152 个文件 SHA-256 与基线一致，Git 固件差异为空。

最终 EXE 在非置顶模式联合运行 **90.0165 秒**：3 次额度更新、预热后失效 0、自动重连 0；423 个本机日志完成扫描、10 次活动更新、361 个曲线端点、2 次折叠切换。原生透明合成正常，TopMost=false。使用模拟设备，真实本机额度/日志/PC 数据；本机历史仍如实标记部分记录。

EXE SHA-256：`F7F8B395C6B7FE1CD8EDF70CD07B4BD7BBF49AC90FD5A8F07947FFCC11F8751C`。

ZIP SHA-256：`0E1D40B7F80EF6FECD81E5041CD998C588D1B08FCA161AB97D65661CBFC07FEF`。六个白名单文件打包并解压逐项校验，不含用户配置、认证与日志。

21:14 已替换本机原启动路径并运行 0.4.2，旧进程正常退出。配置 schema、曲线折叠、置顶等原有偏好逐项比较保留；本地回滚备份不上传。运行中的新进程只读检查见 `artifacts/hud-policy-live-after.txt`。

本地证据：`artifacts/hud-policy-final/`、`artifacts/review-hud-policy-final/`、`artifacts/hud-policy-runtime-final/`、`artifacts/hud-policy-selfchecks/`、`artifacts/hud-policy-lifecycle-final/`、`review/hud-policy-review.md`。

## 边界

只读现场检查曾观察到旧 0.4.1 可见、未最小化、非置顶，但低于承载桌面的 WorkerW；另一次采样桌面宿主为 Progman，旧浮窗高于桌面。说明需要应对动态层级，不能把“可见”标志当作实际可见的保证。

桌面修正发生在下一个一秒 UI 刷新周期，UI 线程忙时会延后；不会宣称每一帧绝对保持层级。原生层级测试使用自建桌面表面 HWND，不等同实际 Explorer 的 Win+D/Peek 操作。没有操作用户桌面的快捷键，也未做锁屏、睡眠、Explorer 重启、多虚拟桌面或真实硬件验收。
