/**
  ******************************************************************************
  * @file    oled.c
  * @brief   SSD1306/SH1106 I2C OLED 128x64 驱动（I2C3）
  ******************************************************************************
  */

#include "oled.h"
#include "app_cfg.h"
#include "i2c.h"
#include "main.h"

#include <string.h>

static uint8_t fb[OLED_WIDTH * OLED_HEIGHT / 8];
static uint8_t oled_ok = 0;
static uint8_t oled_addr = OLED_I2C_ADDR;   /* 0=自动 */
static uint8_t oled_driver = OLED_DRIVER_TYPE; /* 0=自动 1=SSD1306 2=SH1106 */
static uint8_t oled_status = 0;
static uint8_t active_addr = OLED_I2C_ADDR;

#define OLED_DMA_CHUNK 128u

typedef enum
{
  OLED_DMA_IDLE = 0,
  OLED_DMA_SSD1306,
  OLED_DMA_SH1106
} oled_dma_mode_t;

static uint8_t oled_tx_fb[OLED_WIDTH * OLED_HEIGHT / 8];
static uint8_t oled_dma_cmd_buf[3];
static uint8_t oled_dma_runtime;
static uint8_t oled_dma_failed;
static volatile uint8_t oled_dma_active;
static volatile uint8_t oled_dma_inflight;
static volatile uint8_t oled_dma_kick;
static uint8_t oled_dma_pending;
static oled_dma_mode_t oled_dma_mode;
static uint8_t oled_dma_cmd_index;
static uint8_t oled_dma_page;
static uint16_t oled_dma_data_offset;

typedef enum
{
  OLED_NOISE_DIAG_LIVE = 0,
  OLED_NOISE_DIAG_STATIC,
  OLED_NOISE_DIAG_DISPLAY_OFF,
  OLED_NOISE_DIAG_PUMP_OFF,
  OLED_NOISE_DIAG_RESTORE,
  OLED_NOISE_DIAG_DONE
} oled_noise_diag_state_t;

static uint8_t oled_noise_diag_active;
#if OLED_NOISE_DIAG_ROUND >= 1u && OLED_NOISE_DIAG_ROUND <= 7u
static uint32_t oled_noise_diag_started_ms;
#endif
static oled_noise_diag_state_t oled_noise_diag_state = OLED_NOISE_DIAG_DONE;

#if OLED_NOISE_DIAG_ROUND >= 1u && OLED_NOISE_DIAG_ROUND <= 6u
static const uint8_t oled_noise_restore_commands[] = {
  0x8D, 0x14,
  0xD5, 0x80,
  0xD9, 0xF1,
  0x81, 0xCF,
  0xAF
};
#endif

#if OLED_NOISE_DIAG_ROUND == 2u
static const uint8_t oled_noise_round2_d5_values[] = {
  0x80, 0xA0, 0xC0, 0xE0, 0xF0
};
static uint8_t oled_noise_round2_profile_index;
#endif

#if OLED_NOISE_DIAG_ROUND == 3u
#define OLED_NOISE_ROUND3_D5 0xA0u
static const uint8_t oled_noise_round3_d9_values[] = {
  0xF1, 0xB1, 0x71, 0x31, 0x22
};
static uint8_t oled_noise_round3_profile_index;
#endif

#if OLED_NOISE_DIAG_ROUND == 4u
static const uint8_t oled_noise_round4_d5_values[] = {
  0xA0, 0xC0, 0xE0, 0xF0, 0xF0
};
static const uint8_t oled_noise_round4_d9_values[] = {
  0x22, 0x22, 0x22, 0x22, 0x11
};
static uint8_t oled_noise_round4_profile_index;
#endif

#if OLED_NOISE_DIAG_ROUND == 5u
#define OLED_NOISE_ROUND5_FIXED_D5 0xA0u
#define OLED_NOISE_ROUND5_D9 0x22u
#define OLED_NOISE_ROUND5_GENTLE_UPDATE_MS 17u
#define OLED_NOISE_ROUND5_STRONG_UPDATE_MS 11u

typedef enum
{
  OLED_NOISE_ROUND5_FIXED = 0,
  OLED_NOISE_ROUND5_REWRITE_CONTROL,
  OLED_NOISE_ROUND5_GENTLE_PRBS,
  OLED_NOISE_ROUND5_STRONG_PRBS,
  OLED_NOISE_ROUND5_FIXED_REPEAT,
  OLED_NOISE_ROUND5_PROFILE_COUNT
} oled_noise_round5_profile_t;

static const uint8_t oled_noise_round5_gentle_d5_values[] = {
  0x90, 0xA0, 0xB0
};
static const uint8_t oled_noise_round5_strong_d5_values[] = {
  0x80, 0x90, 0xA0, 0xB0, 0xC0
};
static uint8_t oled_noise_round5_profile_index;
static uint8_t oled_noise_round5_lfsr;
static uint32_t oled_noise_round5_last_update_ms;

static uint8_t oled_noise_round5_lfsr_step(uint8_t state)
{
  state = (uint8_t)(state & 0x7Fu);
  if (state == 0u)
  {
    state = 0x5Du;
  }
  uint8_t feedback = (uint8_t)(((state >> 6) ^ (state >> 5)) & 0x01u);
  return (uint8_t)(((state << 1) & 0x7Fu) | feedback);
}
#endif

#if OLED_NOISE_DIAG_ROUND == 6u
#define OLED_NOISE_ROUND6_D5 0xA0u
#define OLED_NOISE_ROUND6_D9 0x22u
static const uint8_t oled_noise_round6_contrast_values[] = {
  0xCF, 0xAF, 0x8F, 0x6F, 0xCF
};
static uint8_t oled_noise_round6_profile_index;
#endif

#if OLED_NOISE_DIAG_ROUND == 7u
#define OLED_NOISE_MANUAL_MODE_FIXED   0u
#define OLED_NOISE_MANUAL_MODE_REWRITE 1u
#define OLED_NOISE_MANUAL_MODE_GENTLE  2u
#define OLED_NOISE_MANUAL_MODE_STRONG  3u

