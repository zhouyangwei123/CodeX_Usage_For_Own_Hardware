/**
  ******************************************************************************
  * @file    app.c
  * @brief   应用主逻辑：状态管理、OLED 页面、RGB 状态灯、事件上报
  ******************************************************************************
  */

#include "app.h"
#include "app_cfg.h"
#include "main.h"
#include "oled.h"
#include "rgb_ws2812.h"
#include "buttons.h"
#include "encoder.h"
#include "pc_metrics_parser.h"
#include "protocol.h"
#include "ui_header.h"
#include "usb_device.h"
#include "usbd_core.h"

#include <stdio.h>
#include <string.h>
#include <math.h>

extern USBD_HandleTypeDef hUsbDeviceFS;

#define PC_FLAG_CPU_VALID        PC_METRICS_FLAG_CPU_VALID
#define PC_FLAG_MEMORY_VALID     PC_METRICS_FLAG_MEMORY_VALID
#define PC_FLAG_CPU_TEMP_VALID   PC_METRICS_FLAG_CPU_TEMP_VALID
#define PC_FLAG_GPU_LOAD_VALID   PC_METRICS_FLAG_GPU_LOAD_VALID
#define PC_FLAG_GPU_TEMP_VALID   PC_METRICS_FLAG_GPU_TEMP_VALID
#define PC_FLAG_MB_TEMP_VALID    PC_METRICS_FLAG_MB_TEMP_VALID
#define PC_FLAG_STALE            PC_METRICS_FLAG_STALE
#define PC_FLAG_NETWORK_VALID    PC_METRICS_FLAG_NETWORK_VALID
#define PC_TEMP_UNKNOWN          PC_METRICS_TEMP_UNKNOWN

typedef struct
{
  uint8_t codex_state;
  uint8_t flags;
  uint8_t primary_rem;
  uint8_t secondary_rem;
  uint32_t primary_reset;
  uint32_t secondary_reset;
  uint8_t ds_available;
  uint32_t ds_balance_cents;
  char ds_currency[4];
  char text[48];
  uint32_t received_ms;
  uint8_t pc_schema;
  uint8_t pc_flags;
  uint8_t pc_cpu_percent;
  uint8_t pc_memory_percent;
  uint8_t pc_gpu_percent;
  int16_t pc_cpu_temperature_x10;
  int16_t pc_gpu_temperature_x10;
  int16_t pc_motherboard_temperature_x10;
  uint32_t pc_memory_used_mb;
  uint32_t pc_memory_total_mb;
  uint32_t pc_download_kib_s;
  uint32_t pc_upload_kib_s;
  uint32_t pc_received_ms;
} host_status_t;

static host_status_t host;
static uint8_t host_linked = 0;
static uint8_t usb_was_configured = 0;
static uint8_t info_sent = 0;
static uint32_t boot_ms;
static uint32_t last_oled_ms;
static uint32_t last_oled_retry_ms;
static volatile uint8_t oled_dirty = 1;
static uint32_t last_rgb_ms;
static uint8_t oled_page = OLED_PAGE_STATUS;
static uint8_t visible_page = OLED_PAGE_STATUS;
static uint8_t auto_page = OLED_PAGE_STATUS;
static uint32_t auto_page_started_ms;
static uint32_t last_oled_page_change_ms;
static uint8_t oled_page_change_valid;
static char marquee[2][64] = {{0}, {0}};
static uint32_t btn_fb_until_ms;
static uint8_t rgb_button_mask;
static volatile uint32_t sof_cyc_start, sof_cyc_end;
static volatile uint16_t sof_cyc_count;
static volatile uint8_t sof_cyc_done;
static uint32_t cpu_mhz;

/* USB SOF 回调（由 usbd_conf.c 的 HAL_PCD_SOFCallback 调用）：
   SOF 帧周期由 USB 主机精确锁定为 1ms（与 MCU 时钟无关），
   用 DWT 周期计数 200 个 SOF 的间隔即可测得真实 CPU 主频 */
void app_on_sof(void)
{
  if (!sof_cyc_done)
  {
    sof_cyc_count++;
    if (sof_cyc_count == 1) sof_cyc_start = DWT->CYCCNT;
    else if (sof_cyc_count >= 200)
    {
      sof_cyc_end = DWT->CYCCNT;
      sof_cyc_done = 1;
    }
  }
}

static void process_button_event(const button_event_t *ev);

static void oled_request_redraw(void)
{
  oled_dirty = 1;
}

static void set_visible_page(uint8_t page, uint32_t now_ms)
{
  (void)now_ms;
  if (page > OLED_PAGE_MONITOR) page = OLED_PAGE_STATUS;
  visible_page = page;
  oled_request_redraw();
}

static void set_oled_mode(uint8_t page, uint8_t param, uint32_t now_ms)
{
  (void)param;
  /* 升级过程兼容旧上位机的 AUTO=3。 */
  if (page == 3u) page = OLED_PAGE_AUTO;
  if (page >= OLED_PAGE_COUNT) page = OLED_PAGE_STATUS;
  oled_page = page;
  if (oled_page == OLED_PAGE_AUTO)
  {
    auto_page = OLED_PAGE_STATUS;
    auto_page_started_ms = now_ms;
    set_visible_page(auto_page, now_ms);
  }
  else
    set_visible_page(oled_page, now_ms);
}

static void update_oled_auto_page(uint32_t now_ms)
{
  if (oled_page != OLED_PAGE_AUTO) return;
  if ((uint32_t)(now_ms - auto_page_started_ms) < OLED_AUTO_INTERVAL_MS) return;
  auto_page_started_ms = now_ms;
  auto_page = (uint8_t)((auto_page + 1u) % 2u);
  set_visible_page(auto_page, now_ms);
}

/* 页面切换采用丢弃式冷却，不排队，保证 500ms 内最多翻一页。 */
static uint8_t allow_oled_page_change(uint32_t now_ms)
{
  if (oled_page_change_valid &&
      (uint32_t)(now_ms - last_oled_page_change_ms) < ENCODER_PAGE_COOLDOWN_MS)
    return 0;
  oled_page_change_valid = 1;
  last_oled_page_change_ms = now_ms;
  return 1;
}

