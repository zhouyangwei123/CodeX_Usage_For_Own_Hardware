/**
  ******************************************************************************
  * @file    rgb_ws2812.c
  * @brief   WS2812B-2020 串行 RGB 灯驱动
  *
  * 在 170MHz 下用内联汇编（SUBS+BNE 每轮 2 周期）逐位输出。
  * Debug -O0 下函数调用/读写开销会把 T0H/T1H 拉长到“全 1”区间，
  * 因此位时序全部走 always_inline + 汇编循环，刷新期间短暂关中断。
  ******************************************************************************
  */

#include "rgb_ws2812.h"
#include "app_cfg.h"
#include "hrtim.h"
#include "main.h"
#include <string.h>

#define MAX_LEDS        16

#if RGB_LED_COUNT > MAX_LEDS
#error "RGB_LED_COUNT exceeds MAX_LEDS"
#endif

/*
 * 默认使用旧版已经长期实机验证的 GPIO + DWT 发送路径。
 * HRTIM/DMA 实现保留在源码中，待示波器确认首位输出无毛刺后再重新启用。
 */
#define RGB_USE_HRTIM_DMA 0u

/* WS2812B-2020 要求复位低电平 >280us；170MHz 下约 376us。 */
#define WS_RESET_LOOPS 32000u
/* WS2812B-2020 容差内的保守时序，两种码周期均 >=1.25us。
   运行时按实测循环开销换算成循环数。 */
#define TARGET_T0H_NS  320u
#define TARGET_T1H_NS  700u
#define TARGET_T0L_NS  940u
#define TARGET_T1L_NS  700u

/* HRTIM Timer A: 170 MHz, DIV1, Period=212 => 213 ticks ≈ 1.25 us. */
#define RGB_DMA_PERIOD_TICKS 212u
#define RGB_DMA_BIT0_TICKS   46u
#define RGB_DMA_BIT1_TICKS   156u
#define RGB_DMA_COMPARE_COUNT ((MAX_LEDS * 3u * 8u) + 2u)
#define RGB_DMA_TIMEOUT_MS    2u

static rgb_config_t cfg = {
  RGB_MODE_WAVE, RGB_LED_COUNT, 64, 2000, 0xFF80C0, 0x408080,
  {0x2060FFu, 0x00FF40u, 0xFF2020u, 0xFF2020u, 0x00FFC0u, 0x000050u}
};
static uint8_t grb[MAX_LEDS * 3];
static uint32_t meas_t0h_ns;
static uint32_t meas_t1h_ns;
/* 运行时校准的循环计数（初值按 170MHz、每循环 2 周期估算） */
static uint32_t cal_t0h_loops = 25u;
static uint32_t cal_t1h_loops = 56u;
static uint32_t cal_t0l_loops = 78u;
static uint32_t cal_t1l_loops = 57u;
static uint8_t cal_done;
static uint8_t rgb_off_latched;
static uint8_t button_mask;
#if RGB_USE_HRTIM_DMA
static uint16_t rgb_dma_compare[RGB_DMA_COMPARE_COUNT];
#endif
static uint8_t rgb_dma_mode;
#if RGB_USE_HRTIM_DMA
static uint32_t rgb_dma_started_ms;
#endif

/* ---------- RGB 配置持久化（Flash 最后一页 2KB） ---------- */
#define CFG_FLASH_ADDR   0x0801F800u
#define CFG_FLASH_BANK2  0x08040000u
#define CFG_FLASH_PAGE   63u
#define CFG_MAGIC        0x43525444u  /* "DTRC"（v2：含 6 种状态颜色） */

typedef struct
{
  uint32_t magic;
  uint8_t mode;
  uint8_t count;
  uint8_t brightness;   /* 已减半的亮度（1..127） */
  uint8_t rsv;
  uint16_t period_ms;
  uint16_t rsv2;
  uint32_t color1;
  uint32_t color2;
  uint32_t status_color[6];
  uint8_t crc;
  uint8_t pad[3];
} rgb_cfg_store_t;

#define CFG_CRC_LEN      44u

static uint8_t cfg_crc(const uint8_t *p, uint8_t len)
{
  uint8_t c = 0;
  for (uint8_t i = 0; i < len; i++) c = (uint8_t)(c + p[i]);
  return c;
}

