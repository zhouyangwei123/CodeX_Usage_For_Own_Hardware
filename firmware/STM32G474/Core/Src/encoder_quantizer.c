/**
  ******************************************************************************
  * @file    encoder_quantizer.c
  * @brief   编码器计数归一化与去抖逻辑
  ******************************************************************************
  */

#include "encoder_quantizer.h"

static void emit_delta(encoder_quantizer_t *quantizer,
                       int8_t direction,
                       uint32_t now_ms,
                       uint32_t guard_ms)
{
  if (quantizer->has_emitted &&
      (uint32_t)(now_ms - quantizer->last_emit_ms) < guard_ms)
  {
    return;
  }

  if (direction > 0 && quantizer->pending_delta < INT8_MAX)
  {
    quantizer->pending_delta++;
  }
  else if (direction < 0 && quantizer->pending_delta > INT8_MIN)
  {
    quantizer->pending_delta--;
  }

  quantizer->last_emit_ms = now_ms;
  quantizer->has_emitted = 1;
}

void encoder_quantizer_init(encoder_quantizer_t *quantizer)
{
  if (quantizer == 0) return;

  quantizer->acc = 0;
  quantizer->pending_delta = 0;
  quantizer->last_emit_ms = 0;
  quantizer->has_emitted = 0;
}

void encoder_quantizer_feed(encoder_quantizer_t *quantizer,
                            int16_t diff,
                            uint32_t now_ms,
                            int16_t counts_per_detent,
                            uint32_t guard_ms)
{
  if (quantizer == 0 || counts_per_detent <= 0 || diff == 0) return;

  quantizer->acc += diff;
  while (quantizer->acc >= counts_per_detent)
  {
    quantizer->acc -= counts_per_detent;
    emit_delta(quantizer, 1, now_ms, guard_ms);
  }
  while (quantizer->acc <= -counts_per_detent)
  {
    quantizer->acc += counts_per_detent;
    emit_delta(quantizer, -1, now_ms, guard_ms);
  }
}

int8_t encoder_quantizer_take_delta(encoder_quantizer_t *quantizer)
{
  int8_t delta;

  if (quantizer == 0) return 0;
  delta = quantizer->pending_delta;
  quantizer->pending_delta = 0;
  return delta;
}
