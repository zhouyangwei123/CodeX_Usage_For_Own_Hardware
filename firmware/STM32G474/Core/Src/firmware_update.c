#include "firmware_update.h"
#include "firmware_image.h"
#include "protocol.h"
#include "stm32g4xx_hal.h"
#include <string.h>

static uint8_t requested;
static uint32_t request_time;
void firmware_update_request(const uint8_t *payload, uint8_t length)
{
  static const uint8_t token[8] = { 'N','M','T','D','F','U','0','1' };
  if (length != sizeof(token) || memcmp(payload, token, sizeof(token)) != 0)
    return;
  uint8_t ack[2] = { MSG_ENTER_DFU, 0 };
  protocol_send(MSG_ACK, ack, sizeof(ack));
  if (!requested) { request_time = HAL_GetTick(); requested = 1; }
}
void firmware_update_poll(void)
{
  if (!requested || (uint32_t)(HAL_GetTick() - request_time) < 200u) return;
  volatile uint32_t *flag = (volatile uint32_t *)NMT_REQUEST_ADDR;
  __disable_irq();
  flag[0] = NMT_REQUEST_MAGIC;
  flag[1] = ~NMT_REQUEST_MAGIC;
  __DSB();
  NVIC_SystemReset();
}