static const oled_noise_manual_profile_t oled_noise_round7_profiles[] = {
  {.name = "FULL FRAME",   .mode_name = "FIXED", .d5 = 0xA0, .d9 = 0x22, .contrast = 0x6F, .mode = OLED_NOISE_MANUAL_MODE_FIXED, .ui_style = OLED_NOISE_UI_FULL_FRAME,   .update_ms = 0u},
  {.name = "CORNER FRAME", .mode_name = "FIXED", .d5 = 0xA0, .d9 = 0x22, .contrast = 0x6F, .mode = OLED_NOISE_MANUAL_MODE_FIXED, .ui_style = OLED_NOISE_UI_CORNER_FRAME, .update_ms = 0u},
  {.name = "NO OUTER",     .mode_name = "FIXED", .d5 = 0xA0, .d9 = 0x22, .contrast = 0x6F, .mode = OLED_NOISE_MANUAL_MODE_FIXED, .ui_style = OLED_NOISE_UI_NO_OUTER,     .update_ms = 0u},
  {.name = "SHORT RULE",   .mode_name = "FIXED", .d5 = 0xA0, .d9 = 0x22, .contrast = 0x6F, .mode = OLED_NOISE_MANUAL_MODE_FIXED, .ui_style = OLED_NOISE_UI_SHORT_RULE,   .update_ms = 0u},
  {.name = "NO RULES",     .mode_name = "FIXED", .d5 = 0xA0, .d9 = 0x22, .contrast = 0x6F, .mode = OLED_NOISE_MANUAL_MODE_FIXED, .ui_style = OLED_NOISE_UI_NO_RULES,     .update_ms = 0u},
  {.name = "TEXT ONLY",    .mode_name = "FIXED", .d5 = 0xA0, .d9 = 0x22, .contrast = 0x6F, .mode = OLED_NOISE_MANUAL_MODE_FIXED, .ui_style = OLED_NOISE_UI_TEXT_ONLY,    .update_ms = 0u}
};
static const uint8_t oled_noise_round7_gentle_d5_values[] = {
  0x90, 0xA0, 0xB0
};
static const uint8_t oled_noise_round7_strong_d5_values[] = {
  0x80, 0x90, 0xA0, 0xB0, 0xC0
};
static uint8_t oled_noise_round7_profile_index;
static uint8_t oled_noise_round7_apply_pending;
static uint8_t oled_noise_round7_ui_submitted;
static uint8_t oled_noise_round7_lfsr;
static uint32_t oled_noise_round7_last_update_ms;

static uint8_t oled_noise_round7_lfsr_step(uint8_t state)
{
  state = (uint8_t)(state & 0x7Fu);
  if (state == 0u) state = 0x5Du;
  uint8_t feedback = (uint8_t)(((state >> 6) ^ (state >> 5)) & 0x01u);
  return (uint8_t)(((state << 1) & 0x7Fu) | feedback);
}
#endif

static void oled_flush_blocking(void);
static void oled_dma_begin(void);
static void oled_dma_fallback(void);

uint8_t oled_get_status(void)
{
  return oled_status;
}

void oled_set_config(uint8_t addr, uint8_t driver)
{
  oled_addr = addr;
  oled_driver = driver;
  oled_init();
}

/* 经典 5x7 ASCII 字体，0x20..0x7F */
static const uint8_t font5x7[96][5] = {
  {0x00,0x00,0x00,0x00,0x00}, /* space */
  {0x00,0x00,0x5F,0x00,0x00}, /* ! */
  {0x00,0x07,0x00,0x07,0x00}, /* " */
  {0x14,0x7F,0x14,0x7F,0x14}, /* # */
  {0x24,0x2A,0x7F,0x2A,0x12}, /* $ */
  {0x23,0x13,0x08,0x64,0x62}, /* % */
  {0x36,0x49,0x55,0x22,0x50}, /* & */
  {0x00,0x05,0x03,0x00,0x00}, /* ' */
  {0x00,0x1C,0x22,0x41,0x00}, /* ( */
  {0x00,0x41,0x22,0x1C,0x00}, /* ) */
  {0x14,0x08,0x3E,0x08,0x14}, /* * */
  {0x08,0x08,0x3E,0x08,0x08}, /* + */
  {0x00,0x50,0x30,0x00,0x00}, /* , */
  {0x08,0x08,0x08,0x08,0x08}, /* - */
  {0x00,0x60,0x60,0x00,0x00}, /* . */
  {0x20,0x10,0x08,0x04,0x02}, /* / */
  {0x3E,0x51,0x49,0x45,0x3E}, /* 0 */
  {0x00,0x42,0x7F,0x40,0x00}, /* 1 */
  {0x42,0x61,0x51,0x49,0x46}, /* 2 */
  {0x21,0x41,0x45,0x4B,0x31}, /* 3 */
  {0x18,0x14,0x12,0x7F,0x10}, /* 4 */
  {0x27,0x45,0x45,0x45,0x39}, /* 5 */
  {0x3C,0x4A,0x49,0x49,0x30}, /* 6 */
  {0x01,0x71,0x09,0x05,0x03}, /* 7 */
  {0x36,0x49,0x49,0x49,0x36}, /* 8 */
  {0x06,0x49,0x49,0x29,0x1E}, /* 9 */
  {0x00,0x36,0x36,0x00,0x00}, /* : */
  {0x00,0x56,0x36,0x00,0x00}, /* ; */
  {0x08,0x14,0x22,0x41,0x00}, /* < */
  {0x14,0x14,0x14,0x14,0x14}, /* = */
  {0x00,0x41,0x22,0x14,0x08}, /* > */
  {0x02,0x01,0x51,0x09,0x06}, /* ? */
  {0x32,0x49,0x79,0x41,0x3E}, /* @ */
  {0x7E,0x11,0x11,0x11,0x7E}, /* A */
  {0x7F,0x49,0x49,0x49,0x36}, /* B */
  {0x3E,0x41,0x41,0x41,0x22}, /* C */
  {0x7F,0x41,0x41,0x22,0x1C}, /* D */
  {0x7F,0x49,0x49,0x49,0x41}, /* E */
  {0x7F,0x09,0x09,0x09,0x01}, /* F */
  {0x3E,0x41,0x49,0x49,0x7A}, /* G */
  {0x7F,0x08,0x08,0x08,0x7F}, /* H */
  {0x00,0x41,0x7F,0x41,0x00}, /* I */
  {0x20,0x40,0x41,0x3F,0x01}, /* J */
  {0x7F,0x08,0x14,0x22,0x41}, /* K */
  {0x7F,0x40,0x40,0x40,0x40}, /* L */
  {0x7F,0x02,0x0C,0x02,0x7F}, /* M */
  {0x7F,0x04,0x08,0x10,0x7F}, /* N */
  {0x3E,0x41,0x41,0x41,0x3E}, /* O */
  {0x7F,0x09,0x09,0x09,0x06}, /* P */
  {0x3E,0x41,0x51,0x21,0x5E}, /* Q */
  {0x7F,0x09,0x19,0x29,0x46}, /* R */
  {0x46,0x49,0x49,0x49,0x31}, /* S */
  {0x01,0x01,0x7F,0x01,0x01}, /* T */
  {0x3F,0x40,0x40,0x40,0x3F}, /* U */
  {0x1F,0x20,0x40,0x20,0x1F}, /* V */
  {0x3F,0x40,0x38,0x40,0x3F}, /* W */
  {0x63,0x14,0x08,0x14,0x63}, /* X */
  {0x07,0x08,0x70,0x08,0x07}, /* Y */
  {0x61,0x51,0x49,0x45,0x43}, /* Z */
  {0x00,0x7F,0x41,0x41,0x00}, /* [ */
  {0x02,0x04,0x08,0x10,0x20}, /* \ */
  {0x00,0x41,0x41,0x7F,0x00}, /* ] */
  {0x04,0x02,0x01,0x02,0x04}, /* ^ */
  {0x40,0x40,0x40,0x40,0x40}, /* _ */
  {0x00,0x01,0x02,0x04,0x00}, /* ` */
  {0x20,0x54,0x54,0x54,0x78}, /* a */
  {0x7F,0x48,0x44,0x44,0x38}, /* b */
  {0x38,0x44,0x44,0x44,0x20}, /* c */
  {0x38,0x44,0x44,0x48,0x7F}, /* d */
  {0x38,0x54,0x54,0x54,0x18}, /* e */
  {0x08,0x7E,0x09,0x01,0x02}, /* f */
  {0x0C,0x52,0x52,0x52,0x3E}, /* g */
  {0x7F,0x08,0x04,0x04,0x78}, /* h */
  {0x00,0x44,0x7D,0x40,0x00}, /* i */
  {0x20,0x40,0x44,0x3D,0x00}, /* j */
  {0x7F,0x10,0x28,0x44,0x00}, /* k */
  {0x00,0x41,0x7F,0x40,0x00}, /* l */
  {0x7C,0x04,0x18,0x04,0x78}, /* m */
  {0x7C,0x08,0x04,0x04,0x78}, /* n */
  {0x38,0x44,0x44,0x44,0x38}, /* o */
  {0x7C,0x14,0x14,0x14,0x08}, /* p */
  {0x08,0x14,0x14,0x18,0x7C}, /* q */
  {0x7C,0x08,0x04,0x04,0x08}, /* r */
  {0x48,0x54,0x54,0x54,0x20}, /* s */
  {0x04,0x3F,0x44,0x40,0x20}, /* t */
  {0x3C,0x40,0x40,0x20,0x7C}, /* u */
  {0x1C,0x20,0x40,0x20,0x1C}, /* v */
  {0x3C,0x40,0x30,0x40,0x3C}, /* w */
  {0x44,0x28,0x10,0x28,0x44}, /* x */
  {0x0C,0x50,0x50,0x50,0x3C}, /* y */
  {0x44,0x64,0x54,0x4C,0x44}, /* z */
  {0x00,0x08,0x36,0x41,0x00}, /* { */
  {0x00,0x00,0x7F,0x00,0x00}, /* | */
  {0x00,0x41,0x36,0x08,0x00}, /* } */
  {0x08,0x04,0x08,0x10,0x08}, /* ~ */
  {0x3E,0x41,0x41,0x41,0x3E}  /* DEL */
};

