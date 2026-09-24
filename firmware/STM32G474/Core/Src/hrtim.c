/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file    hrtim.c
  * @brief   This file provides code for the configuration
  *          of the HRTIM instances.
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
/* Includes ------------------------------------------------------------------*/
#include "hrtim.h"

/* USER CODE BEGIN 0 */

static DMA_HandleTypeDef hdma_hrtim1_timer_a;
static volatile uint8_t hrtim_rgb_dma_active;
static volatile uint8_t hrtim_rgb_dma_done;

/* USER CODE END 0 */

HRTIM_HandleTypeDef hhrtim1;

/* HRTIM1 init function */
void MX_HRTIM1_Init(void)
{

  /* USER CODE BEGIN HRTIM1_Init 0 */

  /* USER CODE END HRTIM1_Init 0 */

  HRTIM_TimeBaseCfgTypeDef pTimeBaseCfg = {0};
  HRTIM_TimerCtlTypeDef pTimerCtl = {0};
  HRTIM_TimerCfgTypeDef pTimerCfg = {0};
  HRTIM_OutputCfgTypeDef pOutputCfg = {0};

  /* USER CODE BEGIN HRTIM1_Init 1 */

  /* USER CODE END HRTIM1_Init 1 */
  hhrtim1.Instance = HRTIM1;
  hhrtim1.Init.HRTIMInterruptResquests = HRTIM_IT_NONE;
  hhrtim1.Init.SyncOptions = HRTIM_SYNCOPTION_NONE;
  if (HAL_HRTIM_Init(&hhrtim1) != HAL_OK)
  {
    Error_Handler();
  }
  if (HAL_HRTIM_DLLCalibrationStart(&hhrtim1, HRTIM_CALIBRATIONRATE_3) != HAL_OK)
  {
    Error_Handler();
  }
  if (HAL_HRTIM_PollForDLLCalibration(&hhrtim1, 10) != HAL_OK)
  {
    Error_Handler();
  }
  /* 170 MHz / (212 + 1) ≈ 1.25 us per WS2812 bit. */
  pTimeBaseCfg.Period = 212u;
  pTimeBaseCfg.RepetitionCounter = 0x00;
  pTimeBaseCfg.PrescalerRatio = HRTIM_PRESCALERRATIO_DIV1;
  pTimeBaseCfg.Mode = HRTIM_MODE_CONTINUOUS;
  if (HAL_HRTIM_TimeBaseConfig(&hhrtim1, HRTIM_TIMERINDEX_TIMER_A, &pTimeBaseCfg) != HAL_OK)
  {
    Error_Handler();
  }
  pTimerCtl.UpDownMode = HRTIM_TIMERUPDOWNMODE_UP;
  pTimerCtl.DualChannelDacEnable = HRTIM_TIMER_DCDE_DISABLED;
  if (HAL_HRTIM_WaveformTimerControl(&hhrtim1, HRTIM_TIMERINDEX_TIMER_A, &pTimerCtl) != HAL_OK)
  {
    Error_Handler();
  }
  pTimerCfg.InterruptRequests = HRTIM_TIM_IT_NONE;
  pTimerCfg.DMARequests = HRTIM_TIM_DMA_REP;
  pTimerCfg.DMASrcAddress = 0x0000;
  pTimerCfg.DMADstAddress = 0x0000;
  pTimerCfg.DMASize = 0x1;
  pTimerCfg.HalfModeEnable = HRTIM_HALFMODE_DISABLED;
  pTimerCfg.InterleavedMode = HRTIM_INTERLEAVED_MODE_DISABLED;
  pTimerCfg.StartOnSync = HRTIM_SYNCSTART_DISABLED;
  pTimerCfg.ResetOnSync = HRTIM_SYNCRESET_DISABLED;
  pTimerCfg.DACSynchro = HRTIM_DACSYNC_NONE;
  /*
   * WS2812 duty values are written into the CMP1 preload register by one
   * Timer-A repetition DMA request per PWM period. Updating CMP1 directly
   * from its own compare event can consume two values in one period when the
   * new compare is larger than the current one, corrupting the serial stream.
   */
  pTimerCfg.PreloadEnable = HRTIM_PRELOAD_ENABLED;
  pTimerCfg.UpdateGating = HRTIM_UPDATEGATING_INDEPENDENT;
  pTimerCfg.BurstMode = HRTIM_TIMERBURSTMODE_MAINTAINCLOCK;
  pTimerCfg.RepetitionUpdate = HRTIM_UPDATEONREPETITION_ENABLED;
  pTimerCfg.PushPull = HRTIM_TIMPUSHPULLMODE_DISABLED;
  pTimerCfg.FaultEnable = HRTIM_TIMFAULTENABLE_NONE;
  pTimerCfg.FaultLock = HRTIM_TIMFAULTLOCK_READWRITE;
  pTimerCfg.DeadTimeInsertion = HRTIM_TIMDEADTIMEINSERTION_DISABLED;
  pTimerCfg.DelayedProtectionMode = HRTIM_TIMER_A_B_C_DELAYEDPROTECTION_DISABLED;
  pTimerCfg.UpdateTrigger = HRTIM_TIMUPDATETRIGGER_NONE;
  pTimerCfg.ResetTrigger = HRTIM_TIMRESETTRIGGER_NONE;
  pTimerCfg.ResetUpdate = HRTIM_TIMUPDATEONRESET_DISABLED;
  pTimerCfg.ReSyncUpdate = HRTIM_TIMERESYNC_UPDATE_UNCONDITIONAL;
  if (HAL_HRTIM_WaveformTimerConfig(&hhrtim1, HRTIM_TIMERINDEX_TIMER_A, &pTimerCfg) != HAL_OK)
  {
    Error_Handler();
  }
  pOutputCfg.Polarity = HRTIM_OUTPUTPOLARITY_HIGH;
  pOutputCfg.SetSource = HRTIM_OUTPUTSET_TIMPER;
  pOutputCfg.ResetSource = HRTIM_OUTPUTRESET_TIMCMP1;
  pOutputCfg.IdleMode = HRTIM_OUTPUTIDLEMODE_NONE;
  pOutputCfg.IdleLevel = HRTIM_OUTPUTIDLELEVEL_INACTIVE;
  pOutputCfg.FaultLevel = HRTIM_OUTPUTFAULTLEVEL_NONE;
  pOutputCfg.ChopperModeEnable = HRTIM_OUTPUTCHOPPERMODE_DISABLED;
  pOutputCfg.BurstModeEntryDelayed = HRTIM_OUTPUTBURSTMODEENTRY_REGULAR;
  if (HAL_HRTIM_WaveformOutputConfig(&hhrtim1, HRTIM_TIMERINDEX_TIMER_A, HRTIM_OUTPUT_TA1, &pOutputCfg) != HAL_OK)
  {
    Error_Handler();
  }
  /* USER CODE BEGIN HRTIM1_Init 2 */

  /* USER CODE END HRTIM1_Init 2 */
  HAL_HRTIM_MspPostInit(&hhrtim1);

}

