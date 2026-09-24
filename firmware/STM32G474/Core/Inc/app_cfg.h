/**
  ******************************************************************************
  * @file    app_cfg.h
  * @brief   CodeX Tools 硬件映射与应用参数
  *
  * 说明：以下引脚映射基于 20260801_CodeX_Tools.ioc 与原理图 OCR 核对结果。
  * 上电前请对照立创 EDA 原理图逐项确认，特别是 RGB 数据脚和编码器按键。
  ******************************************************************************
  */

#ifndef __APP_CFG_H
#define __APP_CFG_H

#include "stm32g4xx_hal.h"

/* ================= OLED ================= */
/* I2C3: PC8=SCL, PC9=SDA（原理图 SCL_OLED/SDA_OLED 已确认） */
#define OLED_I2C_ADDR         0x00   /* 0=自动探测(0x3C/0x3D)，也可固定 0x3C/0x3D */
#define OLED_WIDTH            128
#define OLED_HEIGHT           64
/* 0 = 自动；1 = SSD1306/SSD1309；2 = SH1106（列偏移 2） */
#define OLED_DRIVER_TYPE      0

/* OLED audible-noise controlled diagnostic. 0 = production behavior. */
#define OLED_NOISE_DIAG_ROUND       0u
#define OLED_NOISE_DIAG_WARMUP_MS   10000u
#define OLED_NOISE_DIAG_PHASE_MS    8000u

/* ================= RGB (WS2812B-2020) ================= */
/* 硬件升级：DIN_RGB = PA8（U4 pin42），8 颗 WS2812B-2020 级联；
   PA8 原为 HRTIM1_CHA1，驱动会改回 GPIO */
#define RGB_PORT              GPIOA
#define RGB_PIN               GPIO_PIN_8
#define RGB_LED_COUNT         8u
#define RGB_ORDER_GRB         1     /* WS2812B-2020 为 GRB 顺序 */

/* ================= 按键 / 编码器 ================= */
/* 原理图 PCB 网表确认（U4 引脚）：KEY1=PC3, KEY2=PA0, KEY3=PA1, KEY4=PA2,
   KEY5=PA3, KEY6=PA4, KEY7=PA5, KEY8=PA6, 编码器按键 ENC_P=PA7。
   EC11 A/B = PC0/PC1（TIM1 编码器模式）。全部低电平有效（内部上拉）。 */
#define BTN_COUNT             9     /* 0..7 为 KEY1..KEY8，8 为编码器按键 */
#define BTN_ACTIVE_LOW        1
#define ENCODER_SW_INDEX      8
#define BTN_DEBOUNCE_MS       10
#define BTN_LONG_MS           400
#define BTN_DOUBLE_MS         160

/* TIM1 使用 TI12 四倍频；翻页按 8 个计数归一化，降低轻微旋转的灵敏度。 */
#define ENCODER_COUNTS_PER_DETENT  8
/* 机械触点/边沿抖动保护，避免一次旋转被识别为多格。 */
#define ENCODER_EMIT_GUARD_MS      8
/* 翻页冷却：无论在线/离线，0.5 秒内最多执行一次页面切换。 */
#define ENCODER_PAGE_COOLDOWN_MS   500u
/* TIM 输入数字滤波，保留外部硬件上拉配置不变。 */
#define ENCODER_INPUT_FILTER       8

/* ================= 应用 ================= */
#define APP_TICK_MS           5
#define OLED_REFRESH_MONITOR_MS 40u
#define OLED_REFRESH_STATUS_MS  500u
#define OLED_REFRESH_ACTIVITY_MS 100u
#define OLED_AUTO_INTERVAL_MS  5000u
#define HOST_LINK_TIMEOUT_MS  5000  /* 超过该时间未收到主机 STATUS 视为离线 */
#define FW_VERSION_MAJOR      0
#define FW_VERSION_MINOR      7
#define FW_MODEL_STRING       "CodeX Tools"

#endif /* __APP_CFG_H */