static void oled_write_cmd(uint8_t cmd)
{
  uint8_t buf[1] = {cmd};
  if (active_addr == 0) { oled_ok = 0; return; }
  if (HAL_I2C_Mem_Write(&hi2c3, active_addr << 1, 0x00, I2C_MEMADD_SIZE_8BIT,
                        buf, 1, 50) != HAL_OK)
  {
    oled_ok = 0;
  }
}

#if OLED_NOISE_DIAG_ROUND == 7u
static void oled_noise_round7_apply_profile(
    const oled_noise_manual_profile_t *profile)
{
  oled_write_cmd(0x8D);
  oled_write_cmd(0x14);
  oled_write_cmd(0xD5);
  oled_write_cmd(profile->d5);
  oled_write_cmd(0xD9);
  oled_write_cmd(profile->d9);
  oled_write_cmd(0x81);
  oled_write_cmd(profile->contrast);
  oled_write_cmd(0xAF);
}
#endif

uint8_t oled_noise_manual_active(void)
{
#if OLED_NOISE_DIAG_ROUND == 7u
  return oled_noise_diag_active;
#else
  return 0u;
#endif
}

uint8_t oled_noise_manual_get_profile(oled_noise_manual_profile_t *profile)
{
#if OLED_NOISE_DIAG_ROUND == 7u
  if (!oled_noise_diag_active || profile == 0) return 0u;
  *profile = oled_noise_round7_profiles[oled_noise_round7_profile_index];
  profile->index = (uint8_t)(oled_noise_round7_profile_index + 1u);
  profile->count = (uint8_t)(sizeof(oled_noise_round7_profiles)
      / sizeof(oled_noise_round7_profiles[0]));
  return 1u;
#else
  (void)profile;
  return 0u;
#endif
}

void oled_noise_manual_next(void)
{
#if OLED_NOISE_DIAG_ROUND == 7u
  if (!oled_noise_diag_active) return;
  oled_noise_round7_profile_index++;
  if (oled_noise_round7_profile_index >= (uint8_t)(
      sizeof(oled_noise_round7_profiles) / sizeof(oled_noise_round7_profiles[0])))
  {
    oled_noise_round7_profile_index = 0u;
  }
  oled_noise_round7_apply_pending = 1u;
  oled_noise_round7_ui_submitted = 0u;
  oled_noise_diag_state = OLED_NOISE_DIAG_LIVE;
#endif
}

void oled_noise_manual_mark_ui_submitted(void)
{
#if OLED_NOISE_DIAG_ROUND == 7u
  if (oled_noise_diag_active && oled_noise_round7_apply_pending)
  {
    oled_noise_round7_ui_submitted = 1u;
  }
#endif
}

