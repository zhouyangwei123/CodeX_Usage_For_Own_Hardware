#ifndef FIRMWARE_IMAGE_H
#define FIRMWARE_IMAGE_H
#include <stdint.h>
#include <stddef.h>
#define NMT_BOOT_BASE       0x08000000u
#define NMT_APP_BASE        0x08002000u
/* Fits bank 1 on 128-KiB parts in either DBANK mode; never crosses the bank gap. */
#define NMT_APP_LIMIT       0x08010000u
#define NMT_APP_CAPACITY    (NMT_APP_LIMIT - NMT_APP_BASE)
#define NMT_HEADER_OFFSET  0x200u
#define NMT_CODE_OFFSET    0x400u
#define NMT_APP_MAGIC      0x41544D4Eu
#define NMT_BOOT_MAGIC     0x42544D4Eu
#define NMT_LAYOUT         1u
#define NMT_TARGET         0x04690080u /* DBGMCU ID 0x469, 128-KiB Flash */
#define NMT_REQUEST_ADDR   0x2001FFF8u
#define NMT_REQUEST_MAGIC  0x5546444Eu
#define NMT_ROM_BASE       0x1FFF0000u
uint32_t nmt_read32(const uint8_t *p);
uint32_t nmt_image_crc(const uint8_t *image, uint32_t length);
int nmt_image_valid(const uint8_t *image, size_t available, uint32_t base,
                    uint32_t capacity, uint32_t magic);
#endif