static uint32_t oled_refresh_interval_ms(uint8_t page)
{
  if (page == OLED_PAGE_MONITOR) return OLED_REFRESH_MONITOR_MS;
  if (host_linked && host.codex_state == CODEX_STATE_RUNNING)
    return OLED_REFRESH_ACTIVITY_MS;
  return OLED_REFRESH_STATUS_MS;
}

/* 整数正弦表（64 点） */
static const int16_t sin8[64] = {
  0, 12, 25, 37, 49, 61, 72, 83, 93, 102, 110, 118, 124, 129, 134, 137,
  140, 142, 143, 143, 143, 142, 140, 137, 134, 129, 124, 118, 110, 102, 93, 83,
  72, 61, 49, 37, 25, 12, 0, -12, -25, -37, -49, -61, -72, -83, -93, -102,
  -110, -118, -124, -129, -134, -137, -140, -142, -143, -143, -143, -142, -140, -137, -134, -129
};

static const char *codex_state_str(uint8_t s)
{
  switch (s)
  {
    case CODEX_STATE_IDLE:     return "IDLE";
    case CODEX_STATE_RUNNING:  return "RUN";
    case CODEX_STATE_WAITING:  return "WAIT";
    case CODEX_STATE_ERROR:    return "ERROR";
    case CODEX_STATE_COMPLETE: return "DONE";
    default:                   return "OFF";
  }
}

/* ---------------- 协议消息处理 ---------------- */

static void parse_status(const uint8_t *p, uint8_t len)
{
  if (len < 22) return;
  host.codex_state = p[0];
  host.flags = p[1];
  host.primary_rem = p[2];
  host.secondary_rem = p[3];
  host.primary_reset = (uint32_t)p[4] | ((uint32_t)p[5] << 8)
                     | ((uint32_t)p[6] << 16) | ((uint32_t)p[7] << 24);
  host.secondary_reset = (uint32_t)p[8] | ((uint32_t)p[9] << 8)
                       | ((uint32_t)p[10] << 16) | ((uint32_t)p[11] << 24);
  host.ds_available = p[12];
  host.ds_balance_cents = (uint32_t)p[13] | ((uint32_t)p[14] << 8)
                        | ((uint32_t)p[15] << 16) | ((uint32_t)p[16] << 24);
  memcpy(host.ds_currency, p + 17, 4);
  host.ds_currency[3] = 0;
  uint8_t text_len = p[21];
  if (text_len > sizeof(host.text) - 1) text_len = sizeof(host.text) - 1;
  if (text_len > len - 22) text_len = (uint8_t)(len - 22);
  memcpy(host.text, p + 22, text_len);
  host.text[text_len] = 0;
  host.received_ms = HAL_GetTick();
  host_linked = 1;
  oled_request_redraw();
}

static void parse_rgb(const uint8_t *p, uint8_t len)
{
  if (len < 11) return;
  rgb_config_t cfg;
  memset(&cfg, 0, sizeof(cfg));
  cfg.mode = p[0];
  cfg.count = p[1];
  cfg.brightness = p[2];
  cfg.period_ms = (uint16_t)p[3] | ((uint16_t)p[4] << 8);
  cfg.color1 = ((uint32_t)p[5] << 16) | ((uint32_t)p[6] << 8) | p[7];
  cfg.color2 = ((uint32_t)p[8] << 16) | ((uint32_t)p[9] << 8) | p[10];
  rgb_set_config(&cfg);
}

static void parse_rgb_status(const uint8_t *p, uint8_t len)
{
  if (len < 18) return;
  uint32_t colors[6];
  uint8_t i;
  for (i = 0; i < 6u; i++)
  {
    colors[i] = ((uint32_t)p[i * 3u] << 16)
              | ((uint32_t)p[i * 3u + 1u] << 8)
              | p[i * 3u + 2u];
  }
  rgb_set_status_colors_cfg(colors);
}

static void parse_oled_page(const uint8_t *p, uint8_t len)
{
  if (len < 1) return;
  set_oled_mode(p[0], len >= 2 ? p[1] : 0u, HAL_GetTick());
}

static void parse_oled_text(const uint8_t *p, uint8_t len)
{
  if (len < 2) return;
  uint8_t slot = p[0];
  if (slot > 1) return;
  uint8_t tlen = p[1];
  if (tlen > len - 2) tlen = (uint8_t)(len - 2);
  if (tlen > sizeof(marquee[slot]) - 1) tlen = sizeof(marquee[slot]) - 1;
  memcpy(marquee[slot], p + 2, tlen);
  marquee[slot][tlen] = 0;
  oled_request_redraw();
}

static void parse_pc_metrics(const uint8_t *p, uint8_t len)
{
  pc_metrics_decoded_t decoded;
  if (!pc_metrics_parse(&decoded, p, len)) return;

  host.pc_schema = decoded.schema;
  host.pc_flags = decoded.flags;
  host.pc_cpu_percent = decoded.cpu_percent;
  host.pc_memory_percent = decoded.memory_percent;
  host.pc_gpu_percent = decoded.gpu_percent;
  host.pc_cpu_temperature_x10 = decoded.cpu_temperature_x10;
  host.pc_gpu_temperature_x10 = decoded.gpu_temperature_x10;
  host.pc_motherboard_temperature_x10 = decoded.motherboard_temperature_x10;
  host.pc_memory_used_mb = decoded.memory_used_mb;
  host.pc_memory_total_mb = decoded.memory_total_mb;
  host.pc_download_kib_s = decoded.download_kib_s;
  host.pc_upload_kib_s = decoded.upload_kib_s;
  host.pc_received_ms = HAL_GetTick();
  oled_request_redraw();
}

static void send_info(void)
{
  uint8_t info[32];
  memset(info, 0, sizeof(info));
  info[0] = FW_VERSION_MAJOR;
  info[1] = FW_VERSION_MINOR;
  info[2] = BTN_COUNT;
  info[3] = 1; /* 编码器存在 */
  info[4] = oled_get_status();
  info[5] = RGB_LED_COUNT;
  uint16_t uptime = (uint16_t)((HAL_GetTick() - boot_ms) / 1000);
  info[6] = (uint8_t)(uptime & 0xFF);
  info[7] = (uint8_t)(uptime >> 8);
  strncpy((char *)info + 8, FW_MODEL_STRING, 16);
  protocol_send(MSG_INFO, info, 24);
}

