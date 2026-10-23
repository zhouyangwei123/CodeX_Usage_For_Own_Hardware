#ifndef USAGE_OLED_H
#define USAGE_OLED_H
#include <stdint.h>

#define USAGE_HEADER_SIZE 52u
#define USAGE_MAX_POINTS 76u
#define USAGE_QUOTA_KNOWN 1u
#define USAGE_QUOTA_STALE 2u
#define USAGE_KNOWN 4u
#define USAGE_PARTIAL 8u
#define USAGE_STALE 16u
#define USAGE_API_KNOWN 32u
#define USAGE_API_STALE 64u
#define USAGE_RESET_KNOWN 128u

typedef struct
{
  uint8_t view, flags, remaining, count, month, day;
  uint32_t reset_seconds, received_ms;
  uint64_t upper, current, input, output, balance_cents;
  uint8_t points[USAGE_MAX_POINTS];
} usage_oled_t;

uint8_t usage_oled_parse(usage_oled_t *out, const uint8_t *p, uint8_t len);
void usage_oled_compact(uint8_t x, uint8_t y, const char *text);
void usage_oled_render(const usage_oled_t *data, uint8_t fresh,
                       uint32_t age_seconds, const char *upload, const char *download);
#endif
