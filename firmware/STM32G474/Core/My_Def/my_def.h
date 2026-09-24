/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file           : main.h
  * @brief          : Header for main.c file.
  *                   This file contains the common defines of the application.
  ******************************************************************************
  * @attention
  *
  * Copyright (c) 2022 STMicroelectronics.
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
#ifndef __MY_DEF_H
#define __MY_DEF_H


/* Includes ------------------------------------------------------------------*/
#include "stdint.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */

/* USER CODE END Includes */

/* Exported types ------------------------------------------------------------*/
/* USER CODE BEGIN ET */
//����GPIO�ṹ��λ��
typedef struct
{
   volatile unsigned short bit0 : 1;
   volatile unsigned short bit1 : 1;
   volatile unsigned short bit2 : 1;
   volatile unsigned short bit3 : 1;
   volatile unsigned short bit4 : 1;
   volatile unsigned short bit5 : 1;
   volatile unsigned short bit6 : 1;
   volatile unsigned short bit7 : 1;
   volatile unsigned short bit8 : 1;
   volatile unsigned short bit9 : 1;
   volatile unsigned short bit10 : 1;
   volatile unsigned short bit11 : 1;
   volatile unsigned short bit12 : 1;
   volatile unsigned short bit13 : 1;
   volatile unsigned short bit14 : 1;
   volatile unsigned short bit15 : 1;

} GPIO_Bit_TypeDef;
/* USER CODE END ET */

/* Exported constants --------------------------------------------------------*/
/* USER CODE BEGIN EC */

extern unsigned char Usb_Rx_Data[64];
extern unsigned char Usb_Tx_Data[64];
extern char Usb_Rx_Len;

/* USER CODE END EC */

/* Exported macro ------------------------------------------------------------*/
/* USER CODE BEGIN EM */


//GPIOX->ODR��GPIOX->IDRǿ��ת��ΪGPIO_Bit_TypeDef* ָ��
#define PORTA_OUT    ((GPIO_Bit_TypeDef *)(&(GPIOA->ODR)))
#define PORTA_IN     ((GPIO_Bit_TypeDef *)(&(GPIOA->IDR)))
#define PORTB_OUT    ((GPIO_Bit_TypeDef *)(&(GPIOB->ODR)))
#define PORTB_IN     ((GPIO_Bit_TypeDef *)(&(GPIOB->IDR)))
#define PORTC_OUT    ((GPIO_Bit_TypeDef *)(&(GPIOC->ODR)))
#define PORTC_IN     ((GPIO_Bit_TypeDef *)(&(GPIOC->IDR)))
#define PORTD_OUT    ((GPIO_Bit_TypeDef *)(&(GPIOD->ODR)))
#define PORTD_IN     ((GPIO_Bit_TypeDef *)(&(GPIOD->IDR)))
#define PORTE_OUT    ((GPIO_Bit_TypeDef *)(&(GPIOE->ODR)))
#define PORTE_IN     ((GPIO_Bit_TypeDef *)(&(GPIOE->IDR)))
#define PORTF_OUT    ((GPIO_Bit_TypeDef *)(&(GPIOF->ODR)))
#define PORTF_IN     ((GPIO_Bit_TypeDef *)(&(GPIOF->IDR)))
#define PORTG_OUT    ((GPIO_Bit_TypeDef *)(&(GPIOG->ODR)))
#define PORTG_IN     ((GPIO_Bit_TypeDef *)(&(GPIOG->IDR)))

//���ú꺯����##�ַ���ƴ�ӣ�ȷ��Ҫ������IO��
#define PAout(n) (PORTA_OUT->bit##n)
#define PAin(n)  (PORTA_IN->bit##n)
#define PBout(n) (PORTB_OUT->bit##n)
#define PBin(n)  (PORTB_IN->bit##n)
#define PCout(n) (PORTC_OUT->bit##n)
#define PCin(n)  (PORTC_IN->bit##n)
#define PDout(n) (PORTD_OUT->bit##n)
#define PDin(n)  (PORTD_IN->bit##n)
#define PEout(n) (PORTE_OUT->bit##n)
#define PEin(n)  (PORTE_IN->bit##n)
#define PFout(n) (PORTF_OUT->bit##n)
#define PFin(n)  (PORTF_IN->bit##n)
#define PGout(n) (PORTG_OUT->bit##n)
#define PGin(n)  (PORTG_IN->bit##n)
/* USER CODE END EM */

/* Exported functions prototypes ---------------------------------------------*/

/* USER CODE BEGIN EFP */
#define LED0	PAout(15)
//#define LED1	PCout(3)


/* USER CODE END EFP */

/* Private defines -----------------------------------------------------------*/

/* USER CODE BEGIN Private defines */
typedef uint8_t 	u8;
typedef uint16_t	u16;
typedef uint32_t	u32;


#define FLOAT_MATH              1
#define IQ_MATH                 0
#ifndef MATH_TYPE
#define MATH_TYPE               FLOAT_MATH
#endif

#if MATH_TYPE == IQ_MATH
typedef int32_t	_iq;
#define IQabs(A)    (((A)>=0)?(A):(-A))
#define _IQmpy(A,B)	(  ((_iq)(A)*(_iq)(B) ) >> 15 )	
#define _IQdiv(A,B) (  ((_iq)(A)/(_iq)(B) ) << 15)
#define _IQmpy2(A)	( _IQmpy((A),_IQ(2)) )
#define _IQdiv2(A) 	( _IQmpy((A),_IQ(0.5)) )
#define _IQ(A)			( (_iq)((A)*32768) )
#define _IQtoF(A)		( (float)(A)/32768.0 )
#define _IQfrac(A)  ( (A) - (float)((long)(A)) )
#define _IQsat(A, Pos, Neg)     (((A) > (Pos)) ?                              \
                                 (Pos) :                                      \
                                 (((A) < (Neg)) ? (Neg) : (A)) )	
#define _IQ25(A)		( (_iq)((A)*33554432) )
#define _IQ15to_25(A) ((_iq)(A)<<10)
#define _IQmpy_25(A,B)	(  ((_iq)(A)*(_iq)(B) ) >> 25 )	
#define _IQmpy25_15(A,B)	 (  ((_iq)(A)*(_iq)(B) ) >>  15)
#endif

#if MATH_TYPE == FLOAT_MATH
typedef float 	_iq;
#define _IQabs(v)	fabs(v)
#define _IQmpy(A,B)	( (A)*(B)  )	
#define _IQdiv2(A) 	(_IQmpy((A),0.5f))
#define _IQ(v)			((float)v)
#define _IQtoF(v)		((float)v)
#define _IQfrac(A)  ((A) - (float)((long)(A)))
#define _IQsat(A, Pos, Neg)     (((A) > (Pos)) ?                              \
                                 (Pos) :                                      \
                                 (((A) < (Neg)) ? (Neg) : (A)) )	
#define WRAP_0_2PI(x)               \
({                                  \
    float _x = (x);                 \
    while (_x >= DUAL_PI) _x -= DUAL_PI; \
    while (_x < 0.0f)    _x += DUAL_PI;  \
    _x;                             \
})

#endif

/* USER CODE END Private defines */


#endif /* __MY_DEF_H */
