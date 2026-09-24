# 设备-上位机协议（USB CDC）

## 物理层

- 接口：STM32 USB Device Library CDC（“CodeX Tools Bridge”）
- 参数：115200 8N1，无流控

## 帧格式

```
A5 5A <type 1B> <len 1B> <payload[0..128]> <crc8 1B>
```

- `crc8`：多项式 `0x07`，初值 `0`，覆盖 `type + len + payload`（Dallas/Maxim 风格；已知向量：ASCII `"123456789"` → `0xF4`）。
- 小端字节序（多字节字段）。

## 主机 → 设备

### 0x01 STATUS（状态推送）

| 偏移 | 长度 | 字段 |
| --- | --- | --- |
| 0 | 1 | codex_state（0=离线 1=IDLE 2=RUN 3=WAIT 4=ERROR 5=DONE） |
| 1 | 1 | flags（bit0=1 表示 API 额度无上限，如 OpenRouter unlimited） |
| 2 | 1 | primary_rem（0-100；0xFF=未知） |
| 3 | 1 | secondary_rem（同上） |
| 4 | 4 | primary_reset（Unix 秒，u32 LE） |
| 8 | 4 | secondary_reset（同上） |
| 12 | 1 | api_available（0/1） |
| 13 | 4 | api_balance_cents（u32 LE） |
| 17 | 4 | api_currency（ASCII，如 "CNY\0"） |
| 21 | 1 | text_len |
| 22 | n | text（状态/线程文本，ASCII，≤47） |

OLED 状态页把 CodeX 官方额度（PRI/SEC）与 API 余额用第二条横线分隔；
API 行显示为 `API <币种><余额>`，无上限时显示 `API U/L`。

### 0x02 RGB_SET

`[0]=mode [1]=count [2]=brightness [3..4]=period_ms(u16) [5..7]=color1(RGB) [8..10]=color2(RGB)`

mode：0=off 1=solid 2=breath 3=rainbow 4=status 5=wave 6=blink

### 0x03 OLED_PAGE

`[0]=page（0=CODEX 状态/额度，1=PC MON，2=AUTO） [1]=param（保留）`

### 0x04 OLED_TEXT

`[0]=slot（0/1 两行） [1]=len [2..]=ASCII 文本（≤60）`

### 0x05 CFG_REQ

空 payload，设备回复 0x81 INFO。

### 0x06 LED0_SET

`[0]=level（0/1）`

### 0x07 PING

`[0..3]=ts(u32)`，设备原样回 0x85 PONG。

### 0x0B PC_METRICS v3

CPU、GPU、内存、温度和网络速率使用同一个 28 字节 payload 发送。所有多字节字段均为 little-endian；温度单位为 0.1 °C，未知温度编码为 `0x8000`。

| 偏移 | 长度 | 字段 |
| ---: | ---: | --- |
| 0 | 1 | schema，固定为 `3` |
| 1 | 1 | flags（见下表） |
| 2 | 1 | CPU 占用率（0–100；无效时 255） |
| 3 | 1 | 内存占用率（0–100；无效时 255） |
| 4 | 1 | GPU 占用率（0–100；无效时 255） |
| 5 | 1 | 保留，固定为 0 |
| 6 | 2 | CPU 温度 ×10（s16 LE） |
| 8 | 2 | GPU 温度 ×10（s16 LE） |
| 10 | 2 | 主板/ACPI 温度 ×10（s16 LE） |
| 12 | 4 | 已用内存 MB（u32 LE） |
| 16 | 4 | 总内存 MB（u32 LE） |
| 20 | 4 | 统一下行速率 KiB/s（u32 LE） |
| 24 | 4 | 统一上行速率 KiB/s（u32 LE） |

| flags | 含义 |
| ---: | --- |
| bit0 | CPU 占用率有效 |
| bit1 | 内存信息有效 |
| bit2 | CPU 温度有效 |
| bit3 | GPU 占用率有效 |
| bit4 | GPU 温度有效 |
| bit5 | 主板/ACPI 温度有效 |
| bit6 | 采样已过期（stale） |
| bit7 | 上下行网速有效；未置位时固件显示 `D--` / `U--` |

固件 v0.7 起使用 v3。上位机编码器与串口桥必须固定以单包发送恰好 28 字节的 v3 payload；v1/v2 仅用于旧版本兼容测试。固件解码器对 schema=3 接受 `len >= 28`，只读取前 28 字节并忽略未知尾部，以便未来协议追加字段；这不改变当前 v3 发送长度固定为 28 字节的约束。

## 设备 → 主机

### 0x81 INFO

`[0..1]=fw_major/minor [2]=btn_count [3]=enc(1) [4]=oled_ok [5]=rgb_count [6..7]=uptime_s(u16) [8..23]=model(16B)`

### 0x82 EVT_BUTTON

`[0]=index [1]=kind [2]=count`

kind：1=press 2=release 3=click 4=double 5=long_press 6=long_release

### 0x83 EVT_ENCODER

`[0]=delta（s8，按格数）`；编码器按键事件走 0x82（index=8）。

### 0x84 ACK / 0x85 PONG

ACK：`[0]=原 type [1]=结果（0 OK 1 CRC 错 2 未知消息）`；PONG：回显 PING 的 ts。

## 主机按键绑定键名

- 普通按键：`btn0_click` / `btn0_double` / `btn0_long` … `btn5_*`
- 编码器按键（PA7/索引 8）：`enc_click` / `enc_double` / `enc_long`

## 动作白名单

见 README 第 6 节。
