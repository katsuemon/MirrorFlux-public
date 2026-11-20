// SCSP DSP interface declarations; BSD-3-Clause core by ElSemi and R. Belmont.
// Upstream attribution: https://github.com/mamedev/mame (src/devices/sound/scspdsp.h).
#pragma once
typedef struct _SCSPDSP {
 UINT16* SCSPRAM; UINT32 SCSPRAM_LENGTH,RBP,RBL;
 INT16 COEF[64];UINT16 MADRS[32],MPRO[512];
 INT32 TEMP[128],MEMS[32];UINT32 DEC;
 INT32 MIXS[16];INT16 EXTS[2],EFREG[16];
 int Stopped,LastStep;
} SCSPDSP;
void SCSPDSP_Init(SCSPDSP*);
void SCSPDSP_SetSample(SCSPDSP*,INT32,INT32,INT32);
void SCSPDSP_Step(SCSPDSP*);
void SCSPDSP_Start(SCSPDSP*);
