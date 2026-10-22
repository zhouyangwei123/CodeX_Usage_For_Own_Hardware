#include "firmware_image.h"
#include "app_cfg.h"
/* Length and CRC are filled by tools/package-usb-firmware.py after linking. */
__attribute__((section(".image_header"), used))
const uint32_t nmt_application_header[8] = {
  NMT_APP_MAGIC, NMT_LAYOUT, NMT_APP_BASE, 0xFFFFFFFFu, 0xFFFFFFFFu,
  (FW_VERSION_MAJOR << 16) | FW_VERSION_MINOR, NMT_TARGET, 0u
};