static void rgb_cfg_save(void)
{
  rgb_cfg_store_t s;
  memset(&s, 0, sizeof(s));
  s.magic = CFG_MAGIC;
  s.mode = cfg.mode;
  s.count = cfg.count;
  s.brightness = cfg.brightness;
  s.period_ms = cfg.period_ms;
  s.color1 = cfg.color1;
  s.color2 = cfg.color2;
  for (uint8_t i = 0; i < 6u; i++) s.status_color[i] = cfg.status_color[i];
  s.crc = cfg_crc((const uint8_t *)&s, CFG_CRC_LEN);

  uint32_t banks = FLASH_BANK_1;
  uint32_t page = CFG_FLASH_PAGE;
  uint32_t addr = CFG_FLASH_ADDR;
#if defined(FLASH_OPTR_DBANK)
  if (READ_BIT(FLASH->OPTR, FLASH_OPTR_DBANK) != 0U)
  {
    page = CFG_FLASH_PAGE - 32u;
    banks = FLASH_BANK_2;
    addr = CFG_FLASH_BANK2 + page * 0x800u;
  }
#endif

  __disable_irq();
  HAL_FLASH_Unlock();
  FLASH_EraseInitTypeDef er;
  er.TypeErase = FLASH_TYPEERASE_PAGES;
  er.Banks = banks;
  er.Page = page;
  er.NbPages = 1;
  uint32_t page_err = 0;
  HAL_FLASHEx_Erase(&er, &page_err);
  const uint64_t *src = (const uint64_t *)&s;
  for (uint8_t i = 0; i < (uint8_t)(sizeof(s) / 8u); i++)
    HAL_FLASH_Program(FLASH_TYPEPROGRAM_DOUBLEWORD, addr + i * 8u, src[i]);
  HAL_FLASH_Lock();
  __enable_irq();
}

static void rgb_cfg_load(void)
{
  uint32_t addr = CFG_FLASH_ADDR;
#if defined(FLASH_OPTR_DBANK)
  if (READ_BIT(FLASH->OPTR, FLASH_OPTR_DBANK) != 0U)
  {
    uint32_t page = CFG_FLASH_PAGE - 32u;
    addr = CFG_FLASH_BANK2 + page * 0x800u;
  }
#endif
  const rgb_cfg_store_t *s = (const rgb_cfg_store_t *)addr;
  if (s->magic != CFG_MAGIC) return;
  if (s->crc != cfg_crc((const uint8_t *)s, CFG_CRC_LEN)) return;
  if (s->mode > RGB_MODE_TEST) return;
  if (s->count == 0 || s->count > MAX_LEDS) return;
  if (s->brightness < 1 || s->brightness > 127) return;
  cfg.mode = s->mode;
  /* 物理链固定为 8 颗；旧 Flash 中的 count=3 仅作格式有效性检查。 */
  cfg.count = RGB_LED_COUNT;
  cfg.brightness = s->brightness;
  cfg.period_ms = s->period_ms;
  cfg.color1 = s->color1;
  cfg.color2 = s->color2;
  for (uint8_t i = 0; i < 6u; i++) cfg.status_color[i] = s->status_color[i];
}

__attribute__((always_inline)) static inline void ws_delay(uint32_t loops)
{
  __asm volatile (
    "1: subs %0, %0, #1\n"
    "   bne 1b\n"
    : "+r" (loops)
    :
    : "cc");
}

__attribute__((always_inline, optimize("O2"))) static inline void ws_write_bit(uint8_t bit)
{
  if (bit)
  {
    RGB_PORT->BSRR = RGB_PIN;
    ws_delay(cal_t1h_loops);
    RGB_PORT->BRR = RGB_PIN;
    ws_delay(cal_t1l_loops);
  }
  else
  {
    RGB_PORT->BSRR = RGB_PIN;
    ws_delay(cal_t0h_loops);
    RGB_PORT->BRR = RGB_PIN;
    ws_delay(cal_t0l_loops);
  }
}

__attribute__((optimize("O2"))) static void ws_show(const uint8_t *buf, uint8_t len)
{
  RGB_PORT->BRR = RGB_PIN;
  /* 复位期间允许中断；中断只会延长低电平，不会破坏复位。 */
  ws_delay(WS_RESET_LOOPS);
  __disable_irq();
  for (uint8_t i = 0; i < len; i++)
  {
    uint8_t b = buf[i];
    for (uint8_t bit = 0; bit < 8; bit++)
      ws_write_bit((b >> (7 - bit)) & 1u);
  }
  RGB_PORT->BRR = RGB_PIN;
  __enable_irq();
}

