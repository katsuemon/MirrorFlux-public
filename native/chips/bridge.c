#include "host.h"
extern DEV_DEF devDef_AY8910_MAME;
extern DEV_DEF devDef_C6280_MAME;
extern DEV_DEF devDef_GB_MAME;
void mf_sn_link(void*,void*);
void mf_opx_mute(void*,int,int);
extern DEV_DEF devDef_RF5C68_MAME;
extern DEV_DEF devDef_SAA1099_MAME;
extern DEV_DEF devDef_SN76496_MAME;
extern const DEV_DECL sndDev_32X_PWM;
extern const DEV_DECL sndDev_BSMT2000;
extern const DEV_DECL sndDev_C140;
extern const DEV_DECL sndDev_C219;
extern const DEV_DECL sndDev_C352;
extern const DEV_DECL sndDev_ES5503;
extern const DEV_DECL sndDev_GA20;
extern const DEV_DECL sndDev_ICS2115;
extern const DEV_DECL sndDev_K005289;
extern const DEV_DECL sndDev_K007232;
extern const DEV_DECL sndDev_K051649;
extern const DEV_DECL sndDev_K053260;
extern const DEV_DECL sndDev_K054539;
extern const DEV_DECL sndDev_MSM5205;
extern const DEV_DECL sndDev_MSM5232;
extern const DEV_DECL sndDev_MSM6258;
extern const DEV_DECL sndDev_MSM6295;
extern const DEV_DECL sndDev_Mikey;
extern const DEV_DECL sndDev_Pokey;
extern const DEV_DECL sndDev_SegaPCM;
extern const DEV_DECL sndDev_VBoyVSU;
extern const DEV_DECL sndDev_X1_010;
extern const DEV_DECL sndDev_YMF271;
extern const DEV_DECL sndDev_YMW258;
extern const DEV_DECL sndDev_YMZ280B;
extern const DEV_DECL sndDev_uPD7759;
extern const DEV_DEF mf_nes_core,mf_ws_core,mf_es550x_core,mf_qsound_core;
extern const DEV_DECL sndDev_SCSP;
void mf_multipcm_bank(void*,UINT32,UINT32);
static const DEV_DEF* definition(unsigned type) { switch(type) {
case 20:return &mf_nes_core;case 32:return sndDev_SCSP.cores[0];case 33:return &mf_ws_core;case 37:return &mf_es550x_core;
case 0: return &devDef_SN76496_MAME;
case 4: return sndDev_SegaPCM.cores[0];
case 5: return &devDef_RF5C68_MAME;
case 14: return sndDev_YMF271.cores[0];
case 15: return sndDev_YMZ280B.cores[0];
case 16: return &devDef_RF5C68_MAME;
case 17: return sndDev_32X_PWM.cores[0];
case 18: return &devDef_AY8910_MAME;
case 19: return &devDef_GB_MAME;
case 21: return sndDev_YMW258.cores[0];
case 22: return sndDev_uPD7759.cores[0];
case 23: return sndDev_MSM6258.cores[0];
case 24: return sndDev_MSM6295.cores[0];
case 25: return sndDev_K051649.cores[0];
case 26: return sndDev_K054539.cores[0];
case 27: return &devDef_C6280_MAME;
case 28: return sndDev_C140.cores[0];
case 29: return sndDev_K053260.cores[0];
case 30: return sndDev_Pokey.cores[0];
case 31: return &mf_qsound_core;
case 34: return sndDev_VBoyVSU.cores[0];
case 35: return &devDef_SAA1099_MAME;
case 36: return sndDev_ES5503.cores[0];
case 38: return sndDev_X1_010.cores[0];
case 39: return sndDev_C352.cores[0];
case 40: return sndDev_GA20.cores[0];
case 41: return sndDev_Mikey.cores[0];
case 42: return sndDev_K007232.cores[0];
case 43: return sndDev_K005289.cores[0];
case 44: return sndDev_MSM5205.cores[0];
case 45: return sndDev_MSM5232.cores[0];
case 46: return sndDev_BSMT2000.cores[0];
case 47: return sndDev_ICS2115.cores[0];
case 48: return sndDev_C219.cores[0];
default:return NULL;}}

