#include "host.h"
#include "wonderswan/wswansound.h"
#include "gme/Nes_Fds_Apu.h"
#include <array>
#include <memory>
extern "C" const DEV_DEF mf_nes_core,mf_ws_core;
namespace {
struct Nes { DEV_DATA data{};void* apu=nullptr;Nes_Fds_Apu fds;Blip_Buffer buffer;std::array<uint8_t,65536> ram{};bool useFds=false;uint32_t mask=0; ~Nes(){if(apu)device_stop_nesapu(apu);} };
void nes_stop(void* p) { delete (Nes*)p; }
void nes_reset(void* p) { auto& n=*(Nes*)p;device_reset_nesapu(n.apu);nesapu_set_rom(n.apu,n.ram.data());n.fds.reset();n.buffer.clear(); }
UINT8 nes_start(const DEV_GEN_CFG* c,DEV_INFO* out) {
 try {auto n=std::make_unique<Nes>();n->useFds=c->flags&1;n->apu=device_start_nesapu(c->clock,c->smplRate);
 if(!n->apu || n->buffer.set_sample_rate(c->smplRate))return 1;
 n->buffer.clock_rate(c->clock);n->fds.osc_output(0,&n->buffer);n->data.chipInf=n.get();
 INIT_DEVINF(out,&n->data,c->smplRate,&mf_nes_core);n.release();return 0;}catch(...){return 1;}
}
void nes_write(void* p,UINT8 addr,UINT8 value) {auto& n=*(Nes*)p;
 if(addr<0x20)nes_apu_write(n.apu,addr,value);
 else if(n.useFds) {unsigned absolute=addr>=0x40?0x4000+addr:addr==0x3f?0x4023:0x4060+addr;if(absolute!=0x4023)n.fds.write(0,absolute,value);}
}
void nes_memory(void* p,UINT32 addr,UINT32 length,const UINT8* bytes) {auto& n=*(Nes*)p;if(addr>n.ram.size())return;length=std::min<UINT32>(length,UINT32(n.ram.size()-addr));std::copy_n(bytes,length,n.ram.begin()+addr);}
void nes_mask(void* p,UINT32 mask){auto& n=*(Nes*)p;n.mask=mask;nesapu_set_mute_mask(n.apu,mask);}
void nes_update(void* p,UINT32 count,DEV_SMPL** output){auto& n=*(Nes*)p;nes_apu_update(n.apu,count,output);
 if(n.useFds)for(UINT32 i=0;i<count;i++){auto clocks=n.buffer.count_clocks(1);n.fds.end_frame(clocks);n.buffer.end_frame(clocks);blip_sample_t sample=0;n.buffer.read_samples(&sample,1);if(!(n.mask&32)){output[0][i]+=sample;output[1][i]+=sample;}}
}
DEVDEF_RWFUNC nesFunctions[]={{RWF_REGISTER,DEVRW_A8D8,0,(void*)nes_write},{RWF_MEMORY,DEVRW_BLOCK,0,(void*)nes_memory},{0,0,0,nullptr}};
struct Swan {DEV_DATA data{};wswan_sound_device core;};
UINT8 ws_start(const DEV_GEN_CFG* c,DEV_INFO* out){try{auto n=std::make_unique<Swan>();n->data.chipInf=n.get();INIT_DEVINF(out,&n->data,c->clock/128,&mf_ws_core);n.release();return 0;}catch(...){return 1;}}
void ws_stop(void* p){delete (Swan*)p;}
void ws_reset(void* p){auto& s=((Swan*)p)->core;s.device_reset();s.m_headphone_enable=1;s.m_sample_volume=15;}
void ws_write(void* p,UINT8 address,UINT8 value){auto& s=((Swan*)p)->core;unsigned a=address+0x80;s.port_w(a/2,uint16_t(value)<<((a&1)*8),uint16_t(255)<<((a&1)*8));}
void ws_memory(void* p,UINT32 offset,UINT32 length,const UINT8* bytes){auto& s=((Swan*)p)->core;for(UINT32 i=0;i<length;i++)s.memory[(offset+i)&16383]=bytes[i];}
void ws_mask(void* p,UINT32 mask){((Swan*)p)->core.muteMask=mask;}
void ws_update(void* p,UINT32 count,DEV_SMPL** out){sound_stream stream{out,int(count)};((Swan*)p)->core.sound_stream_update(stream);}
DEVDEF_RWFUNC wsFunctions[]={{RWF_REGISTER,DEVRW_A8D8,0,(void*)ws_write},{RWF_MEMORY,DEVRW_BLOCK,0,(void*)ws_memory},{0,0,0,nullptr}};
}
extern "C" const DEV_DEF mf_nes_core={"NES + FDS","MAME / GME",0,nes_start,nes_stop,nes_reset,nes_update,nullptr,nes_mask,nullptr,nullptr,nullptr,nullptr,nesFunctions};
extern "C" const DEV_DEF mf_ws_core={"WonderSwan","MAME",0,ws_start,ws_stop,ws_reset,ws_update,nullptr,ws_mask,nullptr,nullptr,nullptr,nullptr,wsFunctions};