static void rgb_init_gpio(void)
{
  __HAL_RCC_GPIOA_CLK_ENABLE();
  HAL_GPIO_DeInit(RGB_PORT, RGB_PIN);
  GPIO_InitTypeDef gpio = {0};
  gpio.Pin = RGB_PIN;
  gpio.Mode = GPIO_MODE_OUTPUT_PP;
  gpio.Pull = GPIO_NOPULL;
  gpio.Speed = GPIO_SPEED_FREQ_VERY_HIGH;
  HAL_GPIO_Init(RGB_PORT, &gpio);
  HAL_GPIO_WritePin(RGB_PORT, RGB_PIN, GPIO_PIN_RESET);
}

#if RGB_USE_HRTIM_DMA
static uint8_t rgb_show_dma(const uint8_t *buf, uint8_t len)
{
  uint16_t bit_count = (uint16_t)len * 8u;
  uint16_t total = (uint16_t)(bit_count + 2u);
  if (buf == NULL || len == 0u || total > RGB_DMA_COMPARE_COUNT)
  {
    return 0u;
  }

  uint16_t out = 0u;
  for (uint16_t i = 0; i < len; i++)
  {
    uint8_t value = buf[i];
    for (uint8_t bit = 0; bit < 8u; bit++)
    {
      rgb_dma_compare[out++] = (value & (uint8_t)(1u << (7u - bit)))
      ? RGB_DMA_BIT1_TICKS : RGB_DMA_BIT0_TICKS;
    }
  }
  /*
   * Repetition DMA writes CMP1 through its preload pipeline. Two nonzero tail
   * values let the final data-bit period finish before the DMA-complete IRQ
   * disables Timer A and returns PA8 to GPIO-low.
   */
  rgb_dma_compare[bit_count] = RGB_DMA_BIT0_TICKS;
  rgb_dma_compare[bit_count + 1u] = RGB_DMA_BIT0_TICKS;

  if (hrtim_rgb_start_dma(rgb_dma_compare, total) != HAL_OK)
  {
    return 0u;
  }
  rgb_dma_started_ms = HAL_GetTick();
  return 1u;
}
#endif

static void rgb_show(const uint8_t *buf, uint8_t len)
{
#if RGB_USE_HRTIM_DMA
  if (rgb_dma_mode)
  {
    if (hrtim_rgb_dma_is_active())
    {
      return;
    }
    if (rgb_show_dma(buf, len))
    {
      return;
    }
    /* HRTIM/DMA 失败后切换到原有 GPIO 路径，后续帧继续可见。 */
    rgb_dma_mode = 0u;
    rgb_init_gpio();
  }
#endif
  ws_show(buf, len);
}

void rgb_process(uint32_t now_ms)
{
#if RGB_USE_HRTIM_DMA
  if (!rgb_dma_mode || !hrtim_rgb_dma_is_active())
  {
    return;
  }
  if ((uint32_t)(now_ms - rgb_dma_started_ms) <= RGB_DMA_TIMEOUT_MS)
  {
    return;
  }

  /* A DMA stream that survives its bounded frame time is no longer trusted. */
  hrtim_rgb_abort_dma();
  rgb_dma_mode = 0u;
  rgb_init_gpio();
#else
  (void)now_ms;
#endif
}

static uint8_t scale_channel(uint8_t v, uint8_t brightness)
{
  return (uint16_t)v * brightness / 255u;
}

static void set_led(uint8_t index, uint32_t color, uint8_t brightness)
{
  if (index >= cfg.count || index >= MAX_LEDS) return;
  uint8_t r = (color >> 16) & 0xFF;
  uint8_t g = (color >> 8) & 0xFF;
  uint8_t b = color & 0xFF;
#if RGB_ORDER_GRB
  grb[index * 3 + 0] = scale_channel(g, brightness);
  grb[index * 3 + 1] = scale_channel(r, brightness);
  grb[index * 3 + 2] = scale_channel(b, brightness);
#else
  grb[index * 3 + 0] = scale_channel(r, brightness);
  grb[index * 3 + 1] = scale_channel(g, brightness);
  grb[index * 3 + 2] = scale_channel(b, brightness);
#endif
}

