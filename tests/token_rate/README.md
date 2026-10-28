# 本地日志输出 TPS 可行性原型

2026-10-28新增[可见命令行面板](live_console.py)。运行`python -X utf8 tests/token_rate/live_console.py --thread <本次对话ID>`，或使用`Start-LiveConsole.ps1 -Thread <ID>`。面板每0.5秒重绘，速度在新响应到达时更新，按Q或Ctrl+C退出。

面板参考[codex-statusbar TpsTracker](https://github.com/dongyue-19/codex-statusbar/blob/main/src/CodexStatusbar/TpsTracker.cs)的时间窗并集、按响应去重、EMA系数0.35和计时有效性检查；采用最近5份合格响应的加权速率后再平滑。计时少于250ms的响应仍计入真实Token总数，但不参与测速；累计至少1秒有效模型时间才给出速度。未完成计时、计时反向或过长等情况均在内部处理，保留上次速度并显示距上次更新的时间，后续正常响应会继续测速，不让一条记录使整轮失效。这个面板与下方原始分析探针分开，保留之前的诊断结果以便比较。

此原型用于2026-10-28的方案验证，不接管Codex调用，不读取认证资料、不联网，也不更新上位机或设备。仅用Python标准库；生产上位机仍使用.NET Framework 4.8。

```powershell
python -m unittest discover -s tests/token_rate -p test_probe.py
python tests/token_rate/probe.py --files 8 --watch-seconds 60 --output artifacts/token-rate/probe.json
```

可用`--home`指定Codex日志根目录；缺省依次取CODEX_HOME、用户目录下的.codex。最多采样30份最近修改的sessions日志，现场观察最多300秒，只跟踪启动时选中的文件。`--files`表示采样文件数，不代表账号或全部会话覆盖。输出只含指标、计数和匿名来源序号，不导出会话ID、文件路径、消息正文、工具参数或认证资料。运行期间产生的结果放入忽略的artifacts目录。

## 两个近似量

- `outputTpsApprox`：同一轮已取得的官方输出token总数，除以已完成Reasoning/AgentMessage时间窗的并集。工具窗口不加入并集；重叠的模型窗口只计一次。推理token已包含在output_tokens中，不再相加。
- `lastResponseTpsApprox`：最近一份官方响应用量，除以上一个用量边界后新完成的模型窗口并集。窗口跨过上一份用量时间时不强行配对，返回null。它比整轮平均更接近“最近输出速度”，仍不是瞬时值。

只在新的`token_usage_record`响应边界产生更新，按(thread_id, turn_id, response_id)去重；相同数值的不同响应仍分别计数。旧`event_msg/token_count`仅计入诊断，不与新版重复累加。这个原型不为只有旧载体的日志提供TPS，不能把当前试验范围外推到所有旧版日志。

未见轮次起点、模型时间为零/反向、计数不合法或冲突重复记录时不输出数值。不要用0替代null，也不要把上一个响应的数值当作当前生成进度。短时间窗可能放大时间误差；本原型不把第三方作者的流式对照误差当作本机误差，也不通过平滑或阈值隐藏异常。

条目起止是客户端记录的生命周期，不是服务端解码计时。官方输出量也可能包含工具调用参数，而Reasoning/AgentMessage窗口未必覆盖这些输出的全部生成时间；即使公式可计算，也不能据此宣称与模型实际解码速度等同。本轮没有接入桌面端的原始响应流作为真值，因此未量化误差。

## 接入建议

显示区可试用`≈123/s`，展开说明写“最近响应输出；响应结束后更新”，后台工具运行/等待时保留测量时间并淡化旧值，无法计算时显示`--/s`。没有流式增量时不能做真正每秒变化的输出速度；没有服务端预填充时间时不显示“输入TPS”。

现有曲线表示滚动一分钟的Token消耗。若只替换底栏数字，必须为曲线另保留`tok/min`单位，不能因底栏改为`/s`而让纵轴也看似变成TPS。账号日汇总也不能提供账号全设备的实时输出速度。

方案来源：[用户分享](https://chatgpt.com/s/t_6ae149f0e958819194591a12065a795c)、[codex-statusbar公式与边界](https://github.com/dongyue-19/codex-statusbar/blob/main/README.zh-CN.md)、[官方Codex事件接口](https://learn.chatgpt.com/docs/app-server#events)。这里独立实现最小只读验证，不运行或安装第三方状态栏程序。
