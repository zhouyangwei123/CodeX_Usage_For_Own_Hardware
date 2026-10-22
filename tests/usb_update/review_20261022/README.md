# 三项已知问题的离线复现

源文件和结果来自2026-10-22审查。`HostReviewRepro.cs`模拟原板A未进入DFU、另一板B先出现；`WrpAreaBRepro.cs`模拟应用页被WRP1B保护；`UiLifetimeRepro.cs`用替代BridgeService检查窗口生命周期。均不访问串口或烧录器，不读取用户配置。

先解压已测试整理包，然后在仓库运行：

```powershell
./tests/usb_update/review_20261022/run.ps1 -PackageDirectory '<解压后的包目录>'
```

此入口编译当前生产源码与三个复现程序。当前版本中退出0表示**成功复现缺陷**，不代表产品通过验收。修复后应将复现转换为拒绝错误行为的回归断言，不能继续要求缺陷发生。

`*-results.txt`是最初审查的输出；新运行结果放入忽略的 `artifacts/` 独立目录。详细发现见 `docs/USB_REVIEW-20261022.md`。
