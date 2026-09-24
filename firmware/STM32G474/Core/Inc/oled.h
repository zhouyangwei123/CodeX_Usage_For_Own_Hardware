/**
  ******************************************************************************
  * @file    oled.h
  * @brief   SSD1306/SH1106 I2C OLED 128x64 驱动
  ******************************************************************************
  */
#ifndef __OLED_H
#define __OLED_H

#include <stdint.h>

typedef enum
{
  OLED_NOISE_UI_FULL_FRAME = 0,
  OLED_NOISE_UI_CORNER_FRAME,
  OLED_NOISE_UI_NO_OUTER,
  OLED_NOISE_UI_SHORT_RULE,
  OLED_NOISE_UI_NO_RULES,
  OLED_NOISE_UI_TEXT_ONLY
} oled_noise_ui_style_t;

typedef struct
{
  const char *name;
  const char *mode_name;
  uint8_t index;
  uint8_t count;
  uint8_t d5;
  uint8_t d9;
  uint8_t contrast;
  uint8_t mode;
  uint8_t ui_style;
  uint16_t update_ms;
} oled_noise_manual_profile_t;

void oled_init(void);
void oled_set_config(uint8_t addr, uint8_t driver); /* 0=自动探测 */
uint8_t oled_get_status(void);  /* bit0=ok, bits1-2: 1=0x3C 2=0x3D, bit3: 1=SH1106 */
void oled_clear(uint8_t color);
void oled_draw_pixel(uint8_t x, uint8_t y, uint8_t color);
void oled_fill_rect(uint8_t x, uint8_t y, uint8_t w, uint8_t h, uint8_t color);
void oled_draw_hline(uint8_t x, uint8_t y, uint8_t w, uint8_t color);
void oled_draw_vline(uint8_t x, uint8_t y, uint8_t h, uint8_t color);
void oled_draw_rect(uint8_t x, uint8_t y, uint8_t w, uint8_t h, uint8_t color);
void oled_draw_circle(uint8_t x0, uint8_t y0, uint8_t r, uint8_t color);
void oled_draw_line(int16_t x0, int16_t y0, int16_t x1, int16_t y1, uint8_t color);
void oled_draw_bitmap(uint8_t x, uint8_t y, uint8_t w, uint8_t h,
                      const uint8_t *data);
void oled_draw_char(uint8_t x, uint8_t y, char ch, uint8_t color);
void oled_draw_string(uint8_t x, uint8_t y, const char *str, uint8_t color);
void oled_draw_progress(uint8_t x, uint8_t y, uint8_t w, uint8_t h,
                        uint8_t percent, uint8_t color);
void oled_flush(void);
void oled_process(void);
void oled_noise_diag_start(uint32_t now_ms);
void oled_noise_diag_process(uint32_t now_ms);
uint8_t oled_noise_diag_flush_allowed(void);
uint8_t oled_noise_manual_active(void);
uint8_t oled_noise_manual_get_profile(oled_noise_manual_profile_t *profile);
void oled_noise_manual_next(void);
void oled_noise_manual_mark_ui_submitted(void);
uint8_t oled_is_ok(void);

#endif /* __OLED_H */