static void apply_button_overrides(void)
{
  for (uint8_t i = 0; i < cfg.count; i++)
  {
    if ((button_mask & (uint8_t)(1u << i)) != 0u)
      set_led(i, cfg.color1, cfg.brightness);
  }
}

static uint32_t hsv_to_rgb(uint8_t h, uint8_t s, uint8_t v)
{
  uint8_t region = h / 43;
  uint8_t remainder = (h - (region * 43)) * 6;
  uint8_t p = (uint16_t)v * (255 - s) / 255;
  uint8_t q = (uint16_t)v * (255 - ((uint16_t)s * remainder / 255)) / 255;
  uint8_t t = (uint16_t)v * (255 - ((uint16_t)s * (255 - remainder) / 255)) / 255;
  switch (region)
  {
    case 0: return (v << 16) | (t << 8) | p;
    case 1: return (q << 16) | (v << 8) | p;
    case 2: return (p << 16) | (v << 8) | t;
    case 3: return (p << 16) | (q << 8) | v;
    case 4: return (t << 16) | (p << 8) | v;
    default: return (v << 16) | (p << 8) | q;
  }
}

void rgb_init(void)
{
  /* 使能 DWT 周期计数（SOF 主频测量与 RGB 自校准都依赖它） */
  CoreDebug->DEMCR |= CoreDebug_DEMCR_TRCENA_Msk;
  DWT->CYCCNT = 0;
  DWT->CTRL |= DWT_CTRL_CYCCNTENA_Msk;

  /* 开机加载上次保存的 RGB 配置（粉色波浪等），无配置时用默认 */
  rgb_cfg_load();

  /* HRTIM 代码保留用于后续示波器验证；量产默认回到稳定 GPIO 路径。 */
#if RGB_USE_HRTIM_DMA
  rgb_dma_mode = (hhrtim1.hdmaTimerA != NULL) ? 1u : 0u;
  if (!rgb_dma_mode)
  {
    rgb_init_gpio();
  }
  else
  {
    hrtim_rgb_pin_to_gpio_low();
    HAL_Delay(1);
    memset(grb, 0, sizeof(grb));
    if (!rgb_show_dma(grb, RGB_LED_COUNT * 3u))
    {
      rgb_dma_mode = 0u;
      rgb_init_gpio();
    }
  }
#else
  rgb_dma_mode = 0u;
  rgb_init_gpio();
#endif
}

/* ---------- 运行时自校准 ---------- */

/* 实测指定循环数下的高电平持续时间（cycles），O2 编译，与 ws_show 同路径 */
__attribute__((optimize("O2"))) static uint32_t measure_high_cycles(uint32_t loops)
{
  DWT->CYCCNT = 0;
  RGB_PORT->BSRR = RGB_PIN;
  ws_delay(loops);
  RGB_PORT->BRR = RGB_PIN;
  while (RGB_PORT->IDR & RGB_PIN) { }
  return DWT->CYCCNT;
}

/* 实测低电平持续时间（cycles），等待引脚真正回升 */
__attribute__((optimize("O2"))) static uint32_t measure_low_cycles(uint32_t loops)
{
  DWT->CYCCNT = 0;
  RGB_PORT->BRR = RGB_PIN;
  ws_delay(loops);
  RGB_PORT->BSRR = RGB_PIN;
  while (!(RGB_PORT->IDR & RGB_PIN)) { }
  return DWT->CYCCNT;
}

static uint32_t loops_for_ns(uint32_t target_ns, uint32_t mhz,
                             uint32_t overhead, uint32_t per50)
{
  uint32_t target_cycles = target_ns * mhz / 1000u;
  if (target_cycles <= overhead) return 4u;
  uint32_t loops = (target_cycles - overhead) * 100u / per50;
  if (loops < 4u) loops = 4u;
  if (loops > 200u) loops = 200u;
  return loops;
}

