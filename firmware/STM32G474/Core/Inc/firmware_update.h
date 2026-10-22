#ifndef FIRMWARE_UPDATE_H
#define FIRMWARE_UPDATE_H
#include <stdint.h>
void firmware_update_request(const uint8_t *payload, uint8_t length);
void firmware_update_poll(void);
#endif