void HAL_HRTIM_MspInit(HRTIM_HandleTypeDef* hrtimHandle)
{

  if(hrtimHandle->Instance==HRTIM1)
  {
  /* USER CODE BEGIN HRTIM1_MspInit 0 */

  /* USER CODE END HRTIM1_MspInit 0 */
    /* HRTIM1 clock enable */
    __HAL_RCC_HRTIM1_CLK_ENABLE();

    /* Timer A repetition DMA updates one preloaded CMP1 value per WS2812 bit. */
    __HAL_RCC_DMAMUX1_CLK_ENABLE();
    __HAL_RCC_DMA1_CLK_ENABLE();
    hdma_hrtim1_timer_a.Instance = DMA1_Channel2;
    hdma_hrtim1_timer_a.Init.Request = DMA_REQUEST_HRTIM1_A;
    hdma_hrtim1_timer_a.Init.Direction = DMA_MEMORY_TO_PERIPH;
    hdma_hrtim1_timer_a.Init.PeriphInc = DMA_PINC_DISABLE;
    hdma_hrtim1_timer_a.Init.MemInc = DMA_MINC_ENABLE;
    hdma_hrtim1_timer_a.Init.PeriphDataAlignment = DMA_PDATAALIGN_HALFWORD;
    hdma_hrtim1_timer_a.Init.MemDataAlignment = DMA_MDATAALIGN_HALFWORD;
    hdma_hrtim1_timer_a.Init.Mode = DMA_NORMAL;
    hdma_hrtim1_timer_a.Init.Priority = DMA_PRIORITY_VERY_HIGH;
    if (HAL_DMA_Init(&hdma_hrtim1_timer_a) == HAL_OK)
    {
      __HAL_LINKDMA(hrtimHandle, hdmaTimerA, hdma_hrtim1_timer_a);

      HAL_NVIC_SetPriority(DMA1_Channel2_IRQn, 1, 1);
      HAL_NVIC_EnableIRQ(DMA1_Channel2_IRQn);
    }
  /* USER CODE BEGIN HRTIM1_MspInit 1 */

  /* USER CODE END HRTIM1_MspInit 1 */
  }
}

