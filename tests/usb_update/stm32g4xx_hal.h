#ifndef TEST_HAL_H
#define TEST_HAL_H
#include <stdint.h>
uint32_t HAL_GetTick(void);
void test_reset(void);
#define __disable_irq() ((void)0)
#define __DSB() ((void)0)
#define NVIC_SystemReset() test_reset()
#endif