void app_on_message(uint8_t type, const uint8_t *payload, uint8_t len)
{
  switch (type)
  {
    case MSG_STATUS:
      parse_status(payload, len);
      break;
    case MSG_RGB_SET:
      parse_rgb(payload, len);
      break;
    case MSG_RGB_STATUS:
      parse_rgb_status(payload, len);
      break;
    case MSG_PC_METRICS:
      parse_pc_metrics(payload, len);
      break;
    case MSG_OLED_PAGE:
      parse_oled_page(payload, len);
      break;
    case MSG_OLED_TEXT:
      parse_oled_text(payload, len);
      break;
    case MSG_CFG_REQ:
      send_info();
      break;
    case MSG_LED0_SET:
      if (len >= 1)
        HAL_GPIO_WritePin(GPIOA, GPIO_PIN_15, payload[0] ? GPIO_PIN_SET : GPIO_PIN_RESET);
      break;
    case MSG_PING:
      if (len >= 4) protocol_send(MSG_PONG, payload, 4);
      break;
    case MSG_OLED_CFG:
      if (len >= 2) oled_set_config(payload[0], payload[1]);
      break;
    case MSG_BTN_INJECT:
      if (len >= 2)
      {
        button_event_t ev;
        ev.index = payload[0];
        ev.kind = payload[1];
        ev.count = 1;
        process_button_event(&ev);
      }
      break;
    default:
    {
      uint8_t ack[2] = {type, 2}; /* 未知消息 */
      protocol_send(MSG_ACK, ack, 2);
      break;
    }
  }
}

/* ---------------- OLED 页面渲染 ---------------- */

static void render_activity_spinner(uint32_t now_ms, uint8_t active)
{
  uint8_t s = (uint8_t)((now_ms / 100) % 8);
  oled_draw_circle(UI_HEADER_SPINNER_CENTER_X, 7, UI_HEADER_SPINNER_RADIUS, 1);
  if (!active)
  {
    oled_draw_pixel(UI_HEADER_SPINNER_CENTER_X, 4, 1);
    return;
  }
  for (uint8_t tail = 0; tail < 3u; tail++)
  {
    uint8_t pos = (uint8_t)((s + 8u - tail) % 8u);
    int16_t x = UI_HEADER_SPINNER_CENTER_X
        + (int16_t)((int16_t)sin8[(pos * 8u) % 64u]
                    * UI_HEADER_SPINNER_RADIUS / 143);
    int16_t y = 7 - (int16_t)((int16_t)sin8[(pos * 8u + 16u) % 64u]
                              * UI_HEADER_SPINNER_RADIUS / 143);
    if (tail == 0u)
      oled_fill_rect((uint8_t)(x - 1), (uint8_t)(y - 1), 2, 2, 1);
    else
      oled_draw_pixel((uint8_t)x, (uint8_t)y, 1);
  }
}

static const uint8_t *compact_network_glyph(char ch)
{
  static const uint8_t glyphs[][3] =
  {
    {0x1fu, 0x11u, 0x1fu}, /* 0 */
    {0x12u, 0x1fu, 0x10u}, /* 1 */
    {0x1du, 0x15u, 0x17u}, /* 2 */
    {0x15u, 0x15u, 0x1fu}, /* 3 */
    {0x07u, 0x04u, 0x1fu}, /* 4 */
    {0x17u, 0x15u, 0x1du}, /* 5 */
    {0x1fu, 0x15u, 0x1du}, /* 6 */
    {0x01u, 0x01u, 0x1fu}, /* 7 */
    {0x1fu, 0x15u, 0x1fu}, /* 8 */
    {0x17u, 0x15u, 0x1fu}, /* 9 */
    {0x1fu, 0x0au, 0x11u}, /* K */
    {0x1fu, 0x06u, 0x1fu}, /* M */
    {0x1fu, 0x11u, 0x1du}, /* G */
    {0x00u, 0x10u, 0x00u}, /* . */
    {0x04u, 0x04u, 0x04u}, /* - */
    {0x02u, 0x1fu, 0x02u}, /* U: up arrow */
    {0x08u, 0x1fu, 0x08u}  /* D: down arrow */
  };
  static const char symbols[] = "0123456789KMG.-UD";

  const char *found = strchr(symbols, ch);
  if (found == NULL) return NULL;
  return glyphs[(uint8_t)(found - symbols)];
}

static void draw_compact_network_string(uint8_t x, uint8_t y,
                                        const char *text)
{
  while (*text != '\0')
  {
    const uint8_t *glyph = compact_network_glyph(*text++);
    if (glyph != NULL)
    {
      for (uint8_t col = 0u; col < UI_HEADER_COMPACT_WIDTH; col++)
        for (uint8_t row = 0u; row < 5u; row++)
          if ((glyph[col] & (uint8_t)(1u << row)) != 0u)
            oled_draw_pixel((uint8_t)(x + col), (uint8_t)(y + row), 1);
    }
    x = (uint8_t)(x + UI_HEADER_COMPACT_ADVANCE);
  }
}

static void render_header_network(uint32_t now_ms, uint8_t show_spinner,
                                  uint8_t active)
{
  char upload[6];
  char download[6];
  uint8_t valid = (uint8_t)(host_linked
      && (host.pc_flags & PC_FLAG_NETWORK_VALID));

  if (show_spinner) render_activity_spinner(now_ms, active);
  ui_header_format_speed(upload, sizeof(upload), 'U',
                         host.pc_upload_kib_s, valid);
  ui_header_format_speed(download, sizeof(download), 'D',
                         host.pc_download_kib_s, valid);
  draw_compact_network_string(UI_HEADER_UPLOAD_X, 4u, upload);
  draw_compact_network_string(UI_HEADER_DOWNLOAD_X, 4u, download);
}

#define UI_HEADER_RULE_WIDTH 52u

