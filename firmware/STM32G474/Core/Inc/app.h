/**
  ******************************************************************************
  * @file    app.h
  * @brief   CodeX Tools 应用层接口
  ******************************************************************************
  */
#ifndef __APP_H
#define __APP_H

#include <stdint.h>

/* Codex 状态枚举（与上位机保持一致） */
#define CODEX_STATE_OFFLINE  0
#define CODEX_STATE_IDLE     1
#define CODEX_STATE_RUNNING  2
#define CODEX_STATE_WAITING  3
#define CODEX_STATE_ERROR    4
#define CODEX_STATE_COMPLETE 5

/* OLED 页面 */
#define OLED_PAGE_STATUS   0
#define OLED_PAGE_MONITOR  1
#define OLED_PAGE_AUTO     2
#define OLED_PAGE_LOCAL    4 /* 3 remains the legacy AUTO alias. */
#define OLED_PAGE_COUNT    5

static inline uint8_t app_next_oled_page(uint8_t page, int16_t delta)
{
  static const uint8_t pages[] = {OLED_PAGE_STATUS, OLED_PAGE_MONITOR, OLED_PAGE_LOCAL, OLED_PAGE_AUTO};
  int index = page == OLED_PAGE_MONITOR ? 1 : page == OLED_PAGE_LOCAL ? 2 : page == OLED_PAGE_AUTO ? 3 : 0;
  int next = (index + delta) % 4;
  if (next < 0) next += 4;
  return pages[next];
}

void app_init(void);
void app_tick(void);
void app_on_message(uint8_t type, const uint8_t *payload, uint8_t len);

#endif /* __APP_H */
