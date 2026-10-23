#include "app.h"
#include "app_cfg.h"
#include <stdio.h>
#include <stdint.h>
static uint8_t host_linked = 1, visible_page, oled_dirty;
static struct { uint8_t codex_state; } host;
static uint32_t last_oled_ms;
static uint8_t noise_manual;
static uint8_t oled_noise_manual_active(void) { return noise_manual; }
#include "scheduler.inc"
int main(void)
{
  unsigned failures = 0;
  const uint8_t pages[] = {OLED_PAGE_STATUS, OLED_PAGE_LOCAL};
  for (unsigned p = 0; p < 2; p++) for (unsigned state = 0; state <= CODEX_STATE_COMPLETE; state++)
  {
    visible_page = pages[p]; host.codex_state = state; last_oled_ms = 0;
    unsigned renders = 0;
    for (uint32_t now = 5; now <= 10000; now += 5)
    {
      oled_dirty = 1; /* Incoming STATUS/PC/usage must not bypass the limit. */
      if (oled_refresh_due(now)) { renders++; last_oled_ms = now; oled_dirty = 0; }
    }
    if (renders != 10) { failures++; printf("FAIL page=%u state=%u renders=%u expected=10\n", pages[p], state, renders); }
  }
  visible_page = OLED_PAGE_LOCAL; last_oled_ms = UINT32_MAX - 500; oled_dirty = 1;
  if (oled_refresh_due(498) || !oled_refresh_due(499)) failures++;
  visible_page = OLED_PAGE_MONITOR; last_oled_ms = 0; oled_dirty = 0;
  if (oled_refresh_due(39) || !oled_refresh_due(40)) failures++;
  oled_dirty = 1; if (!oled_refresh_due(5)) failures++;
  visible_page = OLED_PAGE_LOCAL; noise_manual = 1;
  if (!oled_refresh_due(5)) failures++;
  printf("OLED scheduler: %s (12 page/state floods, wraparound, PC/diagnostic timing)\n", failures ? "FAIL" : "PASS");
  return failures ? 1 : 0;
}
