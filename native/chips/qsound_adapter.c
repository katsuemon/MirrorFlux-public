#include "host.h"
#include "qsound/qsound.h"
extern const DEV_DEF mf_qsound_core;
typedef struct {DEV_DATA data;struct qsound_chip core;} MF_QSOUND;
static UINT8 qs_start(const DEV_GEN_CFG* cfg,DEV_INFO* out) {
 MF_QSOUND* p=calloc(1,sizeof(*p));if(!p)return 1;
 UINT32 rate=(UINT32)qsound_start(&p->core,cfg->clock<10000000?cfg->clock*15:cfg->clock);
 p->data.chipInf=p;INIT_DEVINF(out,&p->data,rate,&mf_qsound_core);return 0;
}
static void qs_stop(void* data){MF_QSOUND* p=data;free(p->core.rom_data);free(p);}
static void qs_reset(void* data){MF_QSOUND* p=data;qsound_reset(&p->core);INT16 left,right;INT16* out[2]={&left,&right};for(int i=0;i<32 && !p->core.ready_flag;i++)qsound_stream_update(&p->core,out,1);}
static void qs_write(void* data,UINT8 address,UINT16 value){qsound_write_data(&((MF_QSOUND*)data)->core,address,value);}
static void qs_alloc(void* data,UINT32 size){struct qsound_chip* q=&((MF_QSOUND*)data)->core;UINT32 length=ceil_pow2(size);q->rom_data=realloc(q->rom_data,length);q->rom_mask=length?length-1:0;if(length)memset(q->rom_data,0xff,length);}
static void qs_memory(void* data,UINT32 offset,UINT32 length,const UINT8* bytes){struct qsound_chip* q=&((MF_QSOUND*)data)->core;if(!q->rom_data || offset>q->rom_mask)return;if(length>q->rom_mask+1-offset)length=q->rom_mask+1-offset;memcpy(q->rom_data+offset,bytes,length);}
static void qs_mask(void* data,UINT32 mask){((MF_QSOUND*)data)->core.mf_mask=mask;}
static void qs_update(void* data,UINT32 frames,DEV_SMPL** out){struct qsound_chip* q=&((MF_QSOUND*)data)->core;for(UINT32 i=0;i<frames;i++){INT16 l,r;INT16* sample[2]={&l,&r};qsound_stream_update(q,sample,1);out[0][i]=l;out[1][i]=r;}}
int mf_qsound_voice(void* data,int ch,double* ratio,float* level,float* left,float* right,int* flags){
 struct qsound_chip* q=&((MF_QSOUND*)data)->core;if(ch<0||ch>=19)return 0;
 int volume=ch<16?q->voice[ch].volume:q->adpcm[ch-16].cur_vol;
 *ratio=ch<16?q->voice[ch].rate/4096.0:1.0/3;
 *level=fabsf(volume/32768.0f);unsigned pan=q->voice_pan[ch];
 *left=pan<0x130?1:0;*right=pan>0x110?1:0;
 *flags=(volume && (ch>=16 || q->voice[ch].rate)?1:0)|(ch>=16?2:0);
 if(*flags&1)q->mf_used|=1u<<ch;if(q->mf_used&(1u<<ch))*flags|=8;return 1;
}
static DEVDEF_RWFUNC functions[]={
 {RWF_QUICKWRITE,DEVRW_A8D16,0,qs_write},{RWF_MEMORY,DEVRW_MEMSIZE,0,qs_alloc},
 {RWF_MEMORY,DEVRW_BLOCK,0,qs_memory},{0,0,0,NULL}};
const DEV_DEF mf_qsound_core={"QSound","qsound-hle",0,qs_start,qs_stop,qs_reset,qs_update,NULL,qs_mask,NULL,NULL,NULL,NULL,functions};
