# Host0.6.3 上位机更新包

[下载上位机单文件包](CodexToolsHost-0.6.3.zip)，[ZIP校验值](SHA256SUMS.txt)。

本机全部会话输出速率已接入原生上位机：各会话分别平滑、并行相加，全部结束归零。底栏主值为`123/s`，曲线仍为分钟消耗；账号模式仍为日汇总。详情及验证边界见[接入记录](../../docs/TOKEN_RATE_HOST-20261028.md)。

同一账号短暂刷新失败时保留最近成功的剩余额度；退出登录或账号变化时清空。

更新方式：退出旧上位机，以包内`CodexToolsHost.exe`覆盖原安装目录的EXE后启动，保留原`CodexToolsHost.json`。仅含单个EXE，不包含个人配置、日志或认证信息，不需要Python。本轮不更新固件，实体屏幕仍显示tok/min。

本包通过[正式Release](https://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/v0.6.3)提供，旧版“立即检查”可发现本版。[发布说明](../../docs/RELEASE-0.6.3.md)。离线与本机读取验证已完成，本轮未重复USB实机验收。