/* 用 USB SOF 测得的真实主频校准所有时序循环数 */
void rgb_recalibrate(uint32_t mhz)
{
  if (rgb_dma_mode) return;
  if (mhz < 40u || mhz > 300u) return;

  /* 使能 DWT 周期计数 */
  CoreDebug->DEMCR |= CoreDebug_DEMCR_TRCENA_Msk;
  DWT->CTRL |= DWT_CTRL_CYCCNTENA_Msk;

  __disable_irq();
  /* 预热：让指令预取/缓存进入稳态，避免首个样本偏慢导致开销高估 */
  for (uint8_t i = 0; i < 4; i++)
  {
    measure_high_cycles(8);
    measure_low_cycles(8);
  }
  RGB_PORT->BRR = RGB_PIN;

  uint32_t h100 = measure_high_cycles(100);
  uint32_t h200 = measure_high_cycles(200);
  uint32_t l100 = measure_low_cycles(100);
  uint32_t l200 = measure_low_cycles(200);
  RGB_PORT->BRR = RGB_PIN;
  __enable_irq();

  uint32_t h_per100 = h200 - h100;
  uint32_t l_per100 = l200 - l100;
  if (h_per100 < 100u || h_per100 > 800u || l_per100 < 100u || l_per100 > 800u) return;
  uint32_t h_over = h100 - h_per100;
  uint32_t l_over = l100 - l_per100;

  cal_t0h_loops = loops_for_ns(TARGET_T0H_NS, mhz, h_over, h_per100);
  cal_t1h_loops = loops_for_ns(TARGET_T1H_NS, mhz, h_over, h_per100);
  cal_t0l_loops = loops_for_ns(TARGET_T0L_NS, mhz, l_over, l_per100);
  cal_t1l_loops = loops_for_ns(TARGET_T1L_NS, mhz, l_over, l_per100);

  /* 校准后实测显示值 */
  __disable_irq();
  meas_t0h_ns = measure_high_cycles(cal_t0h_loops) * 1000u / mhz;
  meas_t1h_ns = measure_high_cycles(cal_t1h_loops) * 1000u / mhz;
  RGB_PORT->BRR = RGB_PIN;
  __enable_irq();

  cal_done = 1;
}

uint8_t rgb_is_calibrated(void) { return rgb_dma_mode ? 1u : cal_done; }
uint32_t rgb_get_meas_t0h_ns(void) { return meas_t0h_ns; }
uint32_t rgb_get_meas_t1h_ns(void) { return meas_t1h_ns; }
uint32_t rgb_get_cal_t0h_loops(void) { return cal_t0h_loops; }
uint32_t rgb_get_cal_t1h_loops(void) { return cal_t1h_loops; }

void rgb_set_config(const rgb_config_t *new_cfg)
{
  if (new_cfg == NULL) return;
  /* 若新配置未携带状态色（全 0，例如仅切换模式的旧式帧），保留当前状态色 */
  uint32_t keep_status[6];
  uint8_t has_status = 0;
  for (uint8_t i = 0; i < 6u; i++)
  {
    keep_status[i] = cfg.status_color[i];
    if (new_cfg->status_color[i] != 0u) has_status = 1;
  }
  cfg = *new_cfg;
  /* 上位机协议保留 count 字段，但本硬件版本始终驱动 8 颗灯。 */
  cfg.count = RGB_LED_COUNT;
  if (!has_status)
  {
    for (uint8_t i = 0; i < 6u; i++) cfg.status_color[i] = keep_status[i];
  }
  /* 亮度整体减半（最大亮度比之前低一半，避免刺眼） */
  cfg.brightness = (uint8_t)(cfg.brightness / 2u);
  if (cfg.brightness == 0) cfg.brightness = 1;
  if (cfg.mode > RGB_MODE_TEST) cfg.mode = RGB_MODE_OFF;
  /* 自检模式仅用于现场验证，不写入 Flash；其余配置开机生效 */
  if (cfg.mode != RGB_MODE_TEST) rgb_cfg_save();
}

const rgb_config_t *rgb_get_config(void)
{
  return &cfg;
}

void rgb_set_button_mask(uint8_t mask)
{
  button_mask = mask;
}

/* 供 app 层配置 6 种状态颜色（不改变当前模式），并持久化到 Flash */
void rgb_set_status_colors_cfg(const uint32_t colors[6])
{
  if (colors == NULL) return;
  for (uint8_t i = 0; i < 6u; i++) cfg.status_color[i] = colors[i];
  if (cfg.mode != RGB_MODE_TEST) rgb_cfg_save();
}

