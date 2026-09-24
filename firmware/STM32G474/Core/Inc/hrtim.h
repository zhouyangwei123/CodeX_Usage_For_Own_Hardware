/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file    hrtim.h
  * @brief   This file contains all the function prototypes for
  *          the hrtim.c file
  ******************************************************************************
  * @attention
  *
  * Copyright (c) 2026 STMicroelectronics.
  * All rights reserved.
  *
  * This software is licensed under terms that can be found in the LICENSE file
  * in the root directory of this software component.
  * If no LICENSE file comes with this software, it is provided AS-IS.
  *
  ******************************************************************************
  */
/* USER CODE END Header */
/* Define to prevent recursive inclusion -------------------------------------*/
#ifndef __HRTIM_H__
#define __HRTIM_H__

#ifdef __cplusplus
extern "C" {
#endif

/* Includes ------------------------------------------------------------------*/
#include "main.h"

/* USER CODE BEGIN Includes */

/* USER CODE END Includes */

extern HRTIM_HandleTypeDef hhrtim1;

/* USER CODE BEGIN Private defines */

/* USER CODE END Private defines */

void MX_HRTIM1_Init(void);

void HAL_HRTIM_MspPostInit(HRTIM_HandleTypeDef *hhrtim);

HAL_StatusTypeDef hrtim_rgb_start_dma(const uint16_t *compare_values, uint16_t count);
uint8_t hrtim_rgb_dma_is_active(void);
uint8_t hrtim_rgb_dma_take_done(void);
void hrtim_rgb_pin_to_hardware(void);
void hrtim_rgb_pin_to_gpio_low(void);
void hrtim_rgb_abort_dma(void);
void hrtim_rgb_stop_dma_from_irq(void);

/* USER CODE BEGIN Prototypes */

/* USER CODE END Prototypes */

#ifdef __cplusplus
}
#endif

#endif /* __HRTIM_H__ */

