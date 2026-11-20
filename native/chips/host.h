// MirrorFlux standalone chip host. No player, resampler or device framework dependency.
#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdio.h>
#include "constants.h"
#ifdef __cplusplus
extern "C" {
#endif
typedef uint8_t UINT8; typedef int8_t INT8; typedef uint16_t UINT16; typedef int16_t INT16;
typedef uint32_t UINT32; typedef int32_t INT32; typedef uint64_t UINT64; typedef int64_t INT64;
typedef int32_t DEV_SMPL; typedef uint8_t DEV_ID;
#define INLINE static inline
#define logerror(...) ((void)0)
#define FCC_MEDNAFEN 2
#define CHP_GET_INF_PTR(p) (((DEV_DATA*)(p))->chipInf)
typedef struct { void* chipInf; } DEV_DATA;
typedef struct { UINT32 emuCore; UINT8 srMode,flags; UINT32 clock,smplRate; } DEV_GEN_CFG;
typedef void (*DEVCB_SRATE_CHG)(void*,UINT32);
typedef void (*DEVCB_LOG)(void*,void*,UINT8,const char*);
typedef struct DEV_INFO DEV_INFO;
typedef struct DEV_DEF DEV_DEF;
typedef UINT8 (*DEVFUNC_START)(const DEV_GEN_CFG*,DEV_INFO*);
typedef void (*DEVFUNC_CTRL)(void*);
typedef void (*DEVFUNC_UPDATE)(void*,UINT32,DEV_SMPL**);
typedef void (*DEVFUNC_WRITE_A8D8)(void*,UINT8,UINT8);
typedef void (*DEVFUNC_WRITE_A16D8)(void*,UINT16,UINT8);
typedef void (*DEVFUNC_WRITE_A8D16)(void*,UINT8,UINT16);
typedef void (*DEVFUNC_WRITE_A16D16)(void*,UINT16,UINT16);
typedef void (*DEVFUNC_WRITE_MEMSIZE)(void*,UINT32);
typedef void (*DEVFUNC_WRITE_BLOCK)(void*,UINT32,UINT32,const UINT8*);
typedef struct { UINT8 funcType,rwType; UINT16 user; void* funcPtr; } DEVDEF_RWFUNC;
struct DEV_DEF {
 const char *name,*author; UINT32 coreID;
 DEVFUNC_START Start; DEVFUNC_CTRL Stop,Reset; DEVFUNC_UPDATE Update;
 void (*SetOptionBits)(void*,UINT32); void (*SetMuteMask)(void*,UINT32);
 void (*SetPanning)(void*,const INT16*);
 void (*SetSRateChgCB)(void*,DEVCB_SRATE_CHG,void*);
 void (*SetLogCB)(void*,DEVCB_LOG,void*);
 UINT8 (*LinkDevice)(void*,UINT8,const DEV_INFO*);
 const DEVDEF_RWFUNC* rwFuncs;
};
typedef struct { UINT32 devCount; DEV_ID devIDs[4]; } DEVLINK_IDS;
typedef struct { DEV_ID devID; UINT8 linkID; DEV_GEN_CFG* cfg; } DEVLINK_INFO;
typedef struct {
 DEV_ID deviceID;
 const char* (*name)(const DEV_GEN_CFG*);
 UINT16 (*channelCount)(const DEV_GEN_CFG*);
 const char** (*channelNames)(const DEV_GEN_CFG*);
 const DEVLINK_IDS* (*linkDevIDs)(const DEV_GEN_CFG*);
 const DEV_DEF* cores[4];
} DEV_DECL;
struct DEV_INFO { DEV_DATA* dataPtr; UINT32 sampleRate; const DEV_DEF* devDef; const DEV_DECL* devDecl; UINT32 linkDevCount; DEVLINK_INFO* linkDevs; };
INLINE void INIT_DEVINF(DEV_INFO* out,DEV_DATA* data,UINT32 rate,const DEV_DEF* definition) {
 memset(out,0,sizeof(*out)); out->dataPtr=data;out->sampleRate=rate;out->devDef=definition;
}
#define SRATE_CUSTOM_HIGHEST(mode,rate,custom) do { if ((mode)==1 || ((mode)==2 && (rate)<(custom))) (rate)=(custom); } while(0)
INLINE UINT32 pow2_mask(UINT32 size) { UINT32 mask=0; while(mask<size-1 && size) mask=(mask<<1)|1; return mask; }
INLINE UINT32 ceil_pow2(UINT32 size) { return size ? pow2_mask(size)+1 : 0; }
typedef struct { DEVCB_LOG callback; void* parameter; void* source; } DEV_LOGGER;
INLINE void dev_logger_set(DEV_LOGGER* p,void* source,DEVCB_LOG cb,void* arg) { p->callback=cb;p->source=source;p->parameter=arg; }
#define emu_logf(...) ((void)0)
typedef struct { DEV_GEN_CFG _genCfg; UINT16 noiseTaps; UINT8 shiftRegWidth,negate,clkDiv,ncrPSG,segaPSG,stereo; void* t6w28_tone; } SN76496_CFG;
typedef struct { DEV_GEN_CFG _genCfg; UINT8 bnkshift,bnkmask; } SEGAPCM_CFG;
typedef struct { DEV_GEN_CFG _genCfg; UINT8 chipType,chipFlags; } AY8910_CFG;
typedef struct { DEV_GEN_CFG _genCfg; UINT8 divider,adpcmBits,outputBits; } MSM6258_CFG;
typedef struct { DEV_GEN_CFG _genCfg; UINT8 prescaler,adpcmBits; } MSM5205_CFG;
typedef struct { DEV_GEN_CFG _genCfg; double capacitors[8]; } MSM5232_CFG;

#define RWF_QUICKWRITE 2
#define RWF_QUICKREAD 3
#define ALL_8910_CHANNELS -1
#define AY8910_INTERNAL_RESISTANCE 356
#define YM2149_INTERNAL_RESISTANCE 353
#define AY8910_LEGACY_OUTPUT 1
#define AY8910_SINGLE_OUTPUT 2
#define AY8910_DISCRETE_OUTPUT 4
#define AY8910_RESISTOR_OUTPUT 8
typedef void (*SAMPLE_END_CB)(void*,int);
// Exact rational cycle counter: carry the remainder across sample boundaries.
typedef struct { UINT64 phase,numerator,denominator; } RATIO_CNTR;
INLINE void RC_SET_RATIO(RATIO_CNTR* p,UINT32 numerator,UINT32 denominator) { p->numerator=numerator;p->denominator=denominator?denominator:1;p->phase=0; }
INLINE void RC_RESET(RATIO_CNTR* p) { p->phase=0; }
INLINE void RC_STEP(RATIO_CNTR* p) { p->phase+=p->numerator; }
INLINE UINT32 RC_GET_VAL(RATIO_CNTR* p) { return (UINT32)(p->phase/p->denominator); }
INLINE void RC_MASK(RATIO_CNTR* p) { p->phase%=p->denominator; }
typedef struct _ay8910_context ay8910_context;
UINT8 device_start_ay8910_mame(const AY8910_CFG*,DEV_INFO*);
UINT32 ay8910_start(void**,UINT32,UINT8,UINT8);
void ay8910_stop(void*); void ay8910_reset(void*);
void ay8910_set_clock(void*,UINT32); UINT32 ay8910_get_sample_rate(void*);
void ay8910_write(void*,UINT8,UINT8); UINT8 ay8910_read(void*,UINT8);
void ay8910_write_reg(ay8910_context*,UINT8,UINT8);
void ay8910_update_one(void*,UINT32,DEV_SMPL**);
void ay8910_set_mute_mask(void*,UINT32); void ay8910_set_stereo_mask(void*,UINT32);
void ay8910_set_srchg_cb(void*,DEVCB_SRATE_CHG,void*);
void ay8910_set_log_cb(void*,DEVCB_LOG,void*);

#define OPT_NES_UNMUTE_ON_RESET 1
#define OPT_NES_NONLINEAR_MIXER 2
#define OPT_NES_PHASE_REFRESH 4
#define OPT_NES_DUTY_SWAP 8
#define OPT_NES_DPCM_ANTI_CLICK 16
#define OPT_NES_RANDOMIZE_NOISE 32
#define OPT_NES_TRIANGLE_MUTE 64
#define OPT_NES_REVERSE_DPCM 128
typedef struct _nesapu_state nesapu_state;
UINT8 nes_apu_read(void*,UINT8);void nes_apu_write(void*,UINT8,UINT8);
void nes_apu_update(void*,UINT32,DEV_SMPL**);void* device_start_nesapu(UINT32,UINT32);
void device_stop_nesapu(void*);void device_reset_nesapu(void*);
void nesapu_set_rom(void*,const UINT8*);void nesapu_set_mute_mask(void*,UINT32);
UINT32 nesapu_get_mute_mask(void*);void nesapu_set_options(void*,UINT32);
void nesapu_set_panning(void*,INT16,INT16,INT16,INT16,INT16);
#define PANNING_BITS 16
INLINE void centre_panning(INT32* p) { p[0]=p[1]=65536; }
INLINE void calc_panning(INT32* p,INT16 position) { double x=fmax(-256,fmin(256,position))/256.0;p[0]=(INT32)(65536*sqrt(1-x));p[1]=(INT32)(65536*sqrt(1+x)); }

#define APPLY_PANNING_S(value,pan) ((INT32)(((INT64)(value)*(pan))/65536))
#define APPLY_PANNING_L(value,pan) APPLY_PANNING_S(value,pan)
#define Panning_Centre centre_panning
#define Panning_Calculate calc_panning

#ifdef __cplusplus
}
#endif
