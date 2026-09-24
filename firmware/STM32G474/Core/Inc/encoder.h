/**
  ******************************************************************************
  * @file    encoder.h
  * @brief   EC11 编码器（TIM1 编码器模式）驱动
  ******************************************************************************
  */
#ifndef __ENCODER_H
#define __ENCODER_H

#include <stdint.h>

void encoder_init(void);
void encoder_process(uint32_t now_ms);
int8_t encoder_take_delta(void);

#endif /* __ENCODER_H */
