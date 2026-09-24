/**
  ******************************************************************************
  * @file    buttons.c
  * @brief   按键扫描：5ms 周期软件消抖，识别单击/双击/长按
  ******************************************************************************
  */

#include "buttons.h"
#include "app_cfg.h"
#include "main.h"

#define EV_QUEUE_SIZE 16

/* 引脚表（原理图 PCB 网表确认）：
   idx0=PC3(KEY1), idx1=PA0(KEY2), idx2=PA1(KEY3), idx3=PA2(KEY4),
   idx4=PA3(KEY5), idx5=PA4(KEY6), idx6=PA5(KEY7), idx7=PA6(KEY8),
   idx8=PA7(ENC_P 编码器按键) */
static const GPIO_TypeDef *btn_port[BTN_COUNT] = {
  GPIOC, GPIOA, GPIOA, GPIOA, GPIOA, GPIOA, GPIOA, GPIOA, GPIOA
};
static const uint16_t btn_pin[BTN_COUNT] = {
  GPIO_PIN_3,  /* PC3 */
  GPIO_PIN_0, GPIO_PIN_1, GPIO_PIN_2, GPIO_PIN_3,
  GPIO_PIN_4, GPIO_PIN_5, GPIO_PIN_6,
  GPIO_PIN_7   /* PA7 */
};

typedef struct
{
  uint8_t stable;        /* 去抖后的稳定电平（0=松开，1=按下） */
  uint8_t last_raw;
  uint8_t repeat;
} btn_state_t;

static btn_state_t state[BTN_COUNT];
static button_event_t queue[EV_QUEUE_SIZE];
static volatile uint8_t q_head, q_tail;

static void push_event(uint8_t index, uint8_t kind, uint8_t count)
{
  uint8_t next = (uint8_t)((q_head + 1) % EV_QUEUE_SIZE);
  if (next == q_tail) return; /* 满则丢弃 */
  queue[q_head].index = index;
  queue[q_head].kind = kind;
  queue[q_head].count = count;
  q_head = next;
}

void buttons_init(void)
{
  q_head = 0;
  q_tail = 0;
  /* 将按键引脚重新配置为输入上拉（原 CubeMX 生成为 EVT 模式） */
  __HAL_RCC_GPIOC_CLK_ENABLE();
  __HAL_RCC_GPIOA_CLK_ENABLE();
  GPIO_InitTypeDef gpio = {0};
  gpio.Pin = GPIO_PIN_3;
  gpio.Mode = GPIO_MODE_INPUT;
  gpio.Pull = GPIO_PULLUP;
  HAL_GPIO_Init(GPIOC, &gpio);

  gpio.Pin = GPIO_PIN_0 | GPIO_PIN_1 | GPIO_PIN_2 | GPIO_PIN_3
           | GPIO_PIN_4 | GPIO_PIN_5 | GPIO_PIN_6 | GPIO_PIN_7;
  gpio.Mode = GPIO_MODE_INPUT;
  gpio.Pull = GPIO_PULLUP;
  HAL_GPIO_Init(GPIOA, &gpio);

  for (uint8_t i = 0; i < BTN_COUNT; i++)
  {
    state[i].last_raw = 0;
    state[i].stable = 0;
    state[i].repeat = 0;
  }
}

uint8_t buttons_pop(button_event_t *ev)
{
  if (q_tail == q_head) return 0;
  *ev = queue[q_tail];
  q_tail = (uint8_t)((q_tail + 1) % EV_QUEUE_SIZE);
  return 1;
}

void buttons_process(uint32_t now_ms)
{
  static uint32_t last_tick = 0;
  if (now_ms - last_tick < APP_TICK_MS) return;
  last_tick = now_ms;

  for (uint8_t i = 0; i < BTN_COUNT; i++)
  {
    btn_state_t *st = &state[i];
    uint8_t raw = HAL_GPIO_ReadPin(btn_port[i], btn_pin[i]);
#if BTN_ACTIVE_LOW
    raw = raw ? 0 : 1; /* 1=按下 */
#else
    raw = raw ? 1 : 0;
#endif

    if (raw != st->last_raw)
    {
      st->repeat = 0;
      st->last_raw = raw;
    }
    else if (st->repeat < 2)
    {
      st->repeat++;
      if (st->repeat == 2 && raw != st->stable)
      {
        st->stable = raw;
        if (raw)
        {
          /* 按下沿：立即上报按下（用于本地小图标反馈） */
          push_event(i, BTN_EV_PRESS, 0);
        }
        else
        {
          /* 释放沿：立即产生单击（不等待双击窗口，不区分长按） */
          push_event(i, BTN_EV_RELEASE, 0);
          push_event(i, BTN_EV_CLICK, 1);
        }
      }
    }
  }
}
