/**
  ******************************************************************************
  * @file    encoder_quantizer.h
  * @brief   无硬件依赖的编码器计数归一化与去抖逻辑
  ******************************************************************************
  */

#ifndef CODEX_ENCODER_QUANTIZER_H
#define CODEX_ENCODER_QUANTIZER_H

#include <stdint.h>

typedef struct
{
  int32_t acc;
  int8_t pending_delta;
  uint32_t last_emit_ms;
  uint8_t has_emitted;
} encoder_quantizer_t;

void encoder_quantizer_init(encoder_quantizer_t *quantizer);
void encoder_quantizer_feed(encoder_quantizer_t *quantizer,
                            int16_t diff,
                            uint32_t now_ms,
                            int16_t counts_per_detent,
                            uint32_t guard_ms);
int8_t encoder_quantizer_take_delta(encoder_quantizer_t *quantizer);

#endif /* CODEX_ENCODER_QUANTIZER_H */