static void draw_short_header_rule(uint8_t y)
{
  oled_draw_hline(3, y, UI_HEADER_RULE_WIDTH, 1);
}

static void draw_dotted_hline(uint8_t y)
{
  for (uint8_t x = 3u; x <= 124u; x += 6u)
    oled_draw_pixel(x, y, 1);
}

#define QUOTA_BAR_WIDTH 57u
#define QUOTA_BAR_HEIGHT 5u

/* 稀疏额度轨道：|------>       |。箭头之后保持空白，减少常亮像素。 */
static void draw_sparse_quota_bar(uint8_t x, uint8_t y,
                                  uint8_t value, uint8_t valid)
{
  uint8_t right = (uint8_t)(x + QUOTA_BAR_WIDTH - 1u);
  uint8_t start = (uint8_t)(x + 3u);

  oled_draw_vline(x, y, QUOTA_BAR_HEIGHT, 1);
  oled_draw_vline(right, y, QUOTA_BAR_HEIGHT, 1);
  if (!valid) return;

  if (value > 100u) value = 100u;
  uint8_t marker = (uint8_t)(start
      + ((uint16_t)(right - 3u - start) * value) / 100u);

  for (uint8_t px = start; px < marker; px += 4u)
  {
    oled_draw_pixel(px, y + 2u, 1);
    if ((uint8_t)(px + 1u) < marker)
      oled_draw_pixel(px + 1u, y + 2u, 1);
  }

  oled_draw_pixel(marker, y, 1);
  oled_draw_pixel(marker + 1u, y + 1u, 1);
  oled_draw_pixel(marker + 2u, y + 2u, 1);
  oled_draw_pixel(marker + 1u, y + 3u, 1);
  oled_draw_pixel(marker, y + 4u, 1);
}

static void format_status_metric(char *buf, const char *label,
                                 uint8_t valid, uint8_t value)
{
  if (valid && value <= 100u)
    sprintf(buf, "%s%u%%", label, (unsigned)value);
  else
    sprintf(buf, "%s--", label);
}

static void render_page_status(uint32_t now_ms)
{
  char buf[32];
  char primary_buf[12];
  char secondary_buf[12];
  char cpu_buf[8];
  char ram_buf[8];
  char gpu_buf[8];
  uint8_t primary_valid = (uint8_t)(host_linked && host.primary_rem <= 100u);
  uint8_t secondary_valid = (uint8_t)(host_linked && host.secondary_rem <= 100u);
  oled_clear(0);
  oled_draw_string(UI_HEADER_CODEX_TEXT_X, 3, "CODEX ", 1);
  oled_draw_string(UI_HEADER_STATUS_X, 3,
      codex_state_str(host_linked ? host.codex_state : CODEX_STATE_OFFLINE), 1);
  render_header_network(now_ms, 1u,
      (uint8_t)(host_linked && host.codex_state == CODEX_STATE_RUNNING));
  draw_short_header_rule(12u);

  if (primary_valid)
    sprintf(primary_buf, "PRI %u%%", (unsigned)host.primary_rem);
  else
    strcpy(primary_buf, "PRI n/a");
  if (secondary_valid)
    sprintf(secondary_buf, "SEC %u%%", (unsigned)host.secondary_rem);
  else
    strcpy(secondary_buf, "SEC n/a");
  oled_draw_string(3, 16, primary_buf, 1);
  oled_draw_string(67, 16, secondary_buf, 1);

  draw_sparse_quota_bar(3u, 25u, host.primary_rem, primary_valid);
  draw_sparse_quota_bar(67u, 25u, host.secondary_rem, secondary_valid);

  draw_dotted_hline(31u);

  if (host_linked && host.ds_available)
  {
    if (host.flags & 0x01u)
      oled_draw_string(3, 34, "API U/L", 1);
    else
    {
      sprintf(buf, "API CNY%lu.%02lu",
              (unsigned long)(host.ds_balance_cents / 100),
              (unsigned long)(host.ds_balance_cents % 100));
      oled_draw_string(3, 34, buf, 1);
    }
  }
  else
    oled_draw_string(3, 34, "API n/a", 1);

  draw_dotted_hline(45u);
  format_status_metric(cpu_buf, "CPU",
      (uint8_t)(host_linked && (host.pc_flags & PC_FLAG_CPU_VALID)),
      host.pc_cpu_percent);
  format_status_metric(ram_buf, "RAM",
      (uint8_t)(host_linked && (host.pc_flags & PC_FLAG_MEMORY_VALID)),
      host.pc_memory_percent);
  format_status_metric(gpu_buf, "GPU",
      (uint8_t)(host_linked && (host.pc_flags & PC_FLAG_GPU_LOAD_VALID)),
      host.pc_gpu_percent);
  oled_draw_string(3, 50, cpu_buf, 1);
  oled_draw_string(44, 50, ram_buf, 1);
  oled_draw_string(85, 50, gpu_buf, 1);
}

/* ---------- PC MON 角落三维场景 ---------- */

#define MONITOR_TEXT_RIGHT  87
#define MONITOR_VIEW_X      91
#define MONITOR_VIEW_Y      22
#define MONITOR_VIEW_W      33
#define MONITOR_VIEW_H      32
#define MONITOR_VIEW_CX     107
#define MONITOR_VIEW_CY     38

typedef struct { float x, y, z; } vec3f;
typedef struct { int16_t x, y; float z; } projected3f_t;
typedef struct { uint8_t index; float depth; } depth_index_t;

static const vec3f cube_verts[8] = {
  {-0.55f,-0.55f,-0.55f}, { 0.55f,-0.55f,-0.55f},
  { 0.55f, 0.55f,-0.55f}, {-0.55f, 0.55f,-0.55f},
  {-0.55f,-0.55f, 0.55f}, { 0.55f,-0.55f, 0.55f},
  { 0.55f, 0.55f, 0.55f}, {-0.55f, 0.55f, 0.55f}
};

static const uint8_t cube_edges[12][2] = {
  {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4},
  {0,4},{1,5},{2,6},{3,7}
};

enum
{
  STICK_NECK = 0,
  STICK_HIP,
  STICK_LEFT_HAND,
  STICK_RIGHT_HAND,
  STICK_LEFT_FOOT,
  STICK_RIGHT_FOOT,
  STICK_JOINT_COUNT
};

