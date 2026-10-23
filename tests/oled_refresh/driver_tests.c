/* Compile the complete production driver; fake only the HAL bus boundary. */
#include "../../firmware/STM32G474/Core/Src/oled.c"
#include <stdio.h>
#include <stdlib.h>
I2C_HandleTypeDef hi2c3;
static unsigned calls, data_bytes, busy_once, dma_error_once, blocking_error;
static void check(int ok, const char *label)
{ if (!ok) { fprintf(stderr,"FAIL %s\n",label); exit(1); } }
HAL_StatusTypeDef HAL_I2C_IsDeviceReady(I2C_HandleTypeDef *h,uint16_t a,uint32_t n,uint32_t t)
{ (void)h;(void)a;(void)n;(void)t;return HAL_OK; }
void HAL_Delay(uint32_t ms) { (void)ms; }
HAL_StatusTypeDef HAL_I2C_Mem_Write(I2C_HandleTypeDef *h,uint16_t a,uint16_t m,uint16_t s,uint8_t *p,uint16_t n,uint32_t t)
{
  (void)h;(void)a;(void)s;(void)p;(void)t;
  calls++; if(blocking_error)return HAL_ERROR;
  if(m==0x40)data_bytes+=n;
  return HAL_OK;
}
HAL_StatusTypeDef HAL_I2C_Mem_Write_DMA(I2C_HandleTypeDef *h,uint16_t a,uint16_t m,uint16_t s,uint8_t *p,uint16_t n)
{
  (void)h;(void)a;(void)s;(void)p;
  if(busy_once){busy_once=0;return HAL_BUSY;}
  if(dma_error_once){dma_error_once=0;return HAL_ERROR;}
  calls++;if(m==0x40)data_bytes+=n;return HAL_OK;
}
static void drain(void)
{
  unsigned limit=100;
  while(oled_dma_active && limit--)
  {
    if(oled_dma_inflight)HAL_I2C_MemTxCpltCallback(&hi2c3);
    oled_process();
  }
  check(!oled_dma_active,"DMA completes within bounded transactions");
}
int main(void)
{
  for(uint8_t driver=1;driver<=2;driver++)
  {
    oled_set_config(0,driver);calls=data_bytes=0;
    for(unsigned i=0;i<100;i++)oled_flush_if_changed();
    check(calls==0,"unchanged initial frame has no I2C writes");
    oled_draw_pixel(10,10,1);oled_flush_if_changed();drain();
    check(data_bytes==1024,"changed frame sends exactly one framebuffer");
    unsigned prior=calls;
    for(unsigned i=0;i<100;i++)oled_flush_if_changed();
    check(calls==prior,"100 unchanged refreshes suppressed");

    oled_draw_pixel(11,10,1);busy_once=1;oled_flush_if_changed();
    oled_draw_pixel(12,10,1);oled_flush_if_changed();
    oled_draw_pixel(12,10,0);oled_flush_if_changed();drain();
    check(data_bytes==2048,"reverted pending frame does not trigger duplicate DMA");

    oled_draw_pixel(13,10,1);dma_error_once=1;oled_flush_if_changed();
    check(oled_dma_failed&&oled_tx_fb_valid&&oled_ok,"successful blocking fallback records delivered frame");
    prior=calls;oled_flush_if_changed();check(calls==prior,"blocking fallback also suppresses identical frames");
    oled_draw_pixel(14,10,1);blocking_error=1;oled_flush_if_changed();
    check(!oled_ok&&!oled_tx_fb_valid,"failed write never marks frame delivered");
    blocking_error=0;oled_init();oled_draw_pixel(14,10,1);data_bytes=0;
    oled_flush_if_changed();drain();check(data_bytes==1024,"reinitialization redraws even if old snapshot matched");
    oled_draw_pixel(15,10,1);oled_flush_if_changed();HAL_I2C_ErrorCallback(&hi2c3);
    check(!oled_ok&&!oled_tx_fb_valid,"asynchronous error invalidates comparison snapshot");
  }
  puts("OLED driver: PASS (SSD1306/SH1106, unchanged frames, DMA pending/busy, blocking fallback, error/reinit)");
  return 0;
}