void oled_noise_diag_start(uint32_t now_ms)
{
#if OLED_NOISE_DIAG_ROUND == 1u
  oled_noise_diag_started_ms = now_ms;
  oled_noise_diag_state = OLED_NOISE_DIAG_LIVE;
  oled_noise_diag_active = 1u;
#elif OLED_NOISE_DIAG_ROUND == 2u
  oled_noise_diag_started_ms = now_ms;
  oled_noise_diag_state = OLED_NOISE_DIAG_LIVE;
  oled_noise_round2_profile_index = 0xFFu;
  oled_noise_diag_active = 1u;
#elif OLED_NOISE_DIAG_ROUND == 3u
  oled_noise_diag_started_ms = now_ms;
  oled_noise_diag_state = OLED_NOISE_DIAG_LIVE;
  oled_noise_round3_profile_index = 0xFFu;
  oled_noise_diag_active = 1u;
#elif OLED_NOISE_DIAG_ROUND == 4u
  oled_noise_diag_started_ms = now_ms;
  oled_noise_diag_state = OLED_NOISE_DIAG_LIVE;
  oled_noise_round4_profile_index = 0xFFu;
  oled_noise_diag_active = 1u;
#elif OLED_NOISE_DIAG_ROUND == 5u
  oled_noise_diag_started_ms = now_ms;
  oled_noise_diag_state = OLED_NOISE_DIAG_LIVE;
  oled_noise_round5_profile_index = 0xFFu;
  oled_noise_round5_lfsr = 0x5Du;
  oled_noise_round5_last_update_ms = now_ms;
  oled_noise_diag_active = 1u;
#elif OLED_NOISE_DIAG_ROUND == 6u
  oled_noise_diag_started_ms = now_ms;
  oled_noise_diag_state = OLED_NOISE_DIAG_LIVE;
  oled_noise_round6_profile_index = 0xFFu;
  oled_noise_diag_active = 1u;
#elif OLED_NOISE_DIAG_ROUND == 7u
  oled_noise_diag_started_ms = now_ms;
  oled_noise_diag_state = OLED_NOISE_DIAG_LIVE;
  oled_noise_round7_profile_index = 0u;
  oled_noise_round7_apply_pending = 1u;
  oled_noise_round7_ui_submitted = 0u;
  oled_noise_round7_lfsr = 0x5Du;
  oled_noise_round7_last_update_ms = now_ms;
  oled_noise_diag_active = 1u;
#else
  (void)now_ms;
  oled_noise_diag_state = OLED_NOISE_DIAG_DONE;
  oled_noise_diag_active = 0u;
#endif
}

uint8_t oled_noise_diag_flush_allowed(void)
{
#if OLED_NOISE_DIAG_ROUND == 1u || OLED_NOISE_DIAG_ROUND == 2u \
    || OLED_NOISE_DIAG_ROUND == 3u || OLED_NOISE_DIAG_ROUND == 4u \
    || OLED_NOISE_DIAG_ROUND == 5u || OLED_NOISE_DIAG_ROUND == 6u \
    || OLED_NOISE_DIAG_ROUND == 7u
  if (!oled_noise_diag_active)
  {
    return 1u;
  }
  return (uint8_t)(oled_noise_diag_state == OLED_NOISE_DIAG_LIVE
      || oled_noise_diag_state == OLED_NOISE_DIAG_DONE);
#else
  return 1u;
#endif
}