#define API __declspec(dllexport)
typedef struct {
 DEV_INFO info; unsigned type,outputRate; UINT64 phase; INT32 previous[2],current[2]; UINT32 memorySize[4];
} MF_CHIP;
static UINT32 h32(const UINT8* h,UINT32 length,UINT32 offset) {
 if (offset+4>length) return 0;
 return h[offset]|((UINT32)h[offset+1]<<8)|((UINT32)h[offset+2]<<16)|((UINT32)h[offset+3]<<24);
}
static UINT8 h8(const UINT8* h,UINT32 length,UINT32 offset) { return offset<length?h[offset]:0; }
static void rate_changed(void* p,UINT32 rate) { if(rate) ((MF_CHIP*)p)->info.sampleRate=rate; }
API void* mf_chip_create(UINT32 type,UINT32 clock,UINT32 rate,const UINT8* header,UINT32 headerSize) {
 const DEV_DEF* def=definition(type); if(!def || !clock || !rate) return NULL;
 MF_CHIP* p=calloc(1,sizeof(*p)); if(!p) return NULL;
 union { DEV_GEN_CFG gen; SN76496_CFG sn; SEGAPCM_CFG sega; AY8910_CFG ay; MSM6258_CFG oki; MSM5205_CFG msm; MSM5232_CFG tone; } cfg;
 memset(&cfg,0,sizeof(cfg));cfg.gen.clock=clock&0x3fffffff;cfg.gen.smplRate=rate;
 p->type=type;p->outputRate=rate;
 switch(type) {
 case 0: {
  unsigned flags=h8(header,headerSize,0x2b);
  cfg.sn.noiseTaps=h32(header,headerSize,0x28)&0xffff; if(!cfg.sn.noiseTaps)cfg.sn.noiseTaps=9;
  cfg.sn.shiftRegWidth=h8(header,headerSize,0x2a);if(!cfg.sn.shiftRegWidth)cfg.sn.shiftRegWidth=16;
  cfg.sn.negate=!(flags&2);cfg.sn.clkDiv=(flags&8)?1:8;cfg.sn.ncrPSG=(flags>>4)&1;
  cfg.sn.segaPSG=!(flags&1);cfg.sn.stereo=!(flags&4);break;
 }
 case 4: cfg.sega.bnkshift=h8(header,headerSize,0x3c);cfg.sega.bnkmask=h8(header,headerSize,0x3e);break;
 case 18: cfg.ay.chipType=h8(header,headerSize,0x78);cfg.ay.chipFlags=h8(header,headerSize,0x79);break;
 case 19: cfg.gen.flags=clock>>31;break;
 case 20: cfg.gen.flags=clock>>31;break;
 case 37:cfg.gen.flags=(clock>>31)?0x80:0;break;
 case 22: cfg.gen.flags=clock>>31;break;
 case 23: { unsigned flags=h8(header,headerSize,0x94);cfg.oki.divider=flags&3;cfg.oki.adpcmBits=(flags&4)?3:4;cfg.oki.outputBits=(flags&8)?12:10;break; }
 case 24: cfg.gen.flags=clock>>31;break;
 case 26: cfg.gen.flags=h8(header,headerSize,0x95);break;
 case 28: cfg.gen.flags=h8(header,headerSize,0x96);break;
 case 36: cfg.gen.flags=h8(header,headerSize,0xd4);if(!cfg.gen.flags)cfg.gen.flags=2;break;
 case 39: { unsigned divider=h8(header,headerSize,0xd6)*4;if(!divider){free(p);return NULL;}cfg.gen.clock=(UINT32)((UINT64)cfg.gen.clock*288/divider);cfg.gen.flags=clock>>31;break; }
 case 44: cfg.gen.flags=clock>>31;cfg.msm.prescaler=h8(header,headerSize,0xd7)&3;cfg.msm.adpcmBits=(h8(header,headerSize,0xd7)&4)?4:3;break;
 case 45: for(int i=0;i<8;i++)cfg.tone.capacitors[i]=1e-6;break;
 }
 if(def->Start(&cfg.gen,&p->info) || !p->info.dataPtr || !p->info.sampleRate) { free(p);return NULL; }
 def->Reset(p->info.dataPtr);
 if(def->SetSRateChgCB)def->SetSRateChgCB(p->info.dataPtr,rate_changed,p);
 return p;
}
API void mf_chip_link(void* tone,void* noise) {
 MF_CHIP* a=tone;MF_CHIP* b=noise;
 if(a && b && a->type==0 && b->type==0)mf_sn_link(b->info.dataPtr,a->info.dataPtr);
}
API void mf_chip_close(void* ptr) {
 MF_CHIP* p=ptr;if(!p)return;
 p->info.devDef->Stop(p->info.dataPtr);free(p);
}
static const DEVDEF_RWFUNC* find_function(MF_CHIP* p,unsigned kind,unsigned width,unsigned bank) {
 for(const DEVDEF_RWFUNC* f=p->info.devDef->rwFuncs;f && f->funcPtr;f++)
  if(f->funcType==kind && (!width || f->rwType==width) && f->user==bank)return f;
 return NULL;
}
API int mf_chip_write(void* ptr,UINT32 port,UINT32 address,UINT32 value) {
 MF_CHIP* p=ptr;if(!p)return -1;void* data=p->info.dataPtr;
 // Address/data pairs are used by these chips' bus interfaces.
 if(port==0 && (p->type==18 || p->type==15 || p->type==35)) {
  const DEVDEF_RWFUNC* f=find_function(p,RWF_REGISTER,DEVRW_A8D8,0);if(!f)return -1;
  DEVFUNC_WRITE_A8D8 w=(DEVFUNC_WRITE_A8D8)f->funcPtr;w(data,0,(UINT8)address);w(data,1,(UINT8)value);return 0;
 }
 if(p->type==14 || p->type==25) {
  const DEVDEF_RWFUNC* f=find_function(p,RWF_REGISTER,DEVRW_A8D8,0);if(!f)return -1;
  DEVFUNC_WRITE_A8D8 w=(DEVFUNC_WRITE_A8D8)f->funcPtr;w(data,(UINT8)(port*2),(UINT8)address);w(data,(UINT8)(port*2+1),(UINT8)value);return 0;
 }
 if(port==256) {
  const DEVDEF_RWFUNC* memory=find_function(p,RWF_MEMORY,DEVRW_A16D8,0);
  if(memory){((DEVFUNC_WRITE_A16D8)memory->funcPtr)(data,(UINT16)address,(UINT8)value);return 0;}
  memory=find_function(p,RWF_MEMORY,DEVRW_BLOCK,0);UINT8 byte=(UINT8)value;
  if(memory){((DEVFUNC_WRITE_BLOCK)memory->funcPtr)(data,address,1,&byte);return 0;}return -1;
 }
 if(port==257 && p->type==21){mf_multipcm_bank(data,address,value<<16);return 0;}
 if(port==259 && p->type==18){ay8910_set_stereo_mask(data,value);return 0;}
 if(p->type==42 && address==0x1f) {
  const DEVDEF_RWFUNC* r=find_function(p,RWF_REGISTER|RWF_READ,DEVRW_A8D8,0);
  if(!r)return -1;((UINT8(*)(void*,UINT8))r->funcPtr)(data,(UINT8)value);return 0;
 }
 const DEVDEF_RWFUNC* f=find_function(p,p->type==31 || (p->type==46 && port==258)?RWF_QUICKWRITE:RWF_REGISTER,port==258?DEVRW_A8D16:0,0);
 if(!f)return -1;
 switch(f->rwType) {
 case DEVRW_A8D8:((DEVFUNC_WRITE_A8D8)f->funcPtr)(data,(UINT8)address,(UINT8)value);break;
 case DEVRW_A16D8:((DEVFUNC_WRITE_A16D8)f->funcPtr)(data,(UINT16)address,(UINT8)value);break;
 case DEVRW_A8D16:((DEVFUNC_WRITE_A8D16)f->funcPtr)(data,(UINT8)address,(UINT16)value);break;
 case DEVRW_A16D16:((DEVFUNC_WRITE_A16D16)f->funcPtr)(data,(UINT16)address,(UINT16)value);break;
 default:return -1;
 }
 return 0;
}
API int mf_chip_memory(void* ptr,UINT32 bank,UINT32 size,UINT32 offset,const UINT8* bytes,UINT32 length) {
 MF_CHIP* p=ptr;if(!p)return -1;UINT32 position=p->type==37?offset&0x0fffffff:offset;
 if(bank>=4 || size>64*1024*1024 || position>size || length>size-position)return -1;
 const DEVDEF_RWFUNC* writer=find_function(p,RWF_MEMORY,DEVRW_BLOCK,bank);
 if(!writer)return -1;void* data=p->info.dataPtr;
 const DEVDEF_RWFUNC* alloc=find_function(p,RWF_MEMORY,DEVRW_MEMSIZE,bank);
 if(alloc && size>p->memorySize[bank]) { ((DEVFUNC_WRITE_MEMSIZE)alloc->funcPtr)(data,size);p->memorySize[bank]=size; }
 ((DEVFUNC_WRITE_BLOCK)writer->funcPtr)(data,offset,length,bytes);return 0;
}
API void mf_chip_mask(void* ptr,UINT32 mask) {
 MF_CHIP* p=ptr;if(!p)return;
 if(p->info.devDef->SetMuteMask)p->info.devDef->SetMuteMask(p->info.dataPtr,mask);
 if(p->type==14)for(int i=0;i<48;i++)mf_opx_mute(p->info.dataPtr,i,mask==0xffffffff || (i<32 && (mask&(1u<<i))));
}
static void step(MF_CHIP* p) { DEV_SMPL* outputs[2]={&p->current[0],&p->current[1]};p->current[0]=p->current[1]=0;p->info.devDef->Update(p->info.dataPtr,1,outputs); }
API void mf_chip_render(void* ptr,INT32* out,UINT32 frames) {
 MF_CHIP* p=ptr;UINT32 native=p->info.sampleRate,rate=p->outputRate;
 for(UINT32 frame=0;frame<frames;frame++) {
  if(native>=rate) {
   UINT64 remaining=native;INT64 sum[2]={0,0};
   while(remaining) {if(!p->phase){step(p);p->phase=rate;}UINT64 part=remaining<p->phase?remaining:p->phase;
    sum[0]+=(INT64)p->current[0]*part;sum[1]+=(INT64)p->current[1]*part;p->phase-=part;remaining-=part;}
   out[frame*2]=(INT32)(sum[0]/native);out[frame*2+1]=(INT32)(sum[1]/native);
  } else {
   while(p->phase>=rate){memcpy(p->previous,p->current,sizeof(p->current));step(p);p->phase-=rate;}
   for(unsigned c=0;c<2;c++)out[frame*2+c]=p->previous[c]+(INT32)(((INT64)p->current[c]-p->previous[c])*(INT64)p->phase/rate);
   p->phase+=native;
  }
 }
}
API UINT32 mf_chip_supported(UINT32 type) { return definition(type)!=NULL; }

