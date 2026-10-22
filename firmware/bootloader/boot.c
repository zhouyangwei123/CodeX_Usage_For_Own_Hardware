#include "stm32g474xx.h"
#include "firmware_image.h"

__attribute__((section(".image_header"), used))
const uint32_t nmt_boot_header[8] = {
  NMT_BOOT_MAGIC, NMT_LAYOUT, NMT_BOOT_BASE, 0xFFFFFFFFu, 0xFFFFFFFFu,
  0x00010000u, NMT_TARGET, 0u
};

/* No C stack access is allowed after MSP changes. USB ROM requires IRQ enabled. */
__attribute__((naked, noreturn)) static void jump_vector(uint32_t address)
{
  __asm volatile (
    "ldr r1, [r0]\n"
    "ldr r2, [r0, #4]\n"
    "msr msp, r1\n"
    "movs r3, #0\n"
    "msr control, r3\n"
    "msr basepri, r3\n"
    "msr faultmask, r3\n"
    "dsb\n"
    "isb\n"
    "cpsie i\n"
    "bx r2\n"
  );
}

static int recovery_key(void)
{
  RCC->AHB2ENR |= RCC_AHB2ENR_GPIOCEN;
  (void)RCC->AHB2ENR;
  GPIOC->MODER &= ~(3u << 6); /* KEY1: PC3 active low */
  GPIOC->PUPDR = (GPIOC->PUPDR & ~(3u << 6)) | (1u << 6);
  /* Two stable samples at the reset HSI clock; no HAL/SysTick dependencies. */
  for (volatile unsigned i = 0; i < 16000u; i++) __NOP();
  int pressed = !(GPIOC->IDR & GPIO_IDR_ID3);
  for (volatile unsigned i = 0; i < 160000u; i++) __NOP();
  pressed = pressed && !(GPIOC->IDR & GPIO_IDR_ID3);
  GPIOC->PUPDR &= ~(3u << 6);
  GPIOC->MODER |= 3u << 6;
  RCC->AHB2ENR &= ~RCC_AHB2ENR_GPIOCEN;
  return pressed;
}

int main(void)
{
  __disable_irq();
  uint32_t reset = RCC->CSR;
  /* ROM DFU GO does not guarantee reset peripheral state. We cleared reset
     flags before entering ROM, so GO re-enters through a clean system reset. */
  const uint32_t resets = RCC_CSR_LPWRRSTF | RCC_CSR_WWDGRSTF |
      RCC_CSR_IWDGRSTF | RCC_CSR_SFTRSTF | RCC_CSR_BORRSTF |
      RCC_CSR_PINRSTF | RCC_CSR_OBLRSTF;
  if ((reset & resets) == 0u) NVIC_SystemReset();
  volatile uint32_t *flag = (volatile uint32_t *)NMT_REQUEST_ADDR;
  int requested = (reset & RCC_CSR_SFTRSTF) &&
                  flag[0] == NMT_REQUEST_MAGIC && flag[1] == ~NMT_REQUEST_MAGIC;
  flag[0] = flag[1] = 0;
  RCC->CSR |= RCC_CSR_RMVF;
  int recover = recovery_key() || requested;
  /* Avoid invalid Flash reads on another chip/geometry. The installer checks
     the same identity before it writes anything. */
  if ((DBGMCU->IDCODE & 0xFFFu) != 0x469u ||
      *(const uint16_t *)FLASHSIZE_BASE != 128u)
    recover = 1;
  if (!recover)
    recover = !nmt_image_valid((const uint8_t *)NMT_APP_BASE,
                              NMT_APP_CAPACITY, NMT_APP_BASE,
                              NMT_APP_CAPACITY, NMT_APP_MAGIC);

  SysTick->CTRL = 0;
  SysTick->LOAD = 0;
  SysTick->VAL = 0;
  for (unsigned i = 0; i < 8; i++) {
    NVIC->ICER[i] = 0xFFFFFFFFu;
    NVIC->ICPR[i] = 0xFFFFFFFFu;
  }
  if (recover) {
    RCC->APB2ENR |= RCC_APB2ENR_SYSCFGEN;
    (void)RCC->APB2ENR;
    SYSCFG->MEMRMP = (SYSCFG->MEMRMP & ~SYSCFG_MEMRMP_MEM_MODE) | 1u;
    SCB->VTOR = NMT_ROM_BASE;
    __DSB(); __ISB();
    jump_vector(NMT_ROM_BASE);
  }
  SCB->VTOR = NMT_APP_BASE;
  __DSB(); __ISB();
  jump_vector(NMT_APP_BASE);
}

void NMI_Handler(void)
{
  volatile uint32_t *flag = (volatile uint32_t *)NMT_REQUEST_ADDR;
  flag[0] = NMT_REQUEST_MAGIC; flag[1] = ~NMT_REQUEST_MAGIC;
  __DSB(); NVIC_SystemReset();
}
void HardFault_Handler(void) { NMI_Handler(); }
