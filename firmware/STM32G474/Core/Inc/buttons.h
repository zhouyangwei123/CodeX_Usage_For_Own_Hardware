/**
  ******************************************************************************
  * @file    buttons.h
  * @brief   按键扫描（软件消抖 + 单击/双击/长按识别）
  ******************************************************************************
  */
#ifndef __BUTTONS_H
#define __BUTTONS_H

#include <stdint.h>

#define BTN_EV_PRESS        1
#define BTN_EV_RELEASE      2
#define BTN_EV_CLICK        3
#define BTN_EV_DOUBLE       4
#define BTN_EV_LONG_PRESS   5
#define BTN_EV_LONG_RELEASE 6

typedef struct
{
  uint8_t index;   /* 0..BTN_COUNT-1 */
  uint8_t kind;    /* BTN_EV_* */
  uint8_t count;   /* 双击计数 */
} button_event_t;

void buttons_init(void);
void buttons_process(uint32_t now_ms);
uint8_t buttons_pop(button_event_t *ev);

#endif /* __BUTTONS_H */
