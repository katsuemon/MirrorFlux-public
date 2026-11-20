// Minimal standalone types and sample sink for the BSD WonderSwan core.
#pragma once
#include <cstdint>
#include <algorithm>
using u8=uint8_t;using u16=uint16_t;using u32=uint32_t;using s8=int8_t;using s16=int16_t;using s32=int32_t;using offs_t=uint32_t;
#define BIT(v,b) (((v)>>(b))&1)
#define COMBINE_DATA(p) (*(p)=(*(p)&~mem_mask)|(data&mem_mask))
#define ACCESSING_BITS_0_7 (mem_mask&255)
#define ACCESSING_BITS_8_15 (mem_mask&65280)
#define LOGUNKNOWN(...) ((void)0)
#define LOGHYPERVOICE(...) ((void)0)
struct sound_stream {
 int32_t** output;int count;
 int samples() const {return count;}
 void put_int_clamp(int channel,int index,int value,int scale) {output[channel][index]=std::max(-32768,std::min(32767,int(int64_t(value)*32768/scale)));}
 void put_int(int channel,int index,int value,int scale) {output[channel][index]=int(int64_t(value)*32768/scale);}
 void put(int channel,int index,double value) {output[channel][index]=int(value*32768);}
};