static const vec3f stick_joints[STICK_JOINT_COUNT] = {
  { 0.00f,-0.17f, 0.00f}, { 0.00f, 0.18f, 0.00f},
  {-0.28f, 0.00f,-0.06f}, { 0.28f,-0.02f, 0.06f},
  {-0.19f, 0.43f,-0.05f}, { 0.20f, 0.43f, 0.07f}
};

static const uint8_t stick_segments[5][2] = {
  {STICK_NECK,STICK_HIP},
  {STICK_NECK,STICK_LEFT_HAND}, {STICK_NECK,STICK_RIGHT_HAND},
  {STICK_HIP,STICK_LEFT_FOOT}, {STICK_HIP,STICK_RIGHT_FOOT}
};

static vec3f rotate_scene(vec3f v, float cosy, float siny,
                          float cost, float sint)
{
  vec3f r;
  float x1 = v.x * cosy + v.z * siny;
  float z1 = -v.x * siny + v.z * cosy;
  r.x = x1;
  r.y = v.y * cost - z1 * sint;
  r.z = v.y * sint + z1 * cost;
  return r;
}

/* 透视除法让靠近观察者的线条自然放大，并保留相机空间 z 供遮挡排序。 */
static void project_perspective(vec3f v, float cosy, float siny,
                                float cost, float sint,
                                projected3f_t *out)
{
  vec3f r = rotate_scene(v, cosy, siny, cost, sint);
  float depth = 3.0f - r.z;
  if (depth < 1.2f) depth = 1.2f;
  float scale = 48.0f / depth;
  float sx = (float)MONITOR_VIEW_CX + r.x * scale;
  float sy = (float)MONITOR_VIEW_CY + r.y * scale;
  out->x = (int16_t)(sx + (sx >= 0.0f ? 0.5f : -0.5f));
  out->y = (int16_t)(sy + (sy >= 0.0f ? 0.5f : -0.5f));
  out->z = r.z;
}

static uint8_t monitor_point_visible(int16_t x, int16_t y)
{
  return (uint8_t)(x >= MONITOR_VIEW_X
      && x < MONITOR_VIEW_X + MONITOR_VIEW_W
      && y >= MONITOR_VIEW_Y
      && y < MONITOR_VIEW_Y + MONITOR_VIEW_H);
}

static void draw_dotted_scene_line(int16_t x0, int16_t y0,
                                   int16_t x1, int16_t y1)
{
  int16_t dx = x1 >= x0 ? (int16_t)(x1 - x0) : (int16_t)(x0 - x1);
  int16_t sx = x0 < x1 ? 1 : -1;
  int16_t dy_abs = y1 >= y0 ? (int16_t)(y1 - y0) : (int16_t)(y0 - y1);
  int16_t dy = (int16_t)-dy_abs;
  int16_t sy = y0 < y1 ? 1 : -1;
  int16_t err = (int16_t)(dx + dy);
  uint8_t phase = 0;
  for (;;)
  {
    if ((phase++ & 1u) == 0u && monitor_point_visible(x0, y0))
      oled_draw_pixel((uint8_t)x0, (uint8_t)y0, 1);
    if (x0 == x1 && y0 == y1) break;
    int16_t e2 = (int16_t)(2 * err);
    if (e2 >= dy) { err = (int16_t)(err + dy); x0 = (int16_t)(x0 + sx); }
    if (e2 <= dx) { err = (int16_t)(err + dx); y0 = (int16_t)(y0 + sy); }
  }
}

static void sort_depth_indices(depth_index_t *items, uint8_t count)
{
  for (uint8_t i = 1; i < count; i++)
  {
    depth_index_t key = items[i];
    int8_t j = (int8_t)i - 1;
    while (j >= 0 && items[(uint8_t)j].depth > key.depth)
    {
      items[(uint8_t)(j + 1)] = items[(uint8_t)j];
      j--;
    }
    items[(uint8_t)(j + 1)] = key;
  }
}

static void draw_projected_line(projected3f_t a, projected3f_t b)
{
  if (monitor_point_visible(a.x, a.y) && monitor_point_visible(b.x, b.y))
    oled_draw_line(a.x, a.y, b.x, b.y, 1);
}

static void draw_stick_figure(float cosy, float siny, float cost, float sint)
{
  projected3f_t joints[STICK_JOINT_COUNT];
  depth_index_t order[5];
  for (uint8_t i = 0; i < STICK_JOINT_COUNT; i++)
    project_perspective(stick_joints[i], cosy, siny, cost, sint, &joints[i]);
  for (uint8_t i = 0; i < 5u; i++)
  {
    order[i].index = i;
    order[i].depth = (joints[stick_segments[i][0]].z
                    + joints[stick_segments[i][1]].z) * 0.5f;
  }
  sort_depth_indices(order, 5u);
  for (uint8_t i = 0; i < 5u; i++)
  {
    uint8_t segment = order[i].index;
    draw_projected_line(joints[stick_segments[segment][0]],
                        joints[stick_segments[segment][1]]);
  }

  /* 头部是局部 XY 平面上的 10 段圆环，随立方体一起旋转成透视椭圆。 */
  projected3f_t first, previous, current;
  for (uint8_t i = 0; i <= 10u; i++)
  {
    float t = (float)(i % 10u) * 0.62831853f;
    vec3f p = {0.12f * cosf(t), -0.34f + 0.12f * sinf(t), 0.0f};
    project_perspective(p, cosy, siny, cost, sint, &current);
    if (i == 0u) first = current;
    else draw_projected_line(previous, current);
    previous = current;
  }
  (void)first;
}

