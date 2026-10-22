/* Compile the actual protocol.c AND usbd_cdc_if.c; fake only the USB driver
   and Cortex interrupt primitives, never the production queue/CDC wrapper. */
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include "usbd_cdc_if.h"
#include "protocol.h"

USBD_HandleTypeDef hUsbDeviceFS;
static USBD_CDC_HandleTypeDef cdc;
static uint32_t irq_mask;
static int early_complete, completion_pending, force_busy, force_fail;
static int accepted, messages, failures, checks, submit_unmasked;
static uint8_t last[134];
static uint16_t last_len;

#define CHECK(name, condition) do { int ok = !!(condition); checks++; \
 printf("%-44s %s\n", name, ok ? "PASS" : "FAIL"); failures += !ok; } while (0)

static void complete(int skip_application_callback)
{
    cdc.TxState = 0;
    if (!skip_application_callback && USBD_Interface_fops_FS.TransmitCplt)
        USBD_Interface_fops_FS.TransmitCplt(cdc.TxBuffer, &cdc.TxLength, 1);
}
uint32_t __get_PRIMASK(void) { return irq_mask; }
void __disable_irq(void) { irq_mask = 1; }
void __set_PRIMASK(uint32_t value)
{
    irq_mask = value;
    if (!value && completion_pending) { completion_pending = 0; complete(0); }
}
uint8_t USBD_CDC_SetTxBuffer(USBD_HandleTypeDef *d,uint8_t *p,uint16_t n)
{
    USBD_CDC_HandleTypeDef *c = d->pClassData;
    c->TxBuffer=p; c->TxLength=n; return USBD_OK;
}
uint8_t USBD_CDC_SetRxBuffer(USBD_HandleTypeDef *d,uint8_t *p)
{ (void)d; (void)p; return USBD_OK; }
uint8_t USBD_CDC_ReceivePacket(USBD_HandleTypeDef *d)
{ (void)d; return USBD_OK; }
uint8_t USBD_CDC_TransmitPacket(USBD_HandleTypeDef *d)
{
    USBD_CDC_HandleTypeDef *c = d->pClassData;
    if (force_busy || c->TxState) return USBD_BUSY;
    if (force_fail) return USBD_FAIL;
    c->TxState=1; accepted++;
    if (!irq_mask) submit_unmasked++;
    last_len=(uint16_t)c->TxLength;
    memcpy(last,c->TxBuffer,last_len);
    if (early_complete) {
        if(irq_mask) completion_pending=1;
        else complete(0);
    }
    return USBD_OK;
}
void app_on_message(uint8_t type,const uint8_t *p,uint8_t n)
{ (void)type; (void)p; (void)n; messages++; }
static void enumerate_device(void)
{
    memset(&cdc,0,sizeof(cdc));
    hUsbDeviceFS.pClassData=&cdc;
    hUsbDeviceFS.dev_state=USBD_STATE_CONFIGURED;
    USBD_Interface_fops_FS.Init();
}
static void reset_test(void)
{
    protocol_init(); enumerate_device();
    irq_mask=early_complete=completion_pending=force_busy=force_fail=0;
    accepted=messages=submit_unmasked=0;
}
static void queue(uint8_t token)
{
    uint8_t p[4]={1,2,3,token};
    protocol_send(MSG_PONG,p,4);
}
static uint8_t crc(const uint8_t *p,int n)
{
    uint8_t c=0;
    for(int i=0;i<n;i++){c^=p[i];for(int k=0;k<8;k++)c=(c&128)?(uint8_t)((c<<1)^7):(uint8_t)(c<<1);}
    return c;
}
int main(int argc,char **argv)
{
    int baseline=argc>1 && !strcmp(argv[1],"--regressions-only");
    reset_test(); queue(1); protocol_tx_poll(); complete(1);
    queue(2); protocol_tx_poll();
    CHECK("lost-application-callback-recovers",accepted==2 && last[7]==2);

    reset_test(); early_complete=1;
    queue(1); protocol_tx_poll(); queue(2); protocol_tx_poll();
    CHECK("completion-before-submit-return-recovers",accepted==2 && last[7]==2);

    reset_test(); force_busy=1; queue(3); protocol_tx_poll();
    force_busy=0; protocol_tx_poll();
    CHECK("busy-keeps-frame-for-retry",accepted==1 && last[7]==3);

    reset_test(); force_fail=1; queue(4); protocol_tx_poll();
    force_fail=0; protocol_tx_poll();
    CHECK("failed-submit-keeps-frame-for-retry",accepted==1 && last[7]==4);

    reset_test(); queue(5); protocol_tx_poll();
    USBD_Interface_fops_FS.DeInit();
    enumerate_device(); queue(6); protocol_tx_poll();
    CHECK("usb-reenumeration-without-app-reboot",accepted==2 && last[7]==6);
    if(baseline) goto finish;

    reset_test(); queue(7); protocol_tx_poll();
    uint8_t saved[9]; memcpy(saved,cdc.TxBuffer,9);
    for(int i=0;i<30;i++){queue((uint8_t)i);protocol_tx_poll();}
    CHECK("inflight-buffer-not-overwritten",accepted==1 && !memcmp(saved,cdc.TxBuffer,9));
    complete(0); protocol_tx_poll();
    CHECK("queue-overflow-retains-newest-eight",accepted==2 && last[7]==22);
    CHECK("submission-critical-section-restored",!irq_mask && submit_unmasked==0);

    reset_test(); queue(9); hUsbDeviceFS.pClassData=NULL;
    protocol_tx_poll();
    CHECK("null-usb-class-does-not-crash-or-send",accepted==0 && !irq_mask);
    enumerate_device(); protocol_tx_poll();
    CHECK("queued-frame-survives-enumeration",accepted==1 && last[7]==9);

    reset_test(); queue(10); hUsbDeviceFS.dev_state=USBD_STATE_SUSPENDED;
    protocol_tx_poll();
    CHECK("suspend-does-not-submit",accepted==0);
    hUsbDeviceFS.dev_state=USBD_STATE_CONFIGURED; protocol_tx_poll();
    CHECK("resume-retries-pending-frame",accepted==1 && last[7]==10);

    reset_test(); irq_mask=1; queue(11); protocol_tx_poll();
    CHECK("preserve-preexisting-interrupt-mask",irq_mask==1 && accepted==1);
    irq_mask=0;

    reset_test();
    uint8_t source[9]={0xA5,0x5A,0x85,4,1,2,3,4,0};
    CDC_Transmit_FS(source,9); source[4]=99;
    CHECK("cdc-owns-accepted-data-copy",cdc.TxBuffer[4]==1);
    CHECK("cdc-busy-does-not-overwrite-copy",CDC_Transmit_FS(source,9)==USBD_BUSY && cdc.TxBuffer[4]==1);
    complete(0);
    CHECK("cdc-rejects-null-input",CDC_Transmit_FS(NULL,9)!=USBD_OK && !irq_mask);
    CHECK("cdc-rejects-oversize-input",CDC_Transmit_FS(source,APP_TX_DATA_SIZE+1)!=USBD_OK && !irq_mask);

    reset_test();
    uint8_t bad[260]={0xA5,0x5A,MSG_PING,255};
    for(size_t i=0;i<sizeof(bad);i++)protocol_rx_byte(bad[i]);
    protocol_process();
    CHECK("oversize-frame-rejected",messages==0);
    uint8_t ping[9]={0xA5,0x5A,MSG_PING,4,1,2,3,4,0};
    ping[8]=crc(ping+2,6);
    uint32_t n=sizeof(ping); USBD_Interface_fops_FS.Receive(ping,&n); protocol_process();
    CHECK("valid-receive-after-malformed-frame",messages==1);
    uint8_t largest[133]={0xA5,0x5A,MSG_PING,128};
    largest[132]=crc(largest+2,130);
    n=sizeof(largest);USBD_Interface_fops_FS.Receive(largest,&n);protocol_process();
    CHECK("maximum-payload-still-accepted",messages==2);

    reset_test(); early_complete=1;
    int expected=0, bad_frames=0;
    for(int i=0;i<100000;i++)
    {
        if(i%100==0){USBD_Interface_fops_FS.DeInit();enumerate_device();}
        queue((uint8_t)i);
        force_busy=(i%7==0); force_fail=(i%11==0);
        protocol_tx_poll(); force_busy=force_fail=0;
        if(accepted==expected)protocol_tx_poll();
        expected++;
        if(last_len!=9 || last[7]!=(uint8_t)i || last[8]!=crc(last+2,6))bad_frames++;
    }
    CHECK("100000-transfers-with-1000-usb-resets",accepted==expected && bad_frames==0);
    CHECK("stress-keeps-interrupt-state",!irq_mask && submit_unmasked==0);
finish:
    printf("\n%d checks, %d failures\n",checks,failures);
    return failures ? 1 : 0;
}