void rgb_update(uint32_t now_ms)
{
  uint32_t period = cfg.period_ms;
  if (period == 0) period = 1000;

  for (uint8_t i = 0; i < cfg.count; i++) set_led(i, 0x000000, 0);

  switch (cfg.mode)
  {
    case RGB_MODE_OFF:
      /* 只发送一次全黑帧；随后持续保持数据脚为低，避免空闲帧毛刺闪绿。 */
      if (button_mask == 0u)
      {
        if (!rgb_off_latched)
        {
          rgb_show(grb, cfg.count * 3u);
          rgb_off_latched = 1u;
        }
        RGB_PORT->BRR = RGB_PIN;
        return;
      }
      break;
    case RGB_MODE_SOLID:
      for (uint8_t i = 0; i < cfg.count; i++) set_led(i, cfg.color1, cfg.brightness);
      break;
    case RGB_MODE_BREATH:
    {
      uint32_t t = now_ms % period;
      uint32_t half = period / 2;
      uint16_t wave = (t < half) ? (uint16_t)(255u * t / half)
                                 : (uint16_t)(255u * (period - t) / half);
      uint8_t br = (uint16_t)cfg.brightness * wave / 255u;
      for (uint8_t i = 0; i < cfg.count; i++) set_led(i, cfg.color1, br);
      break;
    }
    case RGB_MODE_RAINBOW:
      for (uint8_t i = 0; i < cfg.count; i++)
      {
        uint8_t hue = (uint8_t)((now_ms * 3u / period) + i * 256u / cfg.count);
        set_led(i, hsv_to_rgb(hue, 200, 255), cfg.brightness);
      }
      break;
    case RGB_MODE_WAVE:
      for (uint8_t i = 0; i < cfg.count; i++)
      {
        uint32_t t = (now_ms + (uint32_t)i * period / cfg.count) % period;
        uint32_t half = period / 2;
        uint16_t wave = (t < half) ? (uint16_t)(255u * t / half)
                                   : (uint16_t)(255u * (period - t) / half);
        uint8_t br = (uint16_t)cfg.brightness * wave / 255u;
        set_led(i, cfg.color1, br);
      }
      break;
    case RGB_MODE_BLINK:
      if ((now_ms / (period / 2)) & 1u)
        for (uint8_t i = 0; i < cfg.count; i++) set_led(i, cfg.color1, cfg.brightness);
      break;
    case RGB_MODE_TEST:
    {
      /* 诊断序列：8 灯重复 RGB；第二相反向轮换；第三相全灭。 */
      uint8_t phase = (uint8_t)((now_ms / 2000) % 3);
      static const uint32_t test_colors[3] = {
        0xFF0000u, 0x00FF00u, 0x0000FFu
      };
      for (uint8_t i = 0; i < cfg.count; i++)
      {
        uint32_t color = 0u;
        if (phase < 2u)
        {
          uint8_t offset = (phase == 0u) ? 0u : 2u;
          color = test_colors[(uint8_t)((i + offset) % 3u)];
        }
        set_led(i, color, 255u);
      }
      break;
    }
    case RGB_MODE_STATUS:
    default:
      break; /* 状态色由 app 层直接写入 */
  }
  rgb_off_latched = 0u;
  apply_button_overrides();
  rgb_show(grb, cfg.count * 3u);
}

/* 供 app 层在 STATUS 模式下直接设置灯色 */
void rgb_set_status_colors(const uint32_t *colors, uint8_t count, uint8_t brightness)
{
  uint8_t br[MAX_LEDS];
  for (uint8_t i = 0; i < count && i < MAX_LEDS; i++) br[i] = brightness;
  rgb_set_status_leds(colors, br, count);
}

/* 供 app 层在 STATUS 模式下逐灯设置颜色与亮度（呼吸/波动等动画） */
void rgb_set_status_leds(const uint32_t *colors, const uint8_t *brightness, uint8_t count)
{
  rgb_off_latched = 0u;
  for (uint8_t i = 0; i < count && i < MAX_LEDS; i++)
  {
    uint8_t b = brightness ? brightness[i] : cfg.brightness;
    set_led(i, colors[i], b);
  }
  apply_button_overrides();
  rgb_show(grb, cfg.count * 3u);
}