// Runtime voice observations owned by MirrorFlux; no playback-framework state is exposed.
typedef struct {UINT32 type,instance,channel,flags;double frequency;INT32 note;float level,left,right;} MF_VOICE;
int mf_okim6295_voice(void*,int,double*,float*,int*);
void mf_pwm_voice(void*,int,float*,int*);int mf_pwm_wave(void*,int,double*,int);
void mf_vsu_voice(void*,int,double*,float*,float*,float*,int*);
void mf_msm5205_voice(void*,double*,float*,int*);
int mf_okim6258_voice(void*,double*,float*,float*,float*,int*);
int mf_ymz280b_voice(void*,int,double*,float*,float*,float*,int*);
double mf_ymz280b_wave(void*,int,double*,int);
int mf_qsound_voice(void*,int,double*,float*,float*,float*,int*);
void mf_es550x_voice(void*,int,double*,double*,float*,float*,float*,int*);
int mf_opx_voice(void*,int,double*,double*,float*,float*,float*,int*);void mf_opx_mute(void*,int,int);
API int mf_chip_voices(void* pointer,UINT32 instance,MF_VOICE* out,int capacity) {
 MF_CHIP* p=pointer;if(!p)return 0;unsigned type=p->type;
 int count=type==14?48:type==39||type==37?32:type==31?19:type==34?6:type==17?2:type==24?4:type==23||type==44?1:type==15?8:0;
 if(count>capacity)count=capacity;void* data=p->info.dataPtr;
 for(int ch=0;ch<count;ch++) {
  MF_VOICE v={0};v.type=type;v.instance=instance;v.channel=ch;double ratio=0;int flags=0;
  switch(type) {
  case 39: {
   const DEVDEF_RWFUNC* f=find_function(p,RWF_READ,DEVRW_A16D16,0);if(!f)return 0;
   UINT16 (*read)(void*,UINT16)=(UINT16(*)(void*,UINT16))f->funcPtr;
   unsigned front=read(data,ch*8),rear=read(data,ch*8+1),pitch=read(data,ch*8+2),state=read(data,ch*8+3);
   v.left=fmaxf(front>>8,rear>>8)/255.0f;v.right=fmaxf(front&255,rear&255)/255.0f;v.level=fmaxf(v.left,v.right);
   flags=((state&0x8000)&&v.level>0?1:0)|((state&0x10)?4:0)|((front||rear||pitch||state)?8:0);
   ratio=pitch/32768.0;v.frequency=p->info.sampleRate*pitch/65536.0;break;
  }
  case 15:mf_ymz280b_voice(data,ch,&ratio,&v.level,&v.left,&v.right,&flags);v.frequency=p->info.sampleRate*ratio/2;break;
  case 31:mf_qsound_voice(data,ch,&ratio,&v.level,&v.left,&v.right,&flags);v.frequency=p->info.sampleRate*ratio;break;
  case 24:mf_okim6295_voice(data,ch,&v.frequency,&v.level,&flags);v.left=v.right=1;ratio=v.frequency/8000;break;
  case 17:mf_pwm_voice(data,ch,&v.level,&flags);v.left=ch==0;v.right=ch==1;break;
  case 34:mf_vsu_voice(data,ch,&v.frequency,&v.level,&v.left,&v.right,&flags);break;
  case 44:mf_msm5205_voice(data,&v.frequency,&v.level,&flags);v.left=v.right=1;break;
  case 23:mf_okim6258_voice(data,&v.frequency,&v.level,&v.left,&v.right,&flags);ratio=v.frequency/8000;break;
  case 14:mf_opx_voice(data,ch,&v.frequency,&ratio,&v.level,&v.left,&v.right,&flags);break;
  case 37:mf_es550x_voice(data,ch,&v.frequency,&ratio,&v.left,&v.right,&v.level,&flags);break;
  }
  v.flags=flags;v.note=(flags&32)&&v.frequency>0?(int)floor(69+12*log2(v.frequency/440)+.5):((flags&6)&&type!=15)||ratio<=0?60:(int)floor(60+12*log2(ratio)+.5);
  if(v.note<0)v.note=0;if(v.note>127)v.note=127;out[ch]=v;
 }
 return count;
}
API double mf_chip_wave(void* pointer,int channel,double* out,int count){MF_CHIP* p=pointer;if(!p)return 0;return p->type==15?mf_ymz280b_wave(p->info.dataPtr,channel,out,count):p->type==17?mf_pwm_wave(p->info.dataPtr,channel,out,count):0;}
API int mf_chip_channel(void* pointer,int channel,int muted){MF_CHIP* p=pointer;if(!p || p->type!=14 || channel<0 || channel>=48)return 0;mf_opx_mute(p->info.dataPtr,channel,muted);return 1;}
