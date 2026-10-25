#ifndef OLED_REFRESH_TEST_HAL_H
#define OLED_REFRESH_TEST_HAL_H
#include <stdint.h>
typedef struct { int unused; } I2C_HandleTypeDef;
typedef enum { HAL_OK, HAL_ERROR, HAL_BUSY } HAL_StatusTypeDef;
#define HAL_I2C_STATE_READY 0u
#define I2C_MEMADD_SIZE_8BIT 1u
HAL_StatusTypeDef HAL_I2C_Mem_Write(I2C_HandleTypeDef *, uint16_t, uint16_t, uint16_t, uint8_t *, uint16_t, uint32_t);
HAL_StatusTypeDef HAL_I2C_Mem_Write_DMA(I2C_HandleTypeDef *, uint16_t, uint16_t, uint16_t, uint8_t *, uint16_t);
HAL_StatusTypeDef HAL_I2C_IsDeviceReady(I2C_HandleTypeDef *, uint16_t, uint32_t, uint32_t);
void HAL_Delay(uint32_t);
uint32_t HAL_GetTick(void);
uint32_t HAL_I2C_GetState(I2C_HandleTypeDef *);
#endif