static void draw_monitor_scene(uint32_t now_ms)
{
  float yaw = (float)(now_ms % 8000u) * 0.00078539816f;
  float tilt = 0.48f;
  float cosy = cosf(yaw), siny = sinf(yaw);
  float cost = cosf(tilt), sint = sinf(tilt);
  projected3f_t projected[8];
  depth_index_t order[12];

  for (uint8_t i = 0; i < 8u; i++)
    project_perspective(cube_verts[i], cosy, siny, cost, sint, &projected[i]);
  for (uint8_t i = 0; i < 12u; i++)
  {
    order[i].index = i;
    order[i].depth = (projected[cube_edges[i][0]].z
                    + projected[cube_edges[i][1]].z) * 0.5f;
  }
  sort_depth_indices(order, 12u);

  /* 后六条棱先画成点线，火柴人居中，前六条实线最后覆盖交点。 */
  for (uint8_t i = 0; i < 6u; i++)
  {
    uint8_t edge = order[i].index;
    draw_dotted_scene_line(projected[cube_edges[edge][0]].x,
                           projected[cube_edges[edge][0]].y,
                           projected[cube_edges[edge][1]].x,
                           projected[cube_edges[edge][1]].y);
  }
  draw_stick_figure(cosy, siny, cost, sint);
  for (uint8_t i = 6u; i < 12u; i++)
  {
    uint8_t edge = order[i].index;
    draw_projected_line(projected[cube_edges[edge][0]],
                        projected[cube_edges[edge][1]]);
  }
}

static int16_t rounded_temperature(int16_t temperature_x10)
{
  return (int16_t)((temperature_x10 >= 0 ? temperature_x10 + 5
                                         : temperature_x10 - 5) / 10);
}

static void render_page_monitor(uint32_t now_ms)
{
  char buf[24];
  uint8_t stale = (uint8_t)((host.pc_flags & PC_FLAG_STALE) != 0u);
  oled_clear(0);
  oled_draw_string(3, 3, "PC MON", 1);
  if (stale) oled_draw_string(46, 3, "~", 1);
  render_header_network(now_ms, 0u,
      (uint8_t)(host_linked && host.codex_state == CODEX_STATE_RUNNING));
  draw_short_header_rule(13u);

  if (host.pc_flags & PC_FLAG_CPU_VALID)
  {
    if ((host.pc_flags & PC_FLAG_CPU_TEMP_VALID)
        && host.pc_cpu_temperature_x10 != PC_TEMP_UNKNOWN)
      sprintf(buf, "CPU %u%%|%dC", (unsigned)host.pc_cpu_percent,
              (int)rounded_temperature(host.pc_cpu_temperature_x10));
    else sprintf(buf, "CPU %u%%|--C", (unsigned)host.pc_cpu_percent);
  }
  else strcpy(buf, "CPU --|--C");
  oled_draw_string(3, 16, buf, 1);

  if (host.pc_flags & PC_FLAG_GPU_LOAD_VALID)
  {
    if ((host.pc_flags & PC_FLAG_GPU_TEMP_VALID)
        && host.pc_gpu_temperature_x10 != PC_TEMP_UNKNOWN)
      sprintf(buf, "GPU %u%%|%dC", (unsigned)host.pc_gpu_percent,
              (int)rounded_temperature(host.pc_gpu_temperature_x10));
    else sprintf(buf, "GPU %u%%|--C", (unsigned)host.pc_gpu_percent);
  }
  else if ((host.pc_flags & PC_FLAG_GPU_TEMP_VALID)
           && host.pc_gpu_temperature_x10 != PC_TEMP_UNKNOWN)
    sprintf(buf, "GPU --|%dC", (int)rounded_temperature(host.pc_gpu_temperature_x10));
  else strcpy(buf, "GPU --|--C");
  oled_draw_string(3, 27, buf, 1);

  if (host.pc_flags & PC_FLAG_MEMORY_VALID)
  {
    uint32_t used_gb = (host.pc_memory_used_mb + 512u) / 1024u;
    uint32_t total_gb = (host.pc_memory_total_mb + 512u) / 1024u;
    if (used_gb < 100u && total_gb < 100u)
      sprintf(buf, "RAM %u|%lu/%luG", (unsigned)host.pc_memory_percent,
              (unsigned long)used_gb, (unsigned long)total_gb);
    else
      sprintf(buf, "RAM %u|%luG", (unsigned)host.pc_memory_percent,
              (unsigned long)used_gb);
  }
  else strcpy(buf, "RAM --");
  oled_draw_string(3, 38, buf, 1);

  if ((host.pc_flags & PC_FLAG_MB_TEMP_VALID)
      && host.pc_motherboard_temperature_x10 != PC_TEMP_UNKNOWN)
  {
    int16_t temp = host.pc_motherboard_temperature_x10;
    int16_t whole = (int16_t)(temp / 10);
    int16_t frac = (int16_t)(temp < 0 ? -temp : temp) % 10;
    sprintf(buf, "MB  %d.%dC", (int)whole, (int)frac);
  }
  else strcpy(buf, "MB  --C");
  oled_draw_string(3, 49, buf, 1);

  /* 点状分隔带让信息区和三维视口保持清晰，不占用完整竖线。 */
  for (uint8_t y = 18u; y <= 58u; y += 4u)
    oled_draw_pixel((uint8_t)(MONITOR_TEXT_RIGHT + 2), y, 1);
  draw_monitor_scene(now_ms);
}

static void draw_noise_corner_frame(void)
{
  const uint8_t arm = 8u;
  oled_draw_hline(1, 1, arm, 1);
  oled_draw_vline(1, 1, arm, 1);
  oled_draw_hline((uint8_t)(OLED_WIDTH - 1u - arm), 1, arm, 1);
  oled_draw_vline((uint8_t)(OLED_WIDTH - 2u), 1, arm, 1);
  oled_draw_hline(1, (uint8_t)(OLED_HEIGHT - 2u), arm, 1);
  oled_draw_vline(1, (uint8_t)(OLED_HEIGHT - 1u - arm), arm, 1);
  oled_draw_hline((uint8_t)(OLED_WIDTH - 1u - arm),
                  (uint8_t)(OLED_HEIGHT - 2u), arm, 1);
  oled_draw_vline((uint8_t)(OLED_WIDTH - 2u),
                  (uint8_t)(OLED_HEIGHT - 1u - arm), arm, 1);
}

static void draw_noise_monitor_separator(void)
{
  for (uint8_t y = 18u; y <= 58u; y += 4u)
    oled_draw_pixel((uint8_t)(MONITOR_TEXT_RIGHT + 2), y, 1);
}