void oled_noise_diag_process(uint32_t now_ms)
{
#if OLED_NOISE_DIAG_ROUND == 1u
  if (!oled_noise_diag_active)
  {
    return;
  }

  uint32_t elapsed = (uint32_t)(now_ms - oled_noise_diag_started_ms);
  oled_noise_diag_state_t target;
  if (elapsed < OLED_NOISE_DIAG_WARMUP_MS)
    target = OLED_NOISE_DIAG_LIVE;
  else if (elapsed < OLED_NOISE_DIAG_WARMUP_MS + OLED_NOISE_DIAG_PHASE_MS)
    target = OLED_NOISE_DIAG_STATIC;
  else if (elapsed < OLED_NOISE_DIAG_WARMUP_MS + 2u * OLED_NOISE_DIAG_PHASE_MS)
    target = OLED_NOISE_DIAG_DISPLAY_OFF;
  else if (elapsed < OLED_NOISE_DIAG_WARMUP_MS + 3u * OLED_NOISE_DIAG_PHASE_MS)
    target = OLED_NOISE_DIAG_PUMP_OFF;
  else if (elapsed < OLED_NOISE_DIAG_WARMUP_MS + 4u * OLED_NOISE_DIAG_PHASE_MS)
    target = OLED_NOISE_DIAG_RESTORE;
  else
    target = OLED_NOISE_DIAG_DONE;

  if (target == oled_noise_diag_state)
  {
    return;
  }
  if (oled_dma_active || oled_dma_inflight
      || HAL_I2C_GetState(&hi2c3) != HAL_I2C_STATE_READY)
  {
    return;
  }

  switch (target)
  {
    case OLED_NOISE_DIAG_STATIC:
      break;
    case OLED_NOISE_DIAG_DISPLAY_OFF:
      oled_write_cmd(0xAE);
      break;
    case OLED_NOISE_DIAG_PUMP_OFF:
      oled_write_cmd(0xAE);
      oled_write_cmd(0x8D);
      oled_write_cmd(0x10);
      break;
    case OLED_NOISE_DIAG_RESTORE:
      for (uint8_t i = 0u; i < sizeof(oled_noise_restore_commands); i++)
      {
        oled_write_cmd(oled_noise_restore_commands[i]);
      }
      break;
    case OLED_NOISE_DIAG_DONE:
      oled_noise_diag_active = 0u;
      break;
    case OLED_NOISE_DIAG_LIVE:
    default:
      break;
  }
  oled_noise_diag_state = target;
#elif OLED_NOISE_DIAG_ROUND == 2u
  if (!oled_noise_diag_active)
  {
    return;
  }

  uint32_t elapsed = (uint32_t)(now_ms - oled_noise_diag_started_ms);
  if (elapsed < OLED_NOISE_DIAG_WARMUP_MS)
  {
    return;
  }

  uint32_t sweep_elapsed = elapsed - OLED_NOISE_DIAG_WARMUP_MS;
  uint8_t target_index = (uint8_t)(sweep_elapsed / OLED_NOISE_DIAG_PHASE_MS);
  if (oled_dma_active || oled_dma_inflight
      || HAL_I2C_GetState(&hi2c3) != HAL_I2C_STATE_READY)
  {
    return;
  }

  if (target_index >= sizeof(oled_noise_round2_d5_values))
  {
    for (uint8_t i = 0u; i < sizeof(oled_noise_restore_commands); i++)
    {
      oled_write_cmd(oled_noise_restore_commands[i]);
    }
    oled_noise_diag_state = OLED_NOISE_DIAG_DONE;
    oled_noise_diag_active = 0u;
    return;
  }

  if (target_index == oled_noise_round2_profile_index)
  {
    return;
  }
  oled_write_cmd(0xD5);
  oled_write_cmd(oled_noise_round2_d5_values[target_index]);
  oled_noise_round2_profile_index = target_index;
  oled_noise_diag_state = OLED_NOISE_DIAG_STATIC;
#elif OLED_NOISE_DIAG_ROUND == 3u
  if (!oled_noise_diag_active)
  {
    return;
  }

  uint32_t elapsed = (uint32_t)(now_ms - oled_noise_diag_started_ms);
  if (elapsed < OLED_NOISE_DIAG_WARMUP_MS)
  {
    return;
  }

  uint32_t sweep_elapsed = elapsed - OLED_NOISE_DIAG_WARMUP_MS;
  uint8_t target_index = (uint8_t)(sweep_elapsed / OLED_NOISE_DIAG_PHASE_MS);
  if (oled_dma_active || oled_dma_inflight
      || HAL_I2C_GetState(&hi2c3) != HAL_I2C_STATE_READY)
  {
    return;
  }

  if (target_index >= sizeof(oled_noise_round3_d9_values))
  {
    for (uint8_t i = 0u; i < sizeof(oled_noise_restore_commands); i++)
    {
      oled_write_cmd(oled_noise_restore_commands[i]);
    }
    oled_noise_diag_state = OLED_NOISE_DIAG_DONE;
    oled_noise_diag_active = 0u;
    return;
  }

  if (target_index == oled_noise_round3_profile_index)
  {
    return;
  }
  oled_write_cmd(0xD5);
  oled_write_cmd(OLED_NOISE_ROUND3_D5);
  oled_write_cmd(0xD9);
  oled_write_cmd(oled_noise_round3_d9_values[target_index]);
  oled_noise_round3_profile_index = target_index;
  oled_noise_diag_state = OLED_NOISE_DIAG_STATIC;
#elif OLED_NOISE_DIAG_ROUND == 4u
  if (!oled_noise_diag_active)
  {
    return;
  }

  uint32_t elapsed = (uint32_t)(now_ms - oled_noise_diag_started_ms);
  if (elapsed < OLED_NOISE_DIAG_WARMUP_MS)
  {
    return;
  }

  uint32_t sweep_elapsed = elapsed - OLED_NOISE_DIAG_WARMUP_MS;
  uint8_t target_index = (uint8_t)(sweep_elapsed / OLED_NOISE_DIAG_PHASE_MS);
  if (oled_dma_active || oled_dma_inflight
      || HAL_I2C_GetState(&hi2c3) != HAL_I2C_STATE_READY)
  {
    return;
  }

  if (target_index >= sizeof(oled_noise_round4_d5_values))
  {
    for (uint8_t i = 0u; i < sizeof(oled_noise_restore_commands); i++)
    {
      oled_write_cmd(oled_noise_restore_commands[i]);
    }
    oled_noise_diag_state = OLED_NOISE_DIAG_DONE;
    oled_noise_diag_active = 0u;
    return;
  }

  if (target_index == oled_noise_round4_profile_index)
  {
    return;
  }
  oled_write_cmd(0xD5);
  oled_write_cmd(oled_noise_round4_d5_values[target_index]);
  oled_write_cmd(0xD9);
  oled_write_cmd(oled_noise_round4_d9_values[target_index]);
  oled_noise_round4_profile_index = target_index;
  oled_noise_diag_state = OLED_NOISE_DIAG_STATIC;
#elif OLED_NOISE_DIAG_ROUND == 5u
  if (!oled_noise_diag_active)
  {
    return;
  }

  uint32_t elapsed = (uint32_t)(now_ms - oled_noise_diag_started_ms);
  if (elapsed < OLED_NOISE_DIAG_WARMUP_MS)
  {
    return;
  }

  uint32_t sweep_elapsed = elapsed - OLED_NOISE_DIAG_WARMUP_MS;
  uint8_t target_index = (uint8_t)(sweep_elapsed / OLED_NOISE_DIAG_PHASE_MS);
  if (oled_dma_active || oled_dma_inflight
      || HAL_I2C_GetState(&hi2c3) != HAL_I2C_STATE_READY)
  {
    return;
  }

  if (target_index >= (uint8_t)OLED_NOISE_ROUND5_PROFILE_COUNT)
  {
    for (uint8_t i = 0u; i < sizeof(oled_noise_restore_commands); i++)
    {
      oled_write_cmd(oled_noise_restore_commands[i]);
    }
    oled_noise_diag_state = OLED_NOISE_DIAG_DONE;
    oled_noise_diag_active = 0u;
    return;
  }

  if (target_index != oled_noise_round5_profile_index)
  {
    oled_write_cmd(0xD5);
    oled_write_cmd(OLED_NOISE_ROUND5_FIXED_D5);
    oled_write_cmd(0xD9);
    oled_write_cmd(OLED_NOISE_ROUND5_D9);
    oled_noise_round5_profile_index = target_index;
    oled_noise_round5_lfsr = 0x5Du;
    oled_noise_round5_last_update_ms = now_ms;
    oled_noise_diag_state = OLED_NOISE_DIAG_STATIC;
    return;
  }

  uint32_t update_interval_ms = 0u;
  if (target_index == (uint8_t)OLED_NOISE_ROUND5_REWRITE_CONTROL
      || target_index == (uint8_t)OLED_NOISE_ROUND5_GENTLE_PRBS)
  {
    update_interval_ms = OLED_NOISE_ROUND5_GENTLE_UPDATE_MS;
  }
  else if (target_index == (uint8_t)OLED_NOISE_ROUND5_STRONG_PRBS)
  {
    update_interval_ms = OLED_NOISE_ROUND5_STRONG_UPDATE_MS;
  }

  if (update_interval_ms == 0u
      || (uint32_t)(now_ms - oled_noise_round5_last_update_ms) < update_interval_ms)
  {
    return;
  }
  oled_noise_round5_last_update_ms = now_ms;

  uint8_t d5_value = OLED_NOISE_ROUND5_FIXED_D5;
  if (target_index == (uint8_t)OLED_NOISE_ROUND5_GENTLE_PRBS)
  {
    oled_noise_round5_lfsr = oled_noise_round5_lfsr_step(oled_noise_round5_lfsr);
    d5_value = oled_noise_round5_gentle_d5_values[
        oled_noise_round5_lfsr % sizeof(oled_noise_round5_gentle_d5_values)];
  }
  else if (target_index == (uint8_t)OLED_NOISE_ROUND5_STRONG_PRBS)
  {
    oled_noise_round5_lfsr = oled_noise_round5_lfsr_step(oled_noise_round5_lfsr);
    d5_value = oled_noise_round5_strong_d5_values[
        oled_noise_round5_lfsr % sizeof(oled_noise_round5_strong_d5_values)];
  }
  oled_write_cmd(0xD5);
  oled_write_cmd(d5_value);
#elif OLED_NOISE_DIAG_ROUND == 6u
  if (!oled_noise_diag_active)
  {
    return;
  }

  uint32_t elapsed = (uint32_t)(now_ms - oled_noise_diag_started_ms);
  if (elapsed < OLED_NOISE_DIAG_WARMUP_MS)
  {
    return;
  }

  uint32_t sweep_elapsed = elapsed - OLED_NOISE_DIAG_WARMUP_MS;
  uint8_t target_index = (uint8_t)(sweep_elapsed / OLED_NOISE_DIAG_PHASE_MS);
  if (oled_dma_active || oled_dma_inflight
      || HAL_I2C_GetState(&hi2c3) != HAL_I2C_STATE_READY)
  {
    return;
  }

  if (target_index >= sizeof(oled_noise_round6_contrast_values))
  {
    for (uint8_t i = 0u; i < sizeof(oled_noise_restore_commands); i++)
    {
      oled_write_cmd(oled_noise_restore_commands[i]);
    }
    oled_noise_diag_state = OLED_NOISE_DIAG_DONE;
    oled_noise_diag_active = 0u;
    return;
  }

  if (target_index == oled_noise_round6_profile_index)
  {
    return;
  }
  oled_write_cmd(0xD5);
  oled_write_cmd(OLED_NOISE_ROUND6_D5);
  oled_write_cmd(0xD9);
  oled_write_cmd(OLED_NOISE_ROUND6_D9);
  oled_write_cmd(0x81);
  oled_write_cmd(oled_noise_round6_contrast_values[target_index]);
  oled_noise_round6_profile_index = target_index;
  oled_noise_diag_state = OLED_NOISE_DIAG_STATIC;
#elif OLED_NOISE_DIAG_ROUND == 7u
  if (!oled_noise_diag_active)
  {
    return;
  }

  const oled_noise_manual_profile_t *profile =
      &oled_noise_round7_profiles[oled_noise_round7_profile_index];
  if (oled_noise_round7_apply_pending)
  {
    if (!oled_noise_round7_ui_submitted)
    {
      return;
    }
    if (oled_dma_active || oled_dma_inflight
        || HAL_I2C_GetState(&hi2c3) != HAL_I2C_STATE_READY)
    {
      return;
    }
    oled_noise_round7_apply_profile(profile);
    oled_noise_round7_apply_pending = 0u;
    oled_noise_round7_lfsr = 0x5Du;
    oled_noise_round7_last_update_ms = now_ms;
    oled_noise_diag_state = OLED_NOISE_DIAG_STATIC;
    return;
  }

  if (profile->mode == OLED_NOISE_MANUAL_MODE_FIXED
      || (uint32_t)(now_ms - oled_noise_round7_last_update_ms)
          < profile->update_ms)
  {
    return;
  }
  if (oled_dma_active || oled_dma_inflight
      || HAL_I2C_GetState(&hi2c3) != HAL_I2C_STATE_READY)
  {
    return;
  }
  oled_noise_round7_last_update_ms = now_ms;

  uint8_t d5_value = profile->d5;
  if (profile->mode == OLED_NOISE_MANUAL_MODE_GENTLE)
  {
    oled_noise_round7_lfsr = oled_noise_round7_lfsr_step(oled_noise_round7_lfsr);
    d5_value = oled_noise_round7_gentle_d5_values[
        oled_noise_round7_lfsr % sizeof(oled_noise_round7_gentle_d5_values)];
  }
  else if (profile->mode == OLED_NOISE_MANUAL_MODE_STRONG)
  {
    oled_noise_round7_lfsr = oled_noise_round7_lfsr_step(oled_noise_round7_lfsr);
    d5_value = oled_noise_round7_strong_d5_values[
        oled_noise_round7_lfsr % sizeof(oled_noise_round7_strong_d5_values)];
  }
  oled_write_cmd(0xD5);
  oled_write_cmd(d5_value);
#else
  (void)now_ms;
#endif
}

