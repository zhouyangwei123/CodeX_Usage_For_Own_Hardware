#include "usage_oled.h"
#include "oled.h"
#include <stdio.h>
#include <string.h>

static uint64_t read64(const uint8_t *p)
{
  uint64_t value = 0;
  for (unsigned i = 0; i < 8; i++) value |= (uint64_t)p[i] << (8u * i);
  return value;
}
uint8_t usage_oled_parse(usage_oled_t *out, const uint8_t *p, uint8_t len)
{
  if (!out || !p || len < USAGE_HEADER_SIZE || p[0] != 1 || p[1] > 1 ||
      p[48] > USAGE_MAX_POINTS || len != USAGE_HEADER_SIZE + p[48] || p[49] != 0 ||
      (p[3] > 100 && p[3] != 255)) return 0;
  usage_oled_t next;
  memset(&next, 0, sizeof(next));
  next.view = p[1]; next.flags = p[2]; next.remaining = p[3]; next.count = p[48];
  next.month = p[50]; next.day = p[51];
  for (unsigned i = 0; i < 4; i++) next.reset_seconds |= (uint32_t)p[4+i] << (8u*i);
  next.upper = read64(p+8); next.current = read64(p+16);
  next.input = read64(p+24); next.output = read64(p+32); next.balance_cents = read64(p+40);
  if ((next.flags & USAGE_QUOTA_KNOWN) && next.remaining > 100) return 0;
  if ((next.flags & USAGE_KNOWN) && (!next.upper || next.current > next.upper || next.count < 2)) return 0;
  if ((next.flags & USAGE_KNOWN) && !next.view &&
      (next.input > UINT64_MAX-next.output || next.current != next.input+next.output)) return 0;
  if (next.month > 12 || next.day > 31 || (!next.month != !next.day)) return 0;
  for (unsigned i = 0; i < next.count; i++)
  {
    if (p[USAGE_HEADER_SIZE+i] > 34 && p[USAGE_HEADER_SIZE+i] != 255) return 0;
    next.points[i] = p[USAGE_HEADER_SIZE+i];
  }
  *out = next;
  return 1;
}

/* The original network alphabet is kept first; additions serve ticks/footer. */
static const char small_symbols[] = "0123456789KMG.-UD API/mhdLTFNE!";
static const uint8_t small_font[][3] = {
  {31,17,31},{18,31,16},{29,21,23},{21,21,31},{7,4,31},{23,21,29},
  {31,21,29},{1,1,31},{31,21,31},{23,21,31},{31,10,17},{31,6,31},
  {31,17,29},{0,16,0},{4,4,4},{2,31,2},{8,31,8},{0,0,0},
  {30,5,30},{31,5,7},{17,31,17},{24,4,3},{30,2,30},{31,4,24},
  {12,18,31},{31,16,16},{1,31,1},{31,5,1},{31,2,31},{31,21,17},{0,23,0}
};
void usage_oled_compact(uint8_t x, uint8_t y, const char *text)
{
  for (; *text; text++, x = (uint8_t)(x+4))
  {
    const char *found = strchr(small_symbols, *text);
    if (!found) continue;
    const uint8_t *glyph = small_font[found-small_symbols];
    for (uint8_t col = 0; col < 3; col++)
      for (uint8_t bit = 0; bit < 5; bit++)
        if (glyph[col] & (1u << bit)) oled_draw_pixel((uint8_t)(x+col), (uint8_t)(y+bit), 1);
  }
}

