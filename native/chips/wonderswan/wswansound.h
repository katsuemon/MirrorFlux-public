// license:BSD-3-Clause
// copyright-holders:Wilbert Pol
#ifndef MAME_SHARED_WSWANSOUND_H
#define MAME_SHARED_WSWANSOUND_H

#pragma once

#include "environment.h"


//**************************************************************************
//  TYPE DEFINITIONS
//**************************************************************************

// ======================> wswan_sound_device

class wswan_sound_device
{
public:
	wswan_sound_device() { device_reset(); }
 uint8_t memory[16384] = {};
 uint32_t muteMask=0;
 uint16_t read_word(uint32_t address) { return memory[address&16383] | (uint16_t(memory[(address+1)&16383])<<8); }

	void set_headphone_connected(bool headphone_connected) { m_headphone_connected = headphone_connected; }

	void hypervoice_dma_w(u8 data);

	void hypervoice_w(offs_t offset, u16 data, u16 mem_mask);
	u16 hypervoice_r(offs_t offset, u16 mem_mask);

	void port_w(offs_t offset, u16 data, u16 mem_mask);
	u16 port_r(offs_t offset, u16 mem_mask);

public:
	// device-levels


	void device_reset();



	// sound stream updates
	void sound_stream_update(sound_stream &stream);

public:
	struct channel_t
	{
		channel_t() :
		freq(0),
		period(2048),
		pos(0),
		vol_left(0),
		vol_right(0),
		on(false),
		offset(0),
		signal(0) { }

		u16  freq;
		u16  period;
		u32  pos;
		u8   vol_left;
		u8   vol_right;
		bool on;
		u8   offset;
		u8   signal;
	};

	struct hypervoice_t
	{
		hypervoice_t() :
		loutput(0),
		routput(0),
		linput(0),
		rinput(0),
		input_channel(false),
		volume(0),
		scale_mode(0),
		div(0),
		counter(0),
		enable(false),
		channel_mode(0) {}

		void stereo_input(u8 input);
		s32 scale(u8 input);

		s32  loutput;
		s32  routput;
		u8   linput;
		u8   rinput;
		bool input_channel;
		u8   volume;
		u8   scale_mode;
		u8   div;
		u8   counter;
		bool enable;
		u8   channel_mode;
	} m_hypervoice;

	u8 fetch_sample(int channel, int offset);


	channel_t m_audio[4];
	s8        m_sweep_step = 0;
	u32       m_sweep_time = 8192;
	u32       m_sweep_count = 0;
	u8        m_noise_type = 0;
	u8        m_noise_enable = 0;
	u8        m_noise_output = 0;
	u16       m_sample_address = 0;
	u8        m_audio2_voice = 0;
	u8        m_audio3_sweep = 0;
	u8        m_audio4_noise = 0;
	u8        m_speaker_enable = 0;
	u8        m_speaker_volume = 0;
	u8        m_headphone_enable = 0;
	u8        m_headphone_connected = 1;
	u16       m_noise_shift = 0;
	u8        m_sample_volume = 0;
	u8        m_system_volume = 0;
	u16       m_loutput = 0;
	u16       m_routput = 0;
};



#endif // MAME_SHARED_WSWANSOUND_H