static void oled_write_data(const uint8_t *data, uint16_t len)
{
  /* 每包 32 字节分块传输，避免一次 129 字节超时 */
  uint16_t off = 0;
  if (active_addr == 0) { oled_ok = 0; return; }
  while (off < len)
  {
    uint16_t chunk = len - off;
    if (chunk > 32) chunk = 32;
    if (HAL_I2C_Mem_Write(&hi2c3, active_addr << 1, 0x40, I2C_MEMADD_SIZE_8BIT,
                          (uint8_t *)data + off, chunk, 50) != HAL_OK)
    {
      oled_ok = 0;
      return;
    }
    off += chunk;
  }
}

static HAL_StatusTypeDef oled_dma_submit(uint8_t mem_addr,
                                         const uint8_t *data,
                                         uint16_t len)
{
  if (active_addr == 0 || data == NULL || len == 0u)
  {
    return HAL_ERROR;
  }

  HAL_StatusTypeDef status = HAL_I2C_Mem_Write_DMA(
      &hi2c3,
      active_addr << 1,
      mem_addr,
      I2C_MEMADD_SIZE_8BIT,
      (uint8_t *)data,
      len);
  if (status == HAL_OK)
  {
    oled_dma_inflight = 1u;
  }
  return status;
}

static HAL_StatusTypeDef oled_dma_submit_next(void)
{
  static const uint8_t ssd1306_cmds[] = {0x21, 0x00, 0x7F, 0x22, 0x00, 0x07};

  if (oled_dma_mode == OLED_DMA_SSD1306)
  {
    if (oled_dma_cmd_index == 0u)
    {
      HAL_StatusTypeDef status = oled_dma_submit(
          0x00, ssd1306_cmds, sizeof(ssd1306_cmds));
      if (status == HAL_OK) oled_dma_cmd_index = 1u;
      return status;
    }
    if (oled_dma_data_offset < sizeof(oled_tx_fb))
    {
      uint16_t chunk = (uint16_t)(sizeof(oled_tx_fb) - oled_dma_data_offset);
      if (chunk > OLED_DMA_CHUNK) chunk = OLED_DMA_CHUNK;
      HAL_StatusTypeDef status = oled_dma_submit(
          0x40, oled_tx_fb + oled_dma_data_offset, chunk);
      if (status == HAL_OK) oled_dma_data_offset += chunk;
      return status;
    }
    oled_dma_active = 0u;
    return HAL_OK;
  }

  if (oled_dma_mode == OLED_DMA_SH1106)
  {
    if (oled_dma_page >= 8u)
    {
      oled_dma_active = 0u;
      return HAL_OK;
    }
    if (oled_dma_cmd_index == 0u)
    {
      oled_dma_cmd_buf[0] = (uint8_t)(0xB0u + oled_dma_page);
      oled_dma_cmd_buf[1] = 0x02u;
      oled_dma_cmd_buf[2] = 0x10u;
      HAL_StatusTypeDef status = oled_dma_submit(
          0x00, oled_dma_cmd_buf, sizeof(oled_dma_cmd_buf));
      if (status == HAL_OK) oled_dma_cmd_index = 1u;
      return status;
    }
    if (oled_dma_data_offset < OLED_WIDTH)
    {
      uint16_t chunk = (uint16_t)(OLED_WIDTH - oled_dma_data_offset);
      if (chunk > OLED_DMA_CHUNK) chunk = OLED_DMA_CHUNK;
      HAL_StatusTypeDef status = oled_dma_submit(
          0x40,
          oled_tx_fb + (uint16_t)oled_dma_page * OLED_WIDTH + oled_dma_data_offset,
          chunk);
      if (status == HAL_OK) oled_dma_data_offset += chunk;
      return status;
    }
    oled_dma_page++;
    oled_dma_cmd_index = 0u;
    oled_dma_data_offset = 0u;
    return oled_dma_submit_next();
  }

  oled_dma_active = 0u;
  return HAL_ERROR;
}