void HAL_HRTIM_MspPostInit(HRTIM_HandleTypeDef* hrtimHandle)
{

  GPIO_InitTypeDef GPIO_InitStruct = {0};
  if(hrtimHandle->Instance==HRTIM1)
  {
  /* USER CODE BEGIN HRTIM1_MspPostInit 0 */

  /* USER CODE END HRTIM1_MspPostInit 0 */

    __HAL_RCC_GPIOA_CLK_ENABLE();
    /**HRTIM1 GPIO Configuration
    PA8     ------> HRTIM1_CHA1
    */
    GPIO_InitStruct.Pin = GPIO_PIN_8;
    GPIO_InitStruct.Mode = GPIO_MODE_AF_PP;
    GPIO_InitStruct.Pull = GPIO_NOPULL;
    GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_VERY_HIGH;
    GPIO_InitStruct.Alternate = GPIO_AF13_HRTIM1;
    HAL_GPIO_Init(GPIOA, &GPIO_InitStruct);

  /* USER CODE BEGIN HRTIM1_MspPostInit 1 */

  /* USER CODE END HRTIM1_MspPostInit 1 */
  }

}

void HAL_HRTIM_MspDeInit(HRTIM_HandleTypeDef* hrtimHandle)
{

  if(hrtimHandle->Instance==HRTIM1)
  {
  /* USER CODE BEGIN HRTIM1_MspDeInit 0 */

  /* USER CODE END HRTIM1_MspDeInit 0 */
    /* Peripheral clock disable */
    __HAL_RCC_HRTIM1_CLK_DISABLE();
    HAL_DMA_DeInit(hrtimHandle->hdmaTimerA);
    HAL_NVIC_DisableIRQ(DMA1_Channel2_IRQn);
  /* USER CODE BEGIN HRTIM1_MspDeInit 1 */

  /* USER CODE END HRTIM1_MspDeInit 1 */
  }
}

/* USER CODE BEGIN 1 */

HAL_StatusTypeDef hrtim_rgb_start_dma(const uint16_t *compare_values, uint16_t count)
{
  if (compare_values == NULL || count < 2u || hhrtim1.hdmaTimerA == NULL)
  {
    return HAL_ERROR;
  }
  if (hrtim_rgb_dma_active)
  {
    return HAL_BUSY;
  }

  hrtim_rgb_dma_done = 0u;
  hrtim_rgb_pin_to_gpio_low();
  __HAL_HRTIM_TIMER_DISABLE_DMA(&hhrtim1, HRTIM_TIMERINDEX_TIMER_A,
                                HRTIM_TIM_DMA_CMP1 | HRTIM_TIM_DMA_REP);
  hhrtim1.Instance->sCommonRegs.ODISR = HRTIM_OUTPUT_TA1;
  __HAL_HRTIM_DISABLE(&hhrtim1, HRTIM_TIMERID_TIMER_A);
  __HAL_HRTIM_SETCOUNTER(&hhrtim1, HRTIM_TIMERINDEX_TIMER_A, 0u);
  __HAL_HRTIM_TIMER_CLEAR_FLAG(&hhrtim1, HRTIM_TIMERINDEX_TIMER_A,
                               HRTIM_TIM_FLAG_CMP1 | HRTIM_TIM_FLAG_REP |
                               HRTIM_TIM_FLAG_UPD);

  /* Prime bit 0 into the active register before the first period starts. */
  __HAL_HRTIM_SETCOMPARE(&hhrtim1, HRTIM_TIMERINDEX_TIMER_A,
                         HRTIM_COMPAREUNIT_1, compare_values[0]);
  if (HAL_HRTIM_SoftwareUpdate(&hhrtim1, HRTIM_TIMERID_TIMER_A) != HAL_OK)
  {
    hrtim_rgb_abort_dma();
    return HAL_ERROR;
  }

  hhrtim1.TimerParam[HRTIM_TIMERINDEX_TIMER_A].DMARequests = HRTIM_TIM_DMA_REP;
  hhrtim1.TimerParam[HRTIM_TIMERINDEX_TIMER_A].DMASrcAddress =
      (uint32_t)(uintptr_t)&compare_values[1];
  hhrtim1.TimerParam[HRTIM_TIMERINDEX_TIMER_A].DMADstAddress =
      (uint32_t)(uintptr_t)&hhrtim1.Instance->sTimerxRegs[HRTIM_TIMERINDEX_TIMER_A].CMP1xR;
  hhrtim1.TimerParam[HRTIM_TIMERINDEX_TIMER_A].DMASize = (uint32_t)(count - 1u);

  hrtim_rgb_pin_to_hardware();
  if (HAL_HRTIM_WaveformOutputStart(&hhrtim1, HRTIM_OUTPUT_TA1) != HAL_OK)
  {
    hrtim_rgb_abort_dma();
    return HAL_ERROR;
  }

  hrtim_rgb_dma_active = 1u;
  HAL_StatusTypeDef status = HAL_HRTIM_WaveformCountStart_DMA(
      &hhrtim1, HRTIM_TIMERID_TIMER_A);
  if (status == HAL_OK)
  {
    return HAL_OK;
  }

  /* Start 失败时也把可能已经打开的输出和 DMA 请求收干净，交给 GPIO 回退。 */
  hrtim_rgb_abort_dma();
  return status;
}

