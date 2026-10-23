# 三项已知问题的离线复现

源文件和结果来自2026-10-22审查。`HostReviewRepro.cs`模拟原板A未进入DFU、另一板B先出现；`WrpAreaBRepro.cs`模拟应用页被WRP1B保护；`UiLifetimeRepro.cs`用替代BridgeService检查窗口生命周期。均不访问串口或烧录器，不读取用户配置。

先解压已测试整理包，然后在仓库运行：

```powershell
./tests/usb_update/review_20261022/run.ps1 -PackageDirectory '<解压后的包目录>'
```

2026-10-23 起，此入口引用实际构建的生产 EXE，三项已转换为安全回归：错误设备不发生任何内存操作；WRP1B冲突在首次写入前拒绝；升级进行时真实托盘退出路径和 ExitThread 在清理窗口前被拦截。退出0表示三项回归通过。先运行 `./host/build.ps1`。

`*-results.txt`是最初审查的输出；新运行结果放入忽略的 `artifacts/` 独立目录。详细发现见 `docs/USB_REVIEW-20261022.md`。
