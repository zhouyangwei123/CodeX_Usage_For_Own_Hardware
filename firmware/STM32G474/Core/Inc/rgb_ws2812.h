/**
  ******************************************************************************
  * @file    rgb_ws2812.h
  * @brief   WS2812B-2020 串行 RGB 灯驱动（DWT 精确定时，位操作）
  ******************************************************************************
  */
#ifndef __RGB_WS2812_H
#define __RGB_WS2812_H

#include <stdint.h>

#define RGB_MODE_OFF      0
#define RGB_MODE_SOLID    1
#define RGB_MODE_BREATH   2
#define RGB_MODE_RAINBOW  3
#define RGB_MODE_STATUS   4
#define RGB_MODE_WAVE     5
#define RGB_MODE_BLINK    6
#define RGB_MODE_TEST     7

typedef struct
{
  uint8_t mode;        /* RGB_MODE_* */
  uint8_t count;       /* 协议兼容字段；本硬件运行时固定为 RGB_LED_COUNT */
  uint8_t brightness;  /* 0..255 */
  uint16_t period_ms;  /* 动画周期 */
  uint32_t color1;     /* 0xRRGGBB */
  uint32_t color2;     /* 0xRRGGBB */
  uint32_t status_color[6]; /* 状态色：空闲/运行/等待/错误/完成/离线 */
} rgb_config_t;

void rgb_init(void);
void rgb_set_config(const rgb_config_t *cfg);
const rgb_config_t *rgb_get_config(void);
void rgb_process(uint32_t now_ms);
void rgb_update(uint32_t now_ms);
void rgb_set_status_colors(const uint32_t *colors, uint8_t count, uint8_t brightness);
void rgb_set_status_leds(const uint32_t *colors, const uint8_t *brightness, uint8_t count);
void rgb_set_status_colors_cfg(const uint32_t colors[6]);
void rgb_set_button_mask(uint8_t mask);
void rgb_recalibrate(uint32_t cpu_mhz);
uint8_t rgb_is_calibrated(void);
uint32_t rgb_get_meas_t0h_ns(void);
uint32_t rgb_get_meas_t1h_ns(void);
uint32_t rgb_get_cal_t0h_loops(void);
uint32_t rgb_get_cal_t1h_loops(void);

#endif /* __RGB_WS2812_H */