static void render_noise_manual(void)
{
  char buf[20];
  oled_noise_manual_profile_t profile;
  oled_clear(0);
  if (!oled_noise_manual_get_profile(&profile))
  {
    oled_draw_string(3, 3, "NOISE TEST ERROR", 1);
    return;
  }

  sprintf(buf, "PC MON A/B %u", (unsigned)profile.index);
  oled_draw_string(3, 3, buf, 1);
  oled_draw_string(3, 16, "CPU 35%|52C", 1);
  oled_draw_string(3, 27, "GPU 20%|41C", 1);
  oled_draw_string(3, 38, "RAM 49|16/32G", 1);
  oled_draw_string(3, 49, "MB  27.9C", 1);

  if (profile.ui_style <= OLED_NOISE_UI_NO_RULES)
  {
    render_activity_spinner(0u, 0u);
    draw_monitor_scene(0u);
  }
  if (profile.ui_style <= OLED_NOISE_UI_SHORT_RULE)
    draw_noise_monitor_separator();

  if (profile.ui_style == OLED_NOISE_UI_FULL_FRAME)
    oled_draw_rect(1, 1, 126, 62, 1);
  else if (profile.ui_style == OLED_NOISE_UI_CORNER_FRAME)
    draw_noise_corner_frame();

  if (profile.ui_style <= OLED_NOISE_UI_NO_OUTER)
    oled_draw_hline(3, 13, 122, 1);
  else if (profile.ui_style == OLED_NOISE_UI_SHORT_RULE)
    oled_draw_hline(3, 13, 52, 1);
}

static void oled_render(uint32_t now_ms)
{
  uint8_t noise_manual = oled_noise_manual_active();
  if (noise_manual)
  {
    render_noise_manual();
  }
  else switch (visible_page)
  {
    case OLED_PAGE_STATUS:  render_page_status(now_ms); break;
    case OLED_PAGE_MONITOR: render_page_monitor(now_ms); break;
    default:                render_page_monitor(now_ms); break;
  }
  /* 按键反馈放在圆圈与网速之间的空白处。 */
  if (!noise_manual && now_ms < btn_fb_until_ms)
    oled_fill_rect(78u, 3u, 3u, 3u, 1);
  oled_flush();
  if (noise_manual) oled_noise_manual_mark_ui_submitted();
}

/* ---------------- RGB 状态色 ---------------- */

static void update_rgb(uint32_t now_ms)
{
  const rgb_config_t *cfg = rgb_get_config();
  if (cfg->mode == RGB_MODE_STATUS)
  {
    uint8_t state = host_linked ? host.codex_state : CODEX_STATE_OFFLINE;
    uint32_t colors[RGB_LED_COUNT];
    uint8_t br[RGB_LED_COUNT];
    uint8_t bright = cfg->brightness;
    uint32_t period = cfg->period_ms;
    if (period < 100u) period = 100u;
    uint32_t half = period / 2u;
    uint8_t i;
    for (i = 0; i < RGB_LED_COUNT; i++) { colors[i] = 0u; br[i] = 0; }

    switch (state)
    {
      case CODEX_STATE_IDLE: /* 空闲：蓝色呼吸 */
      {
        uint32_t t = now_ms % period;
        uint16_t wave = (t < half) ? (uint16_t)(255u * t / half)
                                   : (uint16_t)(255u * (period - t) / half);
        uint8_t b = (uint16_t)bright * wave / 255u;
        for (i = 0; i < RGB_LED_COUNT; i++) { colors[i] = cfg->status_color[0]; br[i] = b; }
        break;
      }
      case CODEX_STATE_RUNNING: /* 运行：状态色沿八灯反向流动 */
        for (i = 0; i < RGB_LED_COUNT; i++)
        {
          uint32_t phase_index = (uint32_t)(RGB_LED_COUNT - 1u - i);
          uint32_t t = (now_ms + phase_index * period / RGB_LED_COUNT) % period;
          uint16_t wave = (t < half) ? (uint16_t)(255u * t / half)
                                     : (uint16_t)(255u * (period - t) / half);
          colors[i] = cfg->status_color[1];
          br[i] = (uint16_t)bright * wave / 255u;
        }
        break;
      case CODEX_STATE_WAITING: /* 等待答复：红色常亮 */
        for (i = 0; i < RGB_LED_COUNT; i++) { colors[i] = cfg->status_color[2]; br[i] = bright; }
        break;
      case CODEX_STATE_ERROR: /* 错误：红色闪烁 */
      {
        uint8_t on = (uint8_t)((now_ms / half) & 1u);
        for (i = 0; i < RGB_LED_COUNT; i++) { colors[i] = cfg->status_color[3]; br[i] = on ? bright : 0; }
        break;
      }
      case CODEX_STATE_COMPLETE: /* 完成：绿色常亮 */
        for (i = 0; i < RGB_LED_COUNT; i++) { colors[i] = cfg->status_color[4]; br[i] = bright; }
        break;
      default: /* 离线：暗蓝常亮，避免与解码失败的白光混淆 */
        for (i = 0; i < RGB_LED_COUNT; i++) { colors[i] = cfg->status_color[5]; br[i] = bright; }
        break;
    }
    rgb_set_status_leds(colors, br, RGB_LED_COUNT);
  }
  else
  {
    rgb_update(now_ms);
  }
}

/* ---------------- 主循环 ---------------- */