static void number_text(char *out, size_t size, uint64_t value, uint8_t whole, uint64_t unit)
{
  static const char *suffix[] = {"","K","M","G","T","P","E"};
  unsigned index = 0;
  while (value/unit >= 1000 && index < 6) { unit *= 1000; index++; }
  uint64_t n = value/unit, rest = value%unit;
  if (whole || n >= 100)
  {
    n += rest >= (unit+1)/2;
    if (n >= 1000 && index < 6) { index++; n = (n+500)/1000; }
    snprintf(out, size, "%u%s", (unsigned)n, suffix[index]);
  }
  else
  {
    unsigned tenths = (unsigned)(n*10 + (rest*10+unit/2)/unit);
    if (tenths%10) snprintf(out, size, "%u.%u%s", tenths/10, tenths%10, suffix[index]);
    else snprintf(out, size, "%u%s", tenths/10, suffix[index]);
  }
}
static void count_text(char *out, size_t size, uint64_t value, uint8_t whole)
{ number_text(out, size, value, whole, 1); }
static void api_text(char *out, size_t size, const usage_oled_t *data, uint8_t fresh)
{
  if (!fresh || !(data->flags & USAGE_API_KNOWN) || (data->flags & USAGE_API_STALE))
    snprintf(out, size, "API --");
  else if (data->balance_cents == UINT64_MAX) snprintf(out, size, "API INF");
  else if (data->balance_cents < 10000)
    snprintf(out, size, "API %u.%02u", (unsigned)(data->balance_cents/100), (unsigned)(data->balance_cents%100));
  else
  {
    char amount[12];
    number_text(amount, sizeof(amount), data->balance_cents, 0, 100);
    snprintf(out, size, "API %s", amount);
  }
}
void usage_oled_render(const usage_oled_t *data, uint8_t fresh,
                       uint32_t age_seconds, const char *upload, const char *download)
{
  char buf[48], current[12], in[12], out[12], api[16];
  uint8_t quota = (uint8_t)(fresh && (data->flags & USAGE_QUOTA_KNOWN) && !(data->flags & USAGE_QUOTA_STALE));
  uint8_t usage = (uint8_t)(fresh && (data->flags & USAGE_KNOWN) && !(data->flags & USAGE_STALE));
  oled_clear(0);
  if (quota) snprintf(buf, sizeof(buf), "7D %u%%", data->remaining);
  else snprintf(buf, sizeof(buf), "7D --");
  oled_draw_string(2, 0, buf, 1);
  if (quota && (data->flags & USAGE_RESET_KNOWN))
  {
    uint32_t seconds = data->reset_seconds > age_seconds ? data->reset_seconds-age_seconds : 0;
    if (seconds >= 100u*86400u) snprintf(buf, sizeof(buf), ">99d");
    else if (seconds >= 86400) snprintf(buf, sizeof(buf), "%ud%02uh", (unsigned)(seconds/86400), (unsigned)(seconds%86400/3600));
    else if (seconds >= 3600) snprintf(buf, sizeof(buf), "%uh%02um", (unsigned)(seconds/3600), (unsigned)(seconds%3600/60));
    else snprintf(buf, sizeof(buf), "%um", (unsigned)(seconds/60));
  }
  else snprintf(buf, sizeof(buf), "--");
  oled_draw_string(46, 0, buf, 1);
  usage_oled_compact(82, 4, upload); usage_oled_compact(105, 4, download);
  if (usage && (data->flags & USAGE_PARTIAL)) usage_oled_compact(77, 0, "!");
  oled_draw_rect(2, 10, 124, 4, 1);
  if (quota) oled_fill_rect(3, 11, (uint8_t)((data->remaining*122u+50u)/100u), 2, 1);
  if (usage)
  {
    const uint8_t ys[] = {17,34,51};
    uint64_t values[] = {data->upper, data->upper/2, 0};
    oled_draw_vline(22, 17, 35, 1);
    for (unsigned i = 0; i < 3; i++)
    {
      /* Preserve a .5 tick when an odd small upper bound is used. */
      if (i == 1 && data->upper == 1) strcpy(buf, "0.5");
      else if (i == 1 && data->upper == 5) strcpy(buf, "2.5");
      else count_text(buf, sizeof(buf), values[i], 0);
      usage_oled_compact(2, i == 0 ? 17 : i == 1 ? 32 : 47, buf);
      oled_draw_hline(22, ys[i], 3, 1);
      for (uint8_t x = 28; x <= 125; x = (uint8_t)(x+8)) oled_draw_pixel(x, ys[i], 1);
    }
    int16_t previous_x = -1, previous_y = 0;
    for (unsigned i = 0; i < data->count; i++)
    {
      if (data->points[i] == 255) { previous_x = -1; continue; }
      int16_t x = (int16_t)(24u+(i*101u+(data->count-1u)/2u)/(data->count-1u));
      int16_t y = (int16_t)(51-data->points[i]);
      if (previous_x >= 0) oled_draw_line(previous_x, previous_y, x, y, 1);
      else oled_draw_pixel((uint8_t)x, (uint8_t)y, 1);
      previous_x = x; previous_y = y;
      if (i+1u == data->count) oled_fill_rect((uint8_t)(x-1), (uint8_t)(y<=17?17:y>=51?50:y-1), 2, 2, 1);
    }
  }
  else oled_draw_string(34, 31, !fresh || (data->flags & USAGE_STALE) ? "DATA STALE" : "NO DATA", 1);
  if (usage) count_text(current, sizeof(current), data->current, 0);
  else strcpy(current, "--");
  snprintf(buf, sizeof(buf), "%s/%c", current, data->view ? 'd' : 'm');
  oled_draw_string(2, 56, buf, 1);
  api_text(api, sizeof(api), data, fresh);
  if (data->view)
  {
    if (usage && data->month) snprintf(buf, sizeof(buf), "%02u-%02u %s", data->month, data->day, api);
    else snprintf(buf, sizeof(buf), "-- -- %s", api);
  }
  else
  {
    if (usage) { count_text(in, sizeof(in), data->input, 0); count_text(out, sizeof(out), data->output, 0); }
    else { strcpy(in, "--"); strcpy(out, "--"); }
    snprintf(buf, sizeof(buf), "U%s D%s %s", in, out, api);
    if (strlen(buf)>21 && usage)
    {
      count_text(in, sizeof(in), data->input, 1); count_text(out, sizeof(out), data->output, 1);
      snprintf(buf, sizeof(buf), "U%s D%s %s", in, out, api);
    }
  }
  usage_oled_compact(44, 58, buf);
}
