#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "firmware_image.h"
static int tests, failures;
#define CHECK(name,c) do { int ok=!!(c);tests++;failures+=!ok;printf("%s %s\n",ok?"PASS":"FAIL",name); } while(0)
static uint8_t *load(const char *path,size_t *size)
{
 FILE *f=fopen(path,"rb"); if(!f)exit(2); fseek(f,0,SEEK_END);*size=ftell(f);rewind(f);
 uint8_t *p=malloc(*size);if(fread(p,1,*size,f)!=*size)exit(2);fclose(f);return p;
}
int main(int argc,char **argv)
{
 if(argc!=3)return 2;
 size_t n,bn;uint8_t *app=load(argv[1],&n), *boot=load(argv[2],&bn);
 CHECK("compiled application accepted",nmt_image_valid(app,n,NMT_APP_BASE,NMT_APP_CAPACITY,NMT_APP_MAGIC));
 CHECK("compiled bootloader accepted",nmt_image_valid(boot,bn,NMT_BOOT_BASE,8192,NMT_BOOT_MAGIC));
 CHECK("boot image rejected as application",!nmt_image_valid(boot,bn,NMT_APP_BASE,NMT_APP_CAPACITY,NMT_APP_MAGIC));
 uint8_t *work=malloc(NMT_APP_CAPACITY);int safe=1,cases=0;
 for(size_t cut=0;cut<n;cut+=64){
  memset(work,0xFF,NMT_APP_CAPACITY);memcpy(work,app,cut);
  if(nmt_image_valid(work,NMT_APP_CAPACITY,NMT_APP_BASE,NMT_APP_CAPACITY,NMT_APP_MAGIC) &&
     memcmp(work,app,n)!=0)safe=0;
  cases++;
 }
 CHECK("interrupted writes never accept different data",safe);
 printf("Interrupted write points checked: %d\n",cases);
 memset(work,0xFF,NMT_APP_CAPACITY);memcpy(work,app,n);
 CHECK("complete image with erased trailing flash accepted",nmt_image_valid(work,NMT_APP_CAPACITY,NMT_APP_BASE,NMT_APP_CAPACITY,NMT_APP_MAGIC));
 const unsigned offsets[]={0,4,512,516,520,524,528,532,536,540,1024};
 safe=1;
 for(unsigned i=0;i<sizeof(offsets)/sizeof(offsets[0]);i++){
  memcpy(work,app,n);work[offsets[i]]^=1;
  if(nmt_image_valid(work,n,NMT_APP_BASE,NMT_APP_CAPACITY,NMT_APP_MAGIC))safe=0;
 }
 CHECK("vector header and code corruption rejected",safe);
 for(unsigned i=0;i<1024;i++){
  memcpy(work,app,n);unsigned off=(i*7919u)%(unsigned)n;work[off]^=(uint8_t)(1u<<(i%8));
  if(nmt_image_valid(work,n,NMT_APP_BASE,NMT_APP_CAPACITY,NMT_APP_MAGIC))safe=0;
 }
 CHECK("1024 scattered bit corruptions rejected",safe);
 CHECK("short header rejected",!nmt_image_valid(app,511,NMT_APP_BASE,NMT_APP_CAPACITY,NMT_APP_MAGIC));
 CHECK("wrong capacity rejected",!nmt_image_valid(app,n,NMT_APP_BASE,1024,NMT_APP_MAGIC));
 printf("%d checks, %d failures\n",tests,failures);
 free(work);free(app);free(boot);return failures?1:0;
}