static void process_button_event(const button_event_t *ev)
{
  /* 人工听音模式下任意实体键仅在本地切换候选，避免触发主机/RGB。 */
  if (oled_noise_manual_active()
      && (ev->index < RGB_LED_COUNT || ev->index == ENCODER_SW_INDEX))
  {
    if (ev->kind == BTN_EV_CLICK)
    {
      oled_noise_manual_next();
      oled_request_redraw();
    }
    return;
  }

  uint8_t payload[3] = {ev->index, ev->kind, ev->count};
  protocol_send(MSG_EVT_BUTTON, payload, 3);

  /* KEY1~KEY8 按住时覆盖对应 RGB；松开后恢复底层模式。 */
  if (ev->index < RGB_LED_COUNT)
  {
    uint8_t bit = (uint8_t)(1u << ev->index);
    if (ev->kind == BTN_EV_PRESS)
    {
      rgb_button_mask |= bit;
      rgb_set_button_mask(rgb_button_mask);
    }
    else if (ev->kind == BTN_EV_RELEASE || ev->kind == BTN_EV_LONG_RELEASE)
    {
      rgb_button_mask &= (uint8_t)~bit;
      rgb_set_button_mask(rgb_button_mask);
    }
  }

  /* 本地按键反馈：按下的瞬间在 OLED 右上角显示小图标 */
  if (ev->kind == BTN_EV_PRESS)
  {
    btn_fb_until_ms = HAL_GetTick() + 400;
    oled_request_redraw();
  }

  /* 主机离线时的本地兜底：编码器按键/旋转切换 OLED 页面 */
  if (!host_linked && ev->index == ENCODER_SW_INDEX)
  {
    if (ev->kind == BTN_EV_CLICK && allow_oled_page_change(HAL_GetTick()))
    {
      set_oled_mode((uint8_t)((oled_page + 1u) % OLED_PAGE_COUNT), 0u,
                    HAL_GetTick());
    }
  }
}

void app_init(void)
{
  boot_ms = HAL_GetTick();
  oled_page = OLED_PAGE_STATUS;
  visible_page = OLED_PAGE_STATUS;
  auto_page = OLED_PAGE_STATUS;
  auto_page_started_ms = boot_ms;
  memset(&host, 0, sizeof(host));
  memset(marquee, 0, sizeof(marquee));
  rgb_button_mask = 0u;
  strcpy(marquee[0], "CodeX Tools");

  oled_init();
  rgb_init();
  buttons_init();
  encoder_init();
  protocol_init();

  oled_clear(0);
  oled_draw_string(6, 24, "CodeX Tools", 1);
  char version[12];
  sprintf(version, "FW %u.%u", (unsigned)FW_VERSION_MAJOR,
          (unsigned)FW_VERSION_MINOR);
  oled_draw_string(6, 36, version, 1);
  oled_flush();
  oled_noise_diag_start(HAL_GetTick());
  oled_dirty = 1;

  /* 使能 USB SOF 中断用于主频测量（PCD 初始化默认关闭） */
  USB->CNTR |= USB_CNTR_SOFM;
}

void app_tick(void)
{
  uint32_t now = HAL_GetTick();

  /* OLED DMA 只在主循环推进，避免在 I2C 中断中串接下一笔事务。 */
  oled_process();
  oled_noise_diag_process(now);
  rgb_process(now);

  /* 通信 */
  protocol_process();
  protocol_tx_poll();

  /* 周期性加固 SOF 中断使能位（防止 USB 复位/枚举期间被清除） */
  USB->CNTR |= USB_CNTR_SOFM;

  /* 主频测量结果更新（USB 连接后自动生效） */
  if (sof_cyc_done)
  {
    cpu_mhz = (sof_cyc_end - sof_cyc_start) / 200000u;
    sof_cyc_done = 0;
    sof_cyc_count = 0;
    /* 拿到真实主频后校准 WS2812 时序（只做一次） */
    if (!rgb_is_calibrated() && cpu_mhz >= 40u)
    {
      rgb_recalibrate(cpu_mhz);
    }
  }

  /* USB 连接后主动上报 INFO */
  uint8_t configured = (hUsbDeviceFS.dev_state == USBD_STATE_CONFIGURED);
  if (configured && !usb_was_configured)
  {
    info_sent = 0;
  }
  usb_was_configured = configured;
  if (configured && !info_sent && now - boot_ms > 500)
  {
    info_sent = 1;
    send_info();
  }

  /* 主机离线判定 */
  if (host_linked && host.received_ms != 0 && now - host.received_ms > HOST_LINK_TIMEOUT_MS)
  {
    host_linked = 0;
    oled_request_redraw();
  }
  if (!configured && host_linked)
  {
    host_linked = 0;
    oled_request_redraw();
  }

  /* 按键 / 编码器 */
  buttons_process(now);
  encoder_process(now);
  button_event_t ev;
  while (buttons_pop(&ev)) process_button_event(&ev);
  int8_t delta = encoder_take_delta();
  if (delta != 0)
  {
    uint8_t payload[1] = {(uint8_t)delta};
    protocol_send(MSG_EVT_ENCODER, payload, 1);
    if (!oled_noise_manual_active() && !host_linked
        && allow_oled_page_change(now))
    {
      int16_t next = (int16_t)oled_page + delta;
      while (next < 0) next += OLED_PAGE_COUNT;
      set_oled_mode((uint8_t)(next % OLED_PAGE_COUNT), 0u, now);
    }
  }

  if (btn_fb_until_ms != 0 && (int32_t)(now - btn_fb_until_ms) >= 0)
  {
    btn_fb_until_ms = 0;
    oled_request_redraw();
  }

  /* OLED 模块上电可能晚于 MCU：首次探测失败后每 500ms 自动重试，
     避免“黑屏后只有上位机重新初始化才能恢复”的问题 */
  if (!oled_is_ok() && now - last_oled_retry_ms >= 500)
  {
    last_oled_retry_ms = now;
    oled_init();
    oled_request_redraw();
  }

  update_oled_auto_page(now);

  /* OLED 刷新：PC MON 25Hz；CODEX 运行 10Hz、空闲 2Hz；事件可立即刷新。 */
  if (oled_dirty || now - last_oled_ms >= oled_refresh_interval_ms(visible_page))
  {
    last_oled_ms = now;
    oled_dirty = 0;
    oled_render(now);
  }

  /* RGB 刷新（30Hz） */
  if (now - last_rgb_ms >= 33)
  {
    last_rgb_ms = now;
    update_rgb(now);
  }

  /* LED0 心跳：在线 1s 周期亮 100ms，离线 400ms 周期亮 100ms（低电平点亮） */
//  uint32_t led_period = host_linked ? 1000u : 400u;
//  uint32_t led_phase = now % led_period;
//  HAL_GPIO_WritePin(GPIOA, GPIO_PIN_15,
//                    led_phase < 200u ? GPIO_PIN_RESET : GPIO_PIN_SET);
}
