/**
  ******************************************************************************
  * @file    encoder.c
  * @brief   EC11 编码器：读取 TIM1 计数并输出按格(detent)的增量
  ******************************************************************************
  */

#include "encoder.h"
#include "app_cfg.h"
#include "encoder_quantizer.h"
#include "tim.h"

static int16_t last_cnt;
static encoder_quantizer_t quantizer;

void encoder_init(void)
{
  HAL_TIM_Encoder_Start(&htim1, TIM_CHANNEL_ALL);
  __HAL_TIM_SET_COUNTER(&htim1, 0);
  last_cnt = 0;
  encoder_quantizer_init(&quantizer);
}

void encoder_process(uint32_t now_ms)
{
  uint16_t cnt = __HAL_TIM_GET_COUNTER(&htim1);
  int16_t diff = (int16_t)((uint16_t)cnt - (uint16_t)last_cnt);
  last_cnt = (int16_t)cnt;
  if (diff == 0) return;

  encoder_quantizer_feed(&quantizer,
                         diff,
                         now_ms,
                         ENCODER_COUNTS_PER_DETENT,
                         ENCODER_EMIT_GUARD_MS);
}

int8_t encoder_take_delta(void)
{
  return encoder_quantizer_take_delta(&quantizer);
}
