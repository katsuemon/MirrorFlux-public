#include "../host.h"
/***************************************************************************
 * Gens: PWM audio emulator.                                               *
 *                                                                         *
 * Copyright (c) 1999-2002 by Stéphane Dallongeville                       *
 * Copyright (c) 2003-2004 by Stéphane Akhoun                              *
 * Copyright (c) 2008-2009 by David Korth                                  *
 *                                                                         *
 * This program is free software; you can redistribute it and/or modify it *
 * under the terms of the GNU General Public License as published by the   *
 * Free Software Foundation; either version 2 of the License, or (at your  *
 * option) any later version.                                              *
 *                                                                         *
 * This program is distributed in the hope that it will be useful, but     *
 * WITHOUT ANY WARRANTY; without even the implied warranty of              *
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the           *
 * GNU General Public License for more details.                            *
 *                                                                         *
 * You should have received a copy of the GNU General Public License along *
 * with this program; if not, write to the Free Software Foundation, Inc., *
 * 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.           *
 ***************************************************************************/

#include <stdlib.h>
#include <string.h>









typedef struct _pwm_chip pwm_chip;


static void PWM_Init(void* info);
static void PWM_Recalc_Scale(pwm_chip* chip);

static void PWM_Set_Cycle(pwm_chip* chip, UINT16 cycle);
static void PWM_Set_Int(pwm_chip* chip, UINT8 int_time);

static void PWM_Update(void* info, UINT32 length, DEV_SMPL **buf);

static UINT8 device_start_pwm(const DEV_GEN_CFG* cfg, DEV_INFO* retDevInf);
static void device_stop_pwm(void* info);

static void pwm_chn_w(void* info, UINT8 Channel, UINT16 data);

static void pwm_set_mute_mask(void *info, UINT32 MuteMask);


static DEVDEF_RWFUNC devFunc[] =
{
	{RWF_REGISTER | RWF_WRITE, DEVRW_A8D16, 0, pwm_chn_w},
	{RWF_CHN_MUTE | RWF_WRITE, DEVRW_ALL, 0, pwm_set_mute_mask},
	{0x00, 0x00, 0, NULL}
};
static DEV_DEF devDef =
{
	"PWM", "Gens/GS", FCC_GENS,

	device_start_pwm,
	device_stop_pwm,
	PWM_Init,
	PWM_Update,

	NULL,	// SetOptionBits
	pwm_set_mute_mask,
	NULL,	// SetPanning
	NULL,	// SetSampleRateChangeCallback
	NULL,	// SetLoggingCallback
	NULL,	// LinkDevice

	devFunc,	// rwFuncs
};

static const char* DeviceName(const DEV_GEN_CFG* devCfg)
{
	return "32X PWM";
}

static UINT16 DeviceChannels(const DEV_GEN_CFG* devCfg)
{
	return 2;
}

static const char** DeviceChannelNames(const DEV_GEN_CFG* devCfg)
{
	return NULL;
}

static const DEVLINK_IDS* DeviceLinkIDs(const DEV_GEN_CFG* devCfg)
{
	return NULL;
}

const DEV_DECL sndDev_32X_PWM =
{
	DEVID_32X_PWM,
	DeviceName,
	DeviceChannels,
	DeviceChannelNames,
	DeviceLinkIDs,
	{	// cores
		&devDef,
		NULL
	}
};


struct _pwm_chip
{
	DEV_DATA _devData;

	UINT16 PWM_Cycle;
	UINT8 PWM_Int;
	UINT8 PWM_Int_Cnt;
	UINT8 PWM_Mode;
	UINT8 PWM_Mute;
	UINT16 PWM_Out_R;
	UINT16 PWM_Out_L;
	INT16 mf_history[2][512];
	UINT16 mf_cursor, mf_count, mf_peak[2];
	UINT8 mf_seen[2];

	// PWM scaling variables.
	INT32 PWM_Offset;
	INT32 PWM_Scale;
#define PWM_Loudness	0
	//UINT8 PWM_Loudness;

	UINT32 clock;
};


/**
 * PWM_Init(): Initialize the PWM audio emulator.
 */
static void PWM_Init(void* info)
{
	pwm_chip* chip = (pwm_chip*)info;

	chip->PWM_Mode = 0;
	chip->PWM_Out_R = 0;
	chip->PWM_Out_L = 0;

	//PWM_Loudness = 0;
	PWM_Set_Cycle(chip, 0);
	PWM_Set_Int(chip, 0);
}


static void PWM_Recalc_Scale(pwm_chip* chip)
{
	chip->PWM_Offset = (chip->PWM_Cycle / 2) + 1;
	chip->PWM_Scale = 0x7FFF00 / chip->PWM_Offset;
}


static void PWM_Set_Cycle(pwm_chip* chip, UINT16 cycle)
{
	cycle--;
	chip->PWM_Cycle = (cycle & 0xFFF);

	PWM_Recalc_Scale(chip);
}


static void PWM_Set_Int(pwm_chip* chip, UINT8 int_time)
{
	int_time &= 0x0F;
	if (int_time)
		chip->PWM_Int = chip->PWM_Int_Cnt = int_time;
	else
		chip->PWM_Int = chip->PWM_Int_Cnt = 16;
}


