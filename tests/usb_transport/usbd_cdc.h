#ifndef TEST_USBD_CDC_H
#define TEST_USBD_CDC_H
#include <stdint.h>
#include <stddef.h>
#define USBD_OK 0
#define USBD_BUSY 1
#define USBD_FAIL 2
#define USBD_STATE_CONFIGURED 3
#define USBD_STATE_SUSPENDED 4
#define CDC_SEND_ENCAPSULATED_COMMAND 0
#define CDC_GET_ENCAPSULATED_RESPONSE 1
#define CDC_SET_COMM_FEATURE 2
#define CDC_GET_COMM_FEATURE 3
#define CDC_CLEAR_COMM_FEATURE 4
#define CDC_SET_LINE_CODING 0x20
#define CDC_GET_LINE_CODING 0x21
#define CDC_SET_CONTROL_LINE_STATE 0x22
#define CDC_SEND_BREAK 0x23
#define UNUSED(x) ((void)(x))
typedef struct { volatile uint32_t TxState; uint8_t *TxBuffer; uint32_t TxLength; } USBD_CDC_HandleTypeDef;
typedef struct { void *pClassData; uint8_t dev_state; } USBD_HandleTypeDef;
typedef struct {
    int8_t (*Init)(void);
    int8_t (*DeInit)(void);
    int8_t (*Control)(uint8_t,uint8_t*,uint16_t);
    int8_t (*Receive)(uint8_t*,uint32_t*);
    int8_t (*TransmitCplt)(uint8_t*,uint32_t*,uint8_t);
} USBD_CDC_ItfTypeDef;
uint32_t __get_PRIMASK(void);
void __disable_irq(void);
void __set_PRIMASK(uint32_t value);
uint8_t USBD_CDC_SetTxBuffer(USBD_HandleTypeDef*,uint8_t*,uint16_t);
uint8_t USBD_CDC_SetRxBuffer(USBD_HandleTypeDef*,uint8_t*);
uint8_t USBD_CDC_ReceivePacket(USBD_HandleTypeDef*);
uint8_t USBD_CDC_TransmitPacket(USBD_HandleTypeDef*);
#endif
