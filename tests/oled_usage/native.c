#include "usage_oled.h"
#include "app.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
extern uint8_t fb[1024];
extern unsigned pixel_overflow;
static int check(int ok, const char *label)
{ if (!ok) fprintf(stderr, "FAIL %s\n", label); return !ok; }
static int selftest(void)
{
  uint8_t p[128] = {0}; p[0]=1; p[2]=USAGE_KNOWN|USAGE_QUOTA_KNOWN; p[3]=78; p[8]=1; p[48]=76;
  usage_oled_t frame, before;
  int errors=check(usage_oled_parse(&frame,p,128),"valid full-size payload"); before=frame;
  struct { unsigned offset; uint8_t value; } bad[]={{0,2},{1,2},{3,101},{48,77},{49,1},{50,13},{51,1},{52,35},{52,254}};
  for(unsigned i=0;i<sizeof(bad)/sizeof(bad[0]);i++)
  {
    uint8_t old=p[bad[i].offset];p[bad[i].offset]=bad[i].value;
    errors+=check(!usage_oled_parse(&frame,p,128)&&memcmp(&frame,&before,sizeof(frame))==0,"invalid frame is atomic");
    p[bad[i].offset]=old;
  }
  errors+=check(!usage_oled_parse(&frame,p,127),"truncated payload");
  p[24]=1;errors+=check(!usage_oled_parse(&frame,p,128),"local input/output sum");p[24]=0;
  errors+=check(app_next_oled_page(0,1)==1&&app_next_oled_page(1,1)==4&&app_next_oled_page(4,1)==2&&app_next_oled_page(2,1)==0,"forward pages skip legacy alias");
  errors+=check(app_next_oled_page(0,-1)==2&&app_next_oled_page(4,-1)==1,"reverse pages");
  usage_oled_render(&before,1,0,"U80K","D1.2M");
  errors+=check(pixel_overflow==0,"renderer within 128x64");
  printf("Native parse/navigation/render checks: %s\n",errors?"FAIL":"PASS");
  return errors?1:0;
}
int main(int argc,char **argv)
{
  if(argc==2&&strcmp(argv[1],"--selftest")==0)return selftest();
  if(argc!=3)return 2;
  FILE *in=fopen(argv[1],"rb");if(!in)return 3;
  uint8_t p[129];size_t n=fread(p,1,sizeof(p),in);fclose(in);
  usage_oled_t frame;if(n>128||!usage_oled_parse(&frame,p,(uint8_t)n))return 4;
  usage_oled_render(&frame,1,0,"U80K","D1.2M");
  if(pixel_overflow)return 5;
  FILE *out=fopen(argv[2],"wb");if(!out)return 6;
  size_t written=fwrite(fb,1,sizeof(fb),out);fclose(out);
  return written==sizeof(fb)?0:7;
}
