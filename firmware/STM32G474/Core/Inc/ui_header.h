#ifndef UI_HEADER_H
#define UI_HEADER_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>

#define UI_HEADER_CODEX_TEXT_X       3u
#define UI_HEADER_STATUS_X          39u
#define UI_HEADER_STATUS_END_X      68u
#define UI_HEADER_SPINNER_CENTER_X  72u
#define UI_HEADER_SPINNER_RADIUS     3u
#define UI_HEADER_SPINNER_RIGHT_X   75u
#define UI_HEADER_UPLOAD_X          82u
#define UI_HEADER_DOWNLOAD_X       105u
#define UI_HEADER_COMPACT_ADVANCE    4u
#define UI_HEADER_COMPACT_WIDTH      3u

typedef struct
{
  uint8_t upload_x;
  uint8_t upload_right_x;
  uint8_t download_x;
  uint8_t download_right_x;
} ui_header_layout_t;

static inline uint8_t ui_header_compact_right_x(uint8_t x,
                                                 uint8_t character_count)
{
  if (character_count < 1u) character_count = 1u;
  if (character_count > 5u) character_count = 5u;
  return (uint8_t)(x + (character_count - 1u) * UI_HEADER_COMPACT_ADVANCE
                   + UI_HEADER_COMPACT_WIDTH - 1u);
}

static inline ui_header_layout_t ui_header_layout(uint8_t upload_characters,
                                                   uint8_t download_characters)
{
  ui_header_layout_t layout;
  layout.upload_x = UI_HEADER_UPLOAD_X;
  layout.upload_right_x = ui_header_compact_right_x(
      UI_HEADER_UPLOAD_X, upload_characters);
  layout.download_x = UI_HEADER_DOWNLOAD_X;
  layout.download_right_x = ui_header_compact_right_x(
      UI_HEADER_DOWNLOAD_X, download_characters);
  return layout;
}

static inline uint8_t ui_header_format_speed(char *out, size_t capacity,
                                             char prefix,
                                             uint32_t kib_per_second,
                                             uint8_t valid)
{
  int written;

  if (out == NULL || capacity == 0u) return 0u;

  if (!valid)
  {
    written = snprintf(out, capacity, "%c--", prefix);
  }
  else if (kib_per_second < 1000u)
  {
    written = snprintf(out, capacity, "%c%luK", prefix,
                       (unsigned long)kib_per_second);
  }
  else if (kib_per_second < 10u * 1024u)
  {
    uint32_t tenths = (uint32_t)(((uint64_t)kib_per_second * 10u + 512u)
                                 / 1024u);
    if (tenths > 99u) tenths = 99u;
    written = snprintf(out, capacity, "%c%lu.%luM", prefix,
                       (unsigned long)(tenths / 10u),
                       (unsigned long)(tenths % 10u));
  }
  else if (kib_per_second < 1000u * 1024u)
  {
    uint32_t mib = (kib_per_second + 512u) / 1024u;
    if (mib > 999u) mib = 999u;
    written = snprintf(out, capacity, "%c%luM", prefix,
                       (unsigned long)mib);
  }
  else if (kib_per_second < 10u * 1024u * 1024u)
  {
    uint32_t tenths = (uint32_t)(((uint64_t)kib_per_second * 10u
                                  + 512u * 1024u)
                                 / (1024u * 1024u));
    if (tenths > 99u) tenths = 99u;
    written = snprintf(out, capacity, "%c%lu.%luG", prefix,
                       (unsigned long)(tenths / 10u),
                       (unsigned long)(tenths % 10u));
  }
  else
  {
    uint32_t gib = (uint32_t)(((uint64_t)kib_per_second
                               + 512u * 1024u)
                              / (1024u * 1024u));
    if (gib > 999u) gib = 999u;
    written = snprintf(out, capacity, "%c%luG", prefix,
                       (unsigned long)gib);
  }

  if (written < 0)
  {
    out[0] = '\0';
    return 0u;
  }
  if ((size_t)written >= capacity) return (uint8_t)(capacity - 1u);
  return (uint8_t)written;
}

#endif /* UI_HEADER_H */
