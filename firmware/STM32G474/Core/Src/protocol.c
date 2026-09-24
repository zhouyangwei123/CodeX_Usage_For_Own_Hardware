/**
  ******************************************************************************
  * @file    protocol.c
  * @brief   帧解析/组帧，RX 环形缓冲 + TX 单槽挂起队列
  ******************************************************************************
  */

#include "protocol.h"
#include "app.h"

#define RX_RING_SIZE 1024

static uint8_t rx_ring[RX_RING_SIZE];
static volatile uint16_t rx_head, rx_tail;

/* 解析状态机 */
typedef enum { S_H1, S_H2, S_TYPE, S_LEN, S_PAYLOAD, S_CRC } parse_state_t;
static parse_state_t pstate = S_H1;
static uint8_t ptype;
static uint8_t plen;
static uint8_t pidx;
static uint8_t pcrc;
static uint8_t pbuf[PROTO_MAX_PAYLOAD];

/* TX 环形队列（8 帧），事件突发时不丢最新帧 */
#define TX_SLOTS 8
#define TX_FRAME_MAX (PROTO_MAX_PAYLOAD + 6)
static uint8_t tx_ring[TX_SLOTS][TX_FRAME_MAX];
static uint8_t tx_ring_len[TX_SLOTS];
static uint8_t tx_active[TX_FRAME_MAX]; /* 在途帧，避免环形槽被覆盖 */
static uint8_t tx_active_len;
static volatile uint8_t tx_head;   /* 下一个写入位置 */
static volatile uint8_t tx_tail;   /* 下一个发送位置 */
static volatile uint8_t tx_count;
static volatile uint8_t tx_busy;

static uint8_t crc8_update(uint8_t crc, uint8_t data)
{
  crc ^= data;
  for (uint8_t i = 0; i < 8; i++)
    crc = (crc & 0x80u) ? (uint8_t)((crc << 1) ^ 0x07u) : (uint8_t)(crc << 1);
  return crc;
}

void protocol_init(void)
{
  rx_head = 0;
  rx_tail = 0;
  pstate = S_H1;
  tx_head = 0;
  tx_tail = 0;
  tx_count = 0;
  tx_busy = 0;
  tx_active_len = 0;
}

void protocol_rx_byte(uint8_t b)
{
  uint16_t next = (uint16_t)((rx_head + 1) % RX_RING_SIZE);
  if (next == rx_tail) return; /* 溢出丢弃 */
  rx_ring[rx_head] = b;
  rx_head = next;
}

static void feed_parser(uint8_t b)
{
  switch (pstate)
  {
    case S_H1:
      if (b == PROTO_HDR1) pstate = S_H2;
      break;
    case S_H2:
      if (b == PROTO_HDR2) pstate = S_TYPE;
      else if (b != PROTO_HDR1) pstate = S_H1;
      break;
    case S_TYPE:
      ptype = b;
      pstate = S_LEN;
      break;
    case S_LEN:
      plen = b;
      pidx = 0;
      pcrc = crc8_update(0, ptype);
      pcrc = crc8_update(pcrc, plen);
      pstate = (plen == 0) ? S_CRC : S_PAYLOAD;
      break;
    case S_PAYLOAD:
      pbuf[pidx++] = b;
      pcrc = crc8_update(pcrc, b);
      if (pidx >= plen) pstate = S_CRC;
      break;
    case S_CRC:
      if (pcrc == b)
      {
        app_on_message(ptype, pbuf, plen);
      }
      else
      {
        uint8_t ack[2] = {ptype, 1}; /* CRC 错误 */
        protocol_send(MSG_ACK, ack, 2);
      }
      pstate = S_H1;
      break;
    default:
      pstate = S_H1;
      break;
  }
}

void protocol_process(void)
{
  while (rx_tail != rx_head)
  {
    uint8_t b = rx_ring[rx_tail];
    rx_tail = (uint16_t)((rx_tail + 1) % RX_RING_SIZE);
    feed_parser(b);
  }
}

uint8_t protocol_send(uint8_t type, const uint8_t *payload, uint8_t len)
{
  if (len > PROTO_MAX_PAYLOAD) return 0;
  uint8_t *buf = tx_ring[tx_head];
  buf[0] = PROTO_HDR1;
  buf[1] = PROTO_HDR2;
  buf[2] = type;
  buf[3] = len;
  uint8_t crc = crc8_update(crc8_update(0, type), len);
  for (uint8_t i = 0; i < len; i++)
  {
    buf[4 + i] = payload[i];
    crc = crc8_update(crc, payload[i]);
  }
  buf[4 + len] = crc;
  tx_ring_len[tx_head] = (uint8_t)(len + 5);
  if (tx_count == TX_SLOTS)
  {
    /* 队列满：丢弃最旧（尚未发送）帧 */
    tx_tail = (uint8_t)((tx_tail + 1) % TX_SLOTS);
  }
  else
  {
    tx_count++;
  }
  tx_head = (uint8_t)((tx_head + 1) % TX_SLOTS);
  return 1;
}

void protocol_tx_poll(void)
{
  if (tx_count == 0 || tx_busy) return;
  uint8_t len = tx_ring_len[tx_tail];
  for (uint8_t i = 0; i < len; i++) tx_active[i] = tx_ring[tx_tail][i];
  tx_active_len = len;
  tx_tail = (uint8_t)((tx_tail + 1) % TX_SLOTS);
  tx_count--;
  if (protocol_cdc_transmit(tx_active, tx_active_len) == 0)
  {
    tx_busy = 1;
  }
}

void protocol_tx_complete(void)
{
  tx_busy = 0;
}
