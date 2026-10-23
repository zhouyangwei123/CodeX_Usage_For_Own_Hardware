# Host0.6.2更新

[下载上位机更新包](CodexToolsHost-0.6.2.zip)。修复Codex分页日志导致近期用量曲线为空；启动自动从日志补回最近一小时，不把历史计入当前分钟。

同一字节的安装包亦通过[正式Release](https://github.com/zhouyangwei123/CodeX_Usage_For_Own_Hardware/releases/tag/v0.6.2)提供，0.4.2的“立即检查”可发现该正式版本；仅检查版本，不自动下载安装。更新方法和固件版本范围见[发布说明](../../docs/RELEASE-0.6.2.md)。

退出旧上位机，将包内`CodexToolsHost.exe`覆盖到原安装目录，再启动即可。保留原`CodexToolsHost.json`，不需要重新设置或更新固件；设备继续使用App0.10。默认本机用量、像素皮肤和固定波形页保持。

纵轴是Token/分钟；一小时都无用量时最小刻度为0、0.5、1。详细原因和验证范围见[修复记录](../../docs/USAGE_HISTORY-20261023.md)。包内不包含个人配置、日志或认证信息。

EXE SHA-256：`23d7f96f27ef3a69a681eed01d35043ef70445cb785e733766c217251df5803c`。ZIP SHA-256：`651fde64a5f39faca37867d661c6812521dbf41d692224694bb8e0215d9be4e0`。