INLINE DEV_SMPL PWM_Update_Scale(pwm_chip* chip, INT16 PWM_In)
{
	if (PWM_In == 0)
		return 0;

	// TODO: Chilly Willy's new scaling algorithm breaks drx's Sonic 1 32X (with PWM drums).
	//return (((PWM_In & 0xFFF) - chip->PWM_Offset) * chip->PWM_Scale) >> (8 - PWM_Loudness);

	// Knuckles' Chaotix: Tachy Touch uses the values 0xF?? for negative values
	// This small modification fixes the terrible pops.
	PWM_In &= 0xFFF;
	if (PWM_In & 0x800)
		PWM_In |= ~0xFFF;
	return ((PWM_In - chip->PWM_Offset) * chip->PWM_Scale) >> (8 - PWM_Loudness);
}


static void PWM_Update(void* info, UINT32 length, DEV_SMPL **buf)
{
	pwm_chip* chip = (pwm_chip*)info;
	DEV_SMPL tmpOutL;
	DEV_SMPL tmpOutR;
	UINT32 i;



	// New PWM scaling algorithm provided by Chilly Willy on the Sonic Retro forums.
	tmpOutL = PWM_Update_Scale(chip, (INT16)chip->PWM_Out_L);
	tmpOutR = PWM_Update_Scale(chip, (INT16)chip->PWM_Out_R);

	for (i = 0; i < length; i ++)
	{
		DEV_SMPL sample[2];
		UINT8 channel;
		sample[0] = tmpOutL;
		sample[1] = tmpOutR;
		for (channel = 0; channel < 2; channel++) {
			INT32 value = sample[channel];
			UINT16 magnitude = (UINT16)(value < 0 ? -value : value);
			if (magnitude > chip->mf_peak[channel]) chip->mf_peak[channel] = magnitude;
			chip->mf_history[channel][chip->mf_cursor] = (INT16)value;
			buf[channel][i] = (chip->PWM_Mute & (1 << channel)) ? 0 : value;
		}
		chip->mf_cursor = (chip->mf_cursor + 1) & 511;
		if (chip->mf_count < 512) chip->mf_count++;
	}

	return;
}


static UINT8 device_start_pwm(const DEV_GEN_CFG* cfg, DEV_INFO* retDevInf)
{
	pwm_chip *chip;
	UINT32 rate;

	chip = (pwm_chip *)calloc(1, sizeof(pwm_chip));
	if (chip == NULL)
		return 0xFF;

	chip->clock = cfg->clock;

	rate = 22020;	// that's the rate the PWM uses in most games
	SRATE_CUSTOM_HIGHEST(cfg->srMode, rate, cfg->smplRate);

	//PWM_Init(chip);
	pwm_set_mute_mask(chip, 0x00);

	chip->_devData.chipInf = chip;
	INIT_DEVINF(retDevInf, &chip->_devData, rate, &devDef);
	return 0x00;
}

static void device_stop_pwm(void* info)
{
	pwm_chip* chip = (pwm_chip*)info;
	free(chip);

	return;
}

static void pwm_chn_w(void* info, UINT8 Channel, UINT16 data)
{
	pwm_chip* chip = (pwm_chip*)info;

	switch(Channel)
	{
	case 0x00/2:	// control register
		PWM_Set_Int(chip, data >> 8);
		break;
	case 0x02/2:	// cycle register
		PWM_Set_Cycle(chip, data);
		break;
	case 0x04/2:	// l ch
		chip->PWM_Out_L = data;
		chip->mf_seen[0] = 1;
		break;
	case 0x06/2:	// r ch
		chip->PWM_Out_R = data;
		chip->mf_seen[1] = 1;
		if (! chip->PWM_Mode)
		{
			if (chip->PWM_Out_L == chip->PWM_Out_R)
			{
				// fixes these terrible pops when
				// starting/stopping/pausing the song
				chip->PWM_Offset = data;
				chip->PWM_Mode = 0x01;
			}
		}
		break;
	case 0x08/2:	// mono ch
		chip->PWM_Out_L = data;
		chip->PWM_Out_R = data;
		chip->mf_seen[0] = chip->mf_seen[1] = 1;
		if (! chip->PWM_Mode)
		{
			chip->PWM_Offset = data;
			chip->PWM_Mode = 0x01;
		}
		break;
	}

	return;
}

static void pwm_set_mute_mask(void *info, UINT32 MuteMask)
{
	pwm_chip* chip = (pwm_chip*)info;

	chip->PWM_Mute = MuteMask & 0x03;

	return;
}

void mf_pwm_voice(void* device, int channel, float* level, int* flags)
{
    pwm_chip* chip = (pwm_chip*)device;
    *level = chip->mf_peak[channel] / 32768.0f;
    *flags = (chip->mf_peak[channel] > 16 ? 1 : 0) |
        (chip->mf_seen[channel] ? 8 : 0);
    chip->mf_peak[channel] = 0;
}

int mf_pwm_wave(void* device, int channel, double* output, int count)
{
    pwm_chip* chip = (pwm_chip*)device;
    int i;
    if (channel < 0 || channel > 1 || count > chip->mf_count) return 0;
    for (i = 0; i < count; i++)
        output[i] = chip->mf_history[channel][(chip->mf_cursor - count + i) & 511] / 32768.0;
    return 22020;
}
