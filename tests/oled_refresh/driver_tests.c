/* Compile the complete production driver; fake only the HAL bus boundary. */
#include "../../firmware/STM32G474/Core/Src/oled.c"
#include <stdio.h>
#include <stdlib.h>
I2C_HandleTypeDef hi2c3;
static unsigned calls, data_bytes, busy_once, dma_error_once, blocking_error;
static unsigned clock_writes, clock_mask, clock_busy_once, bus_busy;
static uint32_t test_tick;
static void check(int ok, const char *label)
{ if (!ok) { fprintf(stderr,"FAIL %s\n",label); exit(1); } }
HAL_StatusTypeDef HAL_I2C_IsDeviceReady(I2C_HandleTypeDef *h,uint16_t a,uint32_t n,uint32_t t)
{ (void)h;(void)a;(void)n;(void)t;return HAL_OK; }
void HAL_Delay(uint32_t ms) { (void)ms; }
uint32_t HAL_GetTick(void) { return test_tick; }
uint32_t HAL_I2C_GetState(I2C_HandleTypeDef *h) { (void)h; return bus_busy ? 1u : HAL_I2C_STATE_READY; }
HAL_StatusTypeDef HAL_I2C_Mem_Write(I2C_HandleTypeDef *h,uint16_t a,uint16_t m,uint16_t s,uint8_t *p,uint16_t n,uint32_t t)
{
  (void)h;(void)a;(void)s;(void)p;(void)t;
  calls++; if(blocking_error)return HAL_ERROR;
  if(m==0x00&&n==2&&p[0]==0xD5)
  {
    if(clock_busy_once){clock_busy_once=0;return HAL_BUSY;}
    check(p[1]==0x80||p[1]==0x90||p[1]==0xA0||p[1]==0xB0||p[1]==0xC0,"clock spread stays within the five accepted values");
    clock_writes++;clock_mask|=1u<<((p[1]>>4)-8u);
  }
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
  oled_set_config(0,1);calls=data_bytes=clock_writes=clock_mask=0;
  for(uint32_t now=5;now<=10000;now+=5)oled_noise_diag_process(now);
  check(clock_writes==666&&clock_mask==31&&data_bytes==0,"strong clock spread runs independently of unchanged 1Hz framebuffer");
  oled_dma_active=1;oled_noise_diag_process(10020);
  oled_dma_active=0;oled_dma_inflight=1;oled_noise_diag_process(10025);
  oled_dma_inflight=0;bus_busy=1;oled_noise_diag_process(10030);
  bus_busy=0;check(clock_writes==666,"clock commands never interleave DMA or an occupied bus");
  clock_busy_once=1;oled_noise_diag_process(10035);
  check(clock_writes==666&&oled_ok,"HAL_BUSY does not lose the pending clock update");
  oled_noise_diag_process(10035);oled_noise_diag_process(10045);
  check(clock_writes==667,"clock update respects the 11ms minimum after busy recovery");
  oled_noise_diag_process(10046);check(clock_writes==668,"clock update resumes at its due time");
  oled_set_config(0,2);oled_noise_diag_process(20000);
  check(clock_writes==668,"unverified SH1106 clock profile is not applied");
  test_tick=UINT32_MAX-10u;oled_set_config(0,0);
  oled_noise_diag_process(UINT32_MAX);check(clock_writes==668,"clock interval remains bounded across tick wrap");
  oled_noise_diag_process(0);check(clock_writes==669,"clock update resumes after tick wrap");
  blocking_error=1;oled_noise_diag_process(11);
  check(!oled_ok&&!oled_tx_fb_valid,"clock write failure triggers existing display reinitialization path");
  blocking_error=0;test_tick=100;oled_init();oled_noise_diag_process(111);
  check(clock_writes==670&&oled_ok,"clock updates restart after OLED reinitialization");
  puts("OLED driver: PASS (SSD1306/SH1106, unchanged frames, DMA pending/busy, blocking fallback, error/reinit)");
  puts("OLED strong clock spread: PASS (five values, cadence, idle bus, busy/error recovery, tick wrap, controller scope)");
  return 0;
}
