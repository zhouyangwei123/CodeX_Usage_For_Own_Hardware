#include <windows.h>
#include <stdio.h>
#include <string.h>
#include "firmware_update.h"
#include "firmware_image.h"
#include "protocol.h"
static uint32_t tick;static int resets,acks;
uint32_t HAL_GetTick(void){return tick;}
void test_reset(void){resets++;}
uint8_t protocol_send(uint8_t type,const uint8_t *p,uint8_t n){
 if(type==MSG_ACK && n==2 && p[0]==MSG_ENTER_DFU && p[1]==0)acks++;
 return 1;
}
int main(void){
 void *region=VirtualAlloc((void *)0x20010000,65536,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
 if(region!=(void *)0x20010000)return 2;
 const uint8_t good[8]={'N','M','T','D','F','U','0','1'};
 uint8_t bad[8];memcpy(bad,good,8);bad[0]^=1;
 firmware_update_request(good,7);firmware_update_request(bad,8);
 tick=1000;firmware_update_poll();
 int invalid_ok=(resets==0 && acks==0);
 tick=0xFFFFFFF0u;firmware_update_request(good,8);
 tick+=199u;firmware_update_poll();
 int delay_ok=(resets==0 && acks==1);
 tick++;firmware_update_poll();
 volatile uint32_t *flag=(volatile uint32_t *)NMT_REQUEST_ADDR;
 int reset_ok=(resets==1 && flag[0]==NMT_REQUEST_MAGIC && flag[1]==~NMT_REQUEST_MAGIC);
 printf("invalid token ignored: %s\nack and delay across tick wrap: %s\n200 ms reset handoff: %s\n",invalid_ok?"PASS":"FAIL",delay_ok?"PASS":"FAIL",reset_ok?"PASS":"FAIL");
 VirtualFree(region,0,MEM_RELEASE);return invalid_ok&&delay_ok&&reset_ok?0:1;
}
