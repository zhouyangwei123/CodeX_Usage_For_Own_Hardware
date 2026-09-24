/*
 * 固件 protocol.c 的 PC 可执行测试（MinGW gcc）
 * 直接编译 Core/Src/protocol.c，仅替换底层发送钩子与 app_on_message。
 * 编译：gcc -I <Core/Inc> main.c <Core/Src>/protocol.c -o protocol_test.exe
 */
#include <stdio.h>
#include <string.h>
#include "protocol.h"

/* ---------- 桩实现 ---------- */

static uint8_t tx_capture[8192];
static uint16_t tx_len = 0;
static int tx_busy_stub = 0;

uint8_t protocol_cdc_transmit(uint8_t *buf, uint16_t len)
{
  if (tx_busy_stub) return 1;
  memcpy(tx_capture + tx_len, buf, len);
  tx_len += len;
  return 0;
}

static int msg_count = 0;
static uint8_t msg_type[128];
static uint8_t msg_payload[128][200];
static uint8_t msg_len[128];

void app_on_message(uint8_t type, const uint8_t *payload, uint8_t len)
{
  if (msg_count < 128)
  {
    msg_type[msg_count] = type;
    msg_len[msg_count] = len;
    memcpy(msg_payload[msg_count], payload, len);
    msg_count++;
  }
}

/* ---------- 独立参考 CRC8（与协议实现分开编写） ---------- */

static uint8_t ref_crc8(const uint8_t *data, size_t len)
{
  uint8_t crc = 0;
  for (size_t i = 0; i < len; i++)
  {
    crc ^= data[i];
    for (int b = 0; b < 8; b++)
      crc = (crc & 0x80u) ? (uint8_t)((crc << 1) ^ 0x07u) : (uint8_t)(crc << 1);
  }
  return crc;
}

static int failures = 0;

#define CHECK(name, cond) do { \
  printf("%-36s %s\n", name, (cond) ? "PASS" : "FAIL"); \
  if (!(cond)) failures++; \
} while (0)

static void feed(const uint8_t *data, size_t len)
{
  for (size_t i = 0; i < len; i++)
  {
    protocol_rx_byte(data[i]);
    if (i % 7 == 0) protocol_process();
  }
  protocol_process();
}

static void make_frame(uint8_t *out, uint8_t type, const uint8_t *payload, uint8_t len)
{
  out[0] = 0xA5;
  out[1] = 0x5A;
  out[2] = type;
  out[3] = len;
  memcpy(out + 4, payload, len);
  out[4 + len] = ref_crc8(out + 2, 2 + len);
}

int main(void)
{
  /* 0. 参考 CRC 已知向量 */
  CHECK("ref-crc-vector-123456789-F4", ref_crc8((const uint8_t *)"123456789", 9) == 0xF4);

  /* 1. protocol_send 组帧与 CRC */
  protocol_init();
  tx_len = 0;
  uint8_t payload[5] = {1, 2, 3, 4, 0x7F};
  CHECK("send-len-too-long", protocol_send(MSG_PING, payload, 200) == 0);
  CHECK("send-ok", protocol_send(MSG_PING, payload, 5) == 1);
  protocol_tx_poll();
  CHECK("send-frame-len", tx_len == 10);
  CHECK("send-frame-hdr", tx_capture[0] == 0xA5 && tx_capture[1] == 0x5A &&
                          tx_capture[2] == MSG_PING && tx_capture[3] == 5);
  CHECK("send-frame-crc", tx_capture[9] == ref_crc8(tx_capture + 2, 7));

  /* 2. 有效帧解析 */
  protocol_init();
  tx_len = 0;
  msg_count = 0;
  uint8_t f[9];
  uint8_t p4[4] = {1, 2, 3, 4};
  make_frame(f, MSG_PING, p4, 4);
  feed(f, sizeof(f));
  CHECK("parse-valid-ping", msg_count == 1 && msg_type[0] == MSG_PING &&
                            msg_len[0] == 4 && msg_payload[0][0] == 1);

  /* 3. CRC 错误 -> 无回调 + ACK(结果=1) */
  protocol_init();
  tx_len = 0;
  msg_count = 0;
  f[8] ^= 0x55;
  feed(f, sizeof(f));
  protocol_tx_poll();
  CHECK("parse-bad-crc-no-callback", msg_count == 0);
  CHECK("parse-bad-crc-ack", tx_len == 7 && tx_capture[2] == MSG_ACK &&
                             tx_capture[3] == 2 && tx_capture[4] == MSG_PING &&
                             tx_capture[5] == 1);

  /* 4. 干扰前缀 + 有效帧 */
  protocol_init();
  tx_len = 0;
  msg_count = 0;
  uint8_t buf4[25];
  memcpy(buf4, "PREFIX__", 8);
  uint8_t f2[9];
  uint8_t p42[4] = {9, 8, 7, 6};
  make_frame(f2, MSG_PING, p42, 4);
  memcpy(buf4 + 8, f2, 9);
  feed(buf4, 17);
  CHECK("parse-junk-prefix", msg_count == 1 && msg_type[0] == MSG_PING &&
                             msg_payload[0][0] == 9);

  /* 5. 分片投递 */
  protocol_init();
  tx_len = 0;
  msg_count = 0;
  for (int i = 0; i < 9; i++)
  {
    protocol_rx_byte(f2[i]);
    if (i == 3 || i == 7) protocol_process();
  }
  protocol_process();
  CHECK("parse-split-delivery", msg_count == 1);

  /* 6. 突发 100 帧 */
  protocol_init();
  tx_len = 0;
  msg_count = 0;
  uint8_t burst[900];
  for (int i = 0; i < 100; i++)
  {
    int o = i * 9;
    burst[o] = 0xA5;
    burst[o + 1] = 0x5A;
    burst[o + 2] = MSG_CFG_REQ;
    burst[o + 3] = 0;
    burst[o + 4] = ref_crc8(burst + o + 2, 2);
  }
  feed(burst, sizeof(burst));
  CHECK("parse-burst-100", msg_count == 100);

  /* 7. TX 队列：满时丢最旧，不覆盖在途帧 */
  protocol_init();
  tx_len = 0;
  tx_busy_stub = 1; /* 模拟 CDC 忙 */
  for (int i = 0; i < 12; i++)
  {
    uint8_t p1[1] = {(uint8_t)i};
    protocol_send(MSG_EVT_BUTTON, p1, 1);
  }
  tx_busy_stub = 0;
  int sent = 0;
  for (int k = 0; k < 64; k++)
  {
    uint16_t before = tx_len;
    protocol_tx_poll();
    if (tx_len > before)
    {
      sent++;
      protocol_tx_complete();
    }
    if (tx_len >= 8 * 6) break;
  }
  CHECK("tx-queue-sent-8", sent == 8);
  CHECK("tx-queue-oldest-dropped", tx_capture[4] == 4);
  CHECK("tx-queue-last-payload", tx_capture[(8 - 1) * 6 + 4] == 11);
  int crc_ok = 1;
  for (int i = 0; i < 8; i++)
  {
    int o = i * 6;
    if (ref_crc8(tx_capture + o + 2, 3) != tx_capture[o + 5]) crc_ok = 0;
  }
  CHECK("tx-queue-all-crc", crc_ok);

  printf("\n%s (%d failures)\n", failures == 0 ? "ALL PASS" : "FAILED", failures);
  return failures == 0 ? 0 : 1;
}