static void oled_dma_begin(void)
{
  memcpy(oled_tx_fb, fb, sizeof(fb));
  oled_dma_mode = (oled_driver == 2u) ? OLED_DMA_SH1106 : OLED_DMA_SSD1306;
  oled_dma_cmd_index = 0u;
  oled_dma_page = 0u;
  oled_dma_data_offset = 0u;
  oled_dma_active = 1u;
  oled_dma_inflight = 0u;
  oled_dma_kick = 1u;
}

static void oled_dma_fallback(void)
{
  oled_dma_active = 0u;
  oled_dma_inflight = 0u;
  oled_dma_pending = 0u;
  oled_dma_runtime = 0u;
  oled_dma_failed = 1u;
  oled_flush_blocking();
}

void oled_process(void)
{
  if (!oled_dma_active || oled_dma_inflight || !oled_dma_kick)
  {
    return;
  }

  oled_dma_kick = 0u;
  HAL_StatusTypeDef status = oled_dma_submit_next();
  if (status == HAL_BUSY)
  {
    oled_dma_kick = 1u;
    return;
  }
  if (status != HAL_OK)
  {
    oled_dma_fallback();
    return;
  }

  if (!oled_dma_active && oled_dma_pending)
  {
    oled_dma_pending = 0u;
    oled_dma_begin();
  }
}

void HAL_I2C_MemTxCpltCallback(I2C_HandleTypeDef *hi2c)
{
  if (hi2c != &hi2c3 || !oled_dma_active)
  {
    return;
  }
  oled_dma_inflight = 0u;
  oled_dma_kick = 1u;
}

void HAL_I2C_ErrorCallback(I2C_HandleTypeDef *hi2c)
{
  if (hi2c != &hi2c3)
  {
    return;
  }
  oled_dma_active = 0u;
  oled_dma_inflight = 0u;
  oled_dma_pending = 0u;
  oled_dma_kick = 0u;
  if (oled_dma_runtime)
  {
    oled_dma_failed = 1u;
    oled_ok = 0u;
  }
}

void oled_init(void)
{
  oled_dma_runtime = 0u;
  oled_dma_failed = 0u;
  oled_dma_active = 0u;
  oled_dma_inflight = 0u;
  oled_dma_pending = 0u;
  oled_dma_kick = 0u;
  oled_ok = 1;
  oled_status = 0;

  /* 地址自动探测 */
  uint8_t addr = oled_addr;
  if (addr == 0)
  {
    if (HAL_I2C_IsDeviceReady(&hi2c3, 0x3C << 1, 5, 50) == HAL_OK) addr = 0x3C;
    else if (HAL_I2C_IsDeviceReady(&hi2c3, 0x3D << 1, 5, 50) == HAL_OK) addr = 0x3D;
    else { oled_ok = 0; return; }
  }
  else
  {
    if (HAL_I2C_IsDeviceReady(&hi2c3, addr << 1, 5, 50) != HAL_OK) { oled_ok = 0; return; }
  }
  active_addr = addr;

  /* 驱动类型：自动时先按 SSD1306 尝试 */
  uint8_t driver = oled_driver;
  if (driver == 0) driver = 1;

  static const uint8_t init_seq[] = {
    0xAE,             /* display off */
    0xD5, 0xA0,       /* clock div: selected low-noise fixed profile */
    0xA8, 0x3F,       /* multiplex 63 */
    0xD3, 0x00,       /* display offset */
    0x40,             /* start line 0 */
    0x8D, 0x14,       /* charge pump on */
    0xA1,             /* segment remap */
    0xC8,             /* com scan dec */
    0xDA, 0x12,       /* com pins */
    0x81, 0x6F,       /* contrast: accepted brightness/noise balance */
    0xD9, 0x22,       /* precharge: selected low-noise fixed profile */
    0xDB, 0x40,       /* vcom detect */
    0xA4,             /* resume to RAM */
    0xA6,             /* normal display */
    0xAF              /* display on */
  };
  if (driver == 2)
  {
    /* SH1106：使用页寻址 */
    oled_write_cmd(0x20); oled_write_cmd(0x02);
  }
  else
  {
    oled_write_cmd(0x20); oled_write_cmd(0x00); /* SSD1306 水平寻址 */
  }
  for (uint16_t i = 0; i < sizeof(init_seq); i++) oled_write_cmd(init_seq[i]);
  HAL_Delay(20);
  oled_clear(0);
  oled_flush();
  oled_status = (uint8_t)(0x01 | (addr == 0x3D ? 0x02 : 0x04) | (driver == 2 ? 0x08 : 0x00));
  if (oled_ok) oled_dma_runtime = 1u;
}

uint8_t oled_is_ok(void)
{
  return oled_ok;
}

