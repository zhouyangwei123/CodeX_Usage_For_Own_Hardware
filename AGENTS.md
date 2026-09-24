# CodeX Usage 项目约定

- 本轮固件仅原样纳管。不得修改 `firmware/`、烧录设备或改写其构建输出。
- 固件基线逐文件 SHA-256 位于 `docs/firmware-baseline.sha256`。
- 上位机保持 .NET Framework 4.8 / WinForms、单 EXE 发布。
- 用户配置、API Key、Codex 认证数据和米家登录资料不可入库或输出到日志。
- 协议兼容：STATUS 保持已有字段；PC_METRICS v3 固定发送 28 字节。
- 测试默认使用模拟设备；没有硬件时不得声称完成真实设备验收。
- 不播放声音；通知默认静音。
- 开发与构建在本仓库完成，旧工程为可回滚来源，不覆盖旧工程源码。
- 验证命令与交付边界记录在 `docs/`，生成物放入忽略的 `artifacts/`。
