#include "firmware_image.h"

uint32_t nmt_read32(const uint8_t *p)
{
  return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
         ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
uint32_t nmt_image_crc(const uint8_t *image, uint32_t length)
{
  uint32_t crc = 0xFFFFFFFFu;
  for (uint32_t i = 0; i < length; i++)
  {
    uint8_t b = (i >= NMT_HEADER_OFFSET + 16u && i < NMT_HEADER_OFFSET + 20u)
                  ? 0u : image[i];
    crc ^= b;
    for (unsigned bit = 0; bit < 8; bit++)
      crc = (crc >> 1) ^ ((crc & 1u) ? 0xEDB88320u : 0u);
  }
  return ~crc;
}
int nmt_image_valid(const uint8_t *image, size_t available, uint32_t base,
                    uint32_t capacity, uint32_t magic)
{
  if (image == NULL || available < NMT_CODE_OFFSET) return 0;
  const uint8_t *h = image + NMT_HEADER_OFFSET;
  uint32_t length = nmt_read32(h + 12);
  if (nmt_read32(h) != magic || nmt_read32(h + 4) != NMT_LAYOUT ||
      nmt_read32(h + 8) != base || nmt_read32(h + 24) != NMT_TARGET ||
      nmt_read32(h + 28) != 0u || length < NMT_CODE_OFFSET ||
      length > capacity || length > available || (length & 7u)) return 0;
  uint32_t stack = nmt_read32(image);
  uint32_t entry = nmt_read32(image + 4);
  if (stack <= 0x20000000u || stack > NMT_REQUEST_ADDR || (stack & 7u) ||
      !(entry & 1u) || entry < base + NMT_CODE_OFFSET || entry >= base + length)
    return 0;
  return nmt_read32(h + 16) == nmt_image_crc(image, length);
}
