# 0.3.0 功能与数据范围

## 平滑额度浮窗

旧浮窗在窗口边界使用 WinForms Region 整像素裁剪，内部绘图的抗锯齿无法改善外边角。新版通过 Windows `UpdateLayeredWindow` 提交预乘 Alpha 图像，轮廓允许部分透明像素；不使用会破坏透明边缘的 ClearType 字体覆盖。

提供柔光玻璃、极简清晰、经典双色，保留大小、不透明度、置顶、拖动与刷新。玻璃为半透明底色、高光和渐变设计，不是实时桌面模糊/折射，也不引入 WebView2。高对比度或原生上传失败时回退为不透明显示。

静止时没有装饰性动画；隐藏后停止动画/倒计时计时器，仅在显示数据变化或用户刷新时绘制。窗口时长来自真实额度元数据，缺失时显示主/次窗口名称，不假定一定是 5 小时与 7 天。

## 本机用量

读取指定 `CODEX_HOME`（未设置时使用用户目录 `.codex`）下的 `sessions` 与 `archived_sessions`。保留数值用量、模型、会话标识和文件游标，不保存提示词文本。首次读取每批最多 32 MiB，未完成时标明“已扫描部分”并自动继续；已知文件每 5 秒检查，目录发现每 60 秒进行。不变文件不重复读取全文，也不重复计算全部汇总。

处理累计重复事件、活动/归档副本、分叉前缀、日志截断/重写和未完成行。同 ID 的更完整副本须通过前缀验证后采用，绝不把两个副本相加；分歧副本、缺父记录、异常计数或缺失日志都有覆盖提示。统计只覆盖存在且可解释的本机记录，不宣称账单级完整性。

“今天 / 近 7 天 / 本月 / 全部会话”按本机时区筛选。缓存输入是输入的子集；推理 Token 已包含在输出中，不再重复相加。可导出当前筛选的 CSV。没有可用价目时，原生统计的费用显示未知。

## ccusage JSON 导入

兼容性参考版本锁定为 **20.0.22**，支持 Codex 专用 `daily`、`monthly` 与 `session` 报告。可选命令：

```powershell
npx --yes ccusage@20.0.22 codex daily --json --offline --no-cost | Out-File -Encoding utf8 usage.json
```

在用量清单点击“导入 JSON”，导入报告作为独立来源显示，不与原生统计相加。月/会话报告不会被伪装成日汇总。旧版或统一多来源 schema 不自动猜测；文件上限 16 MiB。

该版本 JSON 的 `inputTokens` 不包含缓存，导入器将缓存加回，保持本工具的“输入含缓存”含义。若去掉 `--no-cost` 并有完整价格，导入器可保留报告全量的 API 等价费用估算；它不随当前日期筛选变化，不代表订阅扣费。缺价格时不会显示为免费。

固定版本 CLI 已在隔离的合成样本上实际运行，并与原生读取及 JSON 导入对账；没有将所有真实历史与官方账户账单逐项对账。应用本身不下载、安装或运行 ccusage/npm。

## 更新检查

使用公开仓库的 `releases/latest`，无认证 Token。启动或重新启用后延迟 30 秒，成功后每 12 小时检查；失败退避，设置页可手动检查。保留最后一次成功结果与时间，HTTP 404 或网络失败不会显示成“已经最新”。

只接收正式版、比较数值版本、使用 ETag 缓存并限制请求时间。发现更新后托盘菜单提供入口，不自动打开浏览器。用户主动点击后打开经过验证的当前仓库 HTTPS 发布页，下载与安装由用户完成。

## 参考

- [Windows 逐像素透明窗口](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow)
- [ccusage Codex 文档](https://ccusage.com/guide/codex/)
- [ccusage 20.0.22 JSON 实现](https://github.com/ryoppippi/ccusage/blob/v20.0.22/rust/adapters/codex/src/report.rs)
- [GitHub Releases API](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)