void oled_clear(uint8_t color)
{
  memset(fb, color ? 0xFF : 0x00, sizeof(fb));
}

void oled_draw_pixel(uint8_t x, uint8_t y, uint8_t color)
{
  if (x >= OLED_WIDTH || y >= OLED_HEIGHT) return;
  uint16_t idx = (y / 8) * OLED_WIDTH + x;
  uint8_t bit = 1u << (y & 7);
  if (color) fb[idx] |= bit;
  else fb[idx] &= (uint8_t)~bit;
}

void oled_fill_rect(uint8_t x, uint8_t y, uint8_t w, uint8_t h, uint8_t color)
{
  for (uint8_t yy = 0; yy < h; yy++)
    for (uint8_t xx = 0; xx < w; xx++)
      oled_draw_pixel(x + xx, y + yy, color);
}

void oled_draw_hline(uint8_t x, uint8_t y, uint8_t w, uint8_t color)
{
  oled_fill_rect(x, y, w, 1, color);
}

void oled_draw_vline(uint8_t x, uint8_t y, uint8_t h, uint8_t color)
{
  oled_fill_rect(x, y, 1, h, color);
}

void oled_draw_rect(uint8_t x, uint8_t y, uint8_t w, uint8_t h, uint8_t color)
{
  oled_draw_hline(x, y, w, color);
  oled_draw_hline(x, y + h - 1, w, color);
  oled_draw_vline(x, y, h, color);
  oled_draw_vline(x + w - 1, y, h, color);
}

void oled_draw_circle(uint8_t x0, uint8_t y0, uint8_t r, uint8_t color)
{
  int16_t x = r, y = 0, err = 1 - (int16_t)r;
  while (x >= y)
  {
    oled_draw_pixel(x0 + x, y0 + y, color);
    oled_draw_pixel(x0 + y, y0 + x, color);
    oled_draw_pixel(x0 - y, y0 + x, color);
    oled_draw_pixel(x0 - x, y0 + y, color);
    oled_draw_pixel(x0 - x, y0 - y, color);
    oled_draw_pixel(x0 - y, y0 - x, color);
    oled_draw_pixel(x0 + y, y0 - x, color);
    oled_draw_pixel(x0 + x, y0 - y, color);
    y++;
    if (err < 0) err += 2 * y + 1;
    else { x--; err += 2 * (y - x) + 1; }
  }
}

/* Bresenham 直线（像素越界由 oled_draw_pixel 自行裁剪） */
void oled_draw_line(int16_t x0, int16_t y0, int16_t x1, int16_t y1, uint8_t color)
{
  int16_t dx = x1 > x0 ? x1 - x0 : x0 - x1;
  int16_t dy = y1 > y0 ? y1 - y0 : y0 - y1;
  int16_t sx = x0 < x1 ? 1 : -1;
  int16_t sy = y0 < y1 ? 1 : -1;
  int16_t err = dx - dy;
  for (;;)
  {
    oled_draw_pixel((uint8_t)x0, (uint8_t)y0, color);
    if (x0 == x1 && y0 == y1) break;
    int16_t e2 = 2 * err;
    if (e2 > -dy) { err -= dy; x0 += sx; }
    if (e2 < dx) { err += dx; y0 += sy; }
  }
}

/* 单色位图：data 为行主序，每行按 w 位（高位在前）排列 */
void oled_draw_bitmap(uint8_t x, uint8_t y, uint8_t w, uint8_t h,
                      const uint8_t *data)
{
  uint8_t stride = (w + 7u) / 8u;
  for (uint8_t yy = 0; yy < h; yy++)
  {
    uint8_t py = (uint8_t)(y + yy);
    if (py >= OLED_HEIGHT) break;
    uint16_t idx = ((uint16_t)(py / 8) * OLED_WIDTH) + x;
    uint8_t bit = (uint8_t)(1u << (py & 7));
    for (uint8_t xx = 0; xx < w; xx++)
    {
      if (x + xx >= OLED_WIDTH) break;
      if (data[(uint16_t)yy * stride + (xx >> 3)] & (0x80u >> (xx & 7)))
        fb[idx + xx] |= bit;
    }
  }
}

void oled_draw_char(uint8_t x, uint8_t y, char ch, uint8_t color)
{
  if (ch < 0x20 || ch > 0x7F) ch = ' ';
  const uint8_t *glyph = font5x7[ch - 0x20];
  for (uint8_t col = 0; col < 5; col++)
    for (uint8_t row = 0; row < 7; row++)
      if (glyph[col] & (1u << row))
        oled_draw_pixel(x + col, y + row, color);
}

void oled_draw_string(uint8_t x, uint8_t y, const char *str, uint8_t color)
{
  while (*str)
  {
    oled_draw_char(x, y, *str, color);
    x += 6;
    if (x + 5 > OLED_WIDTH) break;
    str++;
  }
}

void oled_draw_progress(uint8_t x, uint8_t y, uint8_t w, uint8_t h,
                        uint8_t percent, uint8_t color)
{
  oled_draw_rect(x, y, w, h, color);
  if (percent > 100) percent = 100;
  uint8_t fill = (uint16_t)(w - 2) * percent / 100;
  if (fill > 0) oled_fill_rect(x + 1, y + 1, fill, h - 2, color);
}

static void oled_flush_blocking(void)
{
  if (!oled_ok) return;
  uint8_t driver = oled_driver;
  if (driver == 0) driver = 1;
  if (driver == 2)
  {
    for (uint8_t page = 0; page < 8; page++)
    {
      oled_write_cmd(0xB0 + page);
      oled_write_cmd(0x02); /* SH1106 列偏移 2 */
      oled_write_cmd(0x10);
      oled_write_data(fb + page * OLED_WIDTH, OLED_WIDTH);
    }
  }
  else
  {
    oled_write_cmd(0x21); /* set column 0..127 */
    oled_write_cmd(0x00);
    oled_write_cmd(0x7F);
    oled_write_cmd(0x22); /* set page 0..7 */
    oled_write_cmd(0x00);
    oled_write_cmd(0x07);
    oled_write_data(fb, sizeof(fb));
  }
}

void oled_flush(void)
{
  if (!oled_noise_diag_flush_allowed()) return;
  if (!oled_ok) return;
  if (!oled_dma_runtime || oled_dma_failed)
  {
    oled_flush_blocking();
    return;
  }
  if (oled_dma_active)
  {
    oled_dma_pending = 1u;
    return;
  }
  oled_dma_begin();
  oled_process();
}
