/**
  ******************************************************************************
  * @file    protocol.h
  * @brief   设备-上位机二进制协议（CDC 串口，115200 8N1）
  *
  * 帧格式：A5 5A <type> <len> <payload[0..128]> <crc8>
  * CRC8: 多项式 0x07，初值 0，覆盖 type+len+payload
  ******************************************************************************
  */
#ifndef __PROTOCOL_H
#define __PROTOCOL_H

#include <stdint.h>

#define PROTO_HDR1       0xA5
#define PROTO_HDR2       0x5A
#define PROTO_MAX_PAYLOAD 128

/* 主机 -> 设备 */
#define MSG_STATUS       0x01
#define MSG_RGB_SET      0x02
#define MSG_OLED_PAGE    0x03
#define MSG_OLED_TEXT    0x04
#define MSG_CFG_REQ      0x05
#define MSG_LED0_SET     0x06
#define MSG_PING         0x07
#define MSG_OLED_CFG     0x08   /* [0]=地址(0=自动/0x3C/0x3D) [1]=驱动(0=自动/1=SSD1306/2=SH1106) */
#define MSG_BTN_INJECT   0x09   /* [0]=按键索引 [1]=事件类型（闭环测试用） */
#define MSG_RGB_STATUS   0x0A   /* 6 种状态颜色 x 3 字节 RGB（空闲/运行/等待/错误/完成/离线） */
#define MSG_PC_METRICS   0x0B   /* v3: schema/flags/CPU%/内存%/温度x10/内存MB/上下行KiB/s，共28字节 */

/* 设备 -> 主机 */
#define MSG_INFO         0x81
#define MSG_EVT_BUTTON   0x82
#define MSG_EVT_ENCODER  0x83
#define MSG_ACK          0x84
#define MSG_PONG         0x85

void protocol_init(void);
void protocol_rx_byte(uint8_t b);
void protocol_process(void);
uint8_t protocol_send(uint8_t type, const uint8_t *payload, uint8_t len);
void protocol_tx_poll(void);
void protocol_tx_complete(void);

/* 底层发送钩子：由 USB CDC 层实现（usbd_cdc_if.c），返回 0=已发送，非 0=忙 */
uint8_t protocol_cdc_transmit(uint8_t *buf, uint16_t len);

#endif /* __PROTOCOL_H */