uint8_t hrtim_rgb_dma_is_active(void)
{
  return hrtim_rgb_dma_active;
}

uint8_t hrtim_rgb_dma_take_done(void)
{
  uint8_t done = hrtim_rgb_dma_done;
  hrtim_rgb_dma_done = 0;
  return done;
}

void hrtim_rgb_pin_to_hardware(void)
{
  __HAL_RCC_GPIOA_CLK_ENABLE();
  GPIOA->BRR = GPIO_PIN_8;
  GPIOA->MODER = (GPIOA->MODER & ~(3u << 16)) | (2u << 16);
  GPIOA->OTYPER &= ~GPIO_PIN_8;
  GPIOA->PUPDR &= ~(3u << 16);
  GPIOA->OSPEEDR = (GPIOA->OSPEEDR & ~(3u << 16)) | (3u << 16);
  GPIOA->AFR[1] = (GPIOA->AFR[1] & ~0x0Fu) | 13u;
}

void hrtim_rgb_pin_to_gpio_low(void)
{
  __HAL_RCC_GPIOA_CLK_ENABLE();
  GPIOA->AFR[1] &= ~0x0Fu;
  GPIOA->MODER = (GPIOA->MODER & ~(3u << 16)) | (1u << 16);
  GPIOA->OTYPER &= ~GPIO_PIN_8;
  GPIOA->PUPDR &= ~(3u << 16);
  GPIOA->OSPEEDR = (GPIOA->OSPEEDR & ~(3u << 16)) | (3u << 16);
  GPIOA->BSRR = (uint32_t)GPIO_PIN_8 << 16;
}

/* Called from the HRTIM DMA completion callback or the main-loop watchdog. */
void hrtim_rgb_abort_dma(void)
{
  __HAL_HRTIM_TIMER_DISABLE_DMA(&hhrtim1, HRTIM_TIMERINDEX_TIMER_A,
                                HRTIM_TIM_DMA_CMP1 | HRTIM_TIM_DMA_REP);
  hhrtim1.Instance->sCommonRegs.ODISR = HRTIM_OUTPUT_TA1;
  __HAL_HRTIM_DISABLE(&hhrtim1, HRTIM_TIMERID_TIMER_A);
  hrtim_rgb_pin_to_gpio_low();
  hrtim_rgb_dma_active = 0u;
  hrtim_rgb_dma_done = 1u;
}

/* Keep the legacy callback name as the IRQ entry point. */
void hrtim_rgb_stop_dma_from_irq(void)
{
  hrtim_rgb_abort_dma();
}

void HAL_HRTIM_RepetitionEventCallback(HRTIM_HandleTypeDef *hhrtim, uint32_t TimerIdx)
{
  if (hhrtim == &hhrtim1 && TimerIdx == HRTIM_TIMERINDEX_TIMER_A)
  {
    hrtim_rgb_stop_dma_from_irq();
  }
}

/* USER CODE END 1 */
