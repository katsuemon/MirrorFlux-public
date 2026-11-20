// MirrorFlux independent adapter for cam900's zlib-licensed vgsound_emu ES5505/ES5506 cores.
#include "host.h"

#include "vgsound_emu/es550x/es5505.hpp"
#include "vgsound_emu/es550x/es5506.hpp"
#include <cstring>
#include <unordered_map>

extern "C" const DEV_DEF mf_es550x_core;

namespace {
struct SampleMemory : es550x_intf {
    // External banks can be sparsely populated by VGM data blocks.
    static constexpr size_t pageSize = 4096;
    using Page = std::array<uint8_t, pageSize>;
    struct Region { size_t size = 0; std::unordered_map<size_t, Page> pages; };
    std::array<Region, 4> rom;
    std::array<uint64_t, 32> bank{};
    uint32_t declaredSize = 0;
    s16 read_voice_sample(u8 voice, u8 region, u32 address) override {
        const auto& memory = rom[region & 3];
        uint64_t pos = (bank[voice] + address) * 2;
        if (pos + 1 >= memory.size) return 0;
        auto page = memory.pages.find(size_t(pos / pageSize));
        if (page == memory.pages.end()) return 0;
        size_t inPage = pos % pageSize;
        return static_cast<int16_t>(page->second[inPage] | (page->second[inPage + 1] << 8));
    }
};
struct Device {
    DEV_DATA header{};
    SampleMemory memory;
    std::unique_ptr<es5505_core> otis;
    std::unique_ptr<es5506_core> otto;
    uint32_t rate, clock, mask = 0, slots = 0;
    int32_t left = 0, right = 0;
    explicit Device(const DEV_GEN_CFG& cfg) : rate(std::max(1u, cfg.clock / 512)), clock(cfg.clock) {
        if (cfg.flags & 0x80) otto.reset(new es5506_core(memory));
        else otis.reset(new es5505_core(memory));
        header.chipInf = this;
    }
};
Device& device(void* data) { return *static_cast<Device*>(static_cast<DEV_DATA*>(data)->chipInf); }
void reset(void* data) {
    auto& d = device(data);
    d.memory.bank.fill(0); d.slots = 0; d.left = d.right = 0;
    if (d.otto) d.otto->reset(); else d.otis->reset();
    // Reset leaves all voice control registers stopped until the score keys them on.
    for (int voice = 0; voice < 32; voice++) {
        if (d.otto) d.otto->regs_w(voice, 0, 3);
        else d.otis->regs_w(voice, 0, 3);
        bool muted = ((d.mask >> voice) & 1) != 0;
        if (d.otto) d.otto->set_mute(voice, muted); else d.otis->set_mute(voice, muted);
    }
}
UINT8 start(const DEV_GEN_CFG* cfg, DEV_INFO* info) {
    try {
        std::unique_ptr<Device> d(new Device(*cfg));
        reset(&d->header);
        INIT_DEVINF(info, &d->header, d->rate, &mf_es550x_core);
        d.release(); return 0;
    } catch (...) { return 0xff; }
}
void stop(void* data) { delete &device(data); }
void write8(void* data, UINT8 offset, UINT8 value) {
    auto& d = device(data);
    if (offset >= 0x40) { d.memory.bank[offset & 31] = uint64_t(value) << 20; return; }
    if (d.otto) d.otto->write(offset, value);
    else {
        int shift = (offset & 1) ? 0 : 8;
        uint16_t old = d.otis->read(offset >> 1, false);
        d.otis->write(offset >> 1, (old & ~(0xffu << shift)) | (uint16_t(value) << shift));
    }
}
void write16(void* data, UINT8 offset, UINT16 value) {
    auto& d = device(data);
    if (offset >= 0x40) { d.memory.bank[offset & 31] = uint64_t(value) << 20; return; }
    if (d.otis) d.otis->write(offset >> 1, value);
    else { write8(data, offset & ~1, value >> 8); write8(data, offset | 1, value & 0xff); }
}
void romSize(void* data, UINT32 size) { device(data).memory.declaredSize = size; }
void romWrite(void* data, UINT32 address, UINT32 length, const UINT8* source) {
    auto& mem = device(data).memory;
    auto& bank = mem.rom[(address >> 28) & 3];
    bool highOnly = (address >> 31) != 0;
    size_t offset = address & 0x0fffffff;
    if (offset >= mem.declaredSize) return;
    length = std::min<uint32_t>(length, mem.declaredSize - offset);
    const size_t stride = highOnly ? 2 : 1;
    const size_t imageSize = size_t(mem.declaredSize) * stride;
    if (bank.size != imageSize) { bank.pages.clear(); bank.size = imageSize; }
    offset *= stride;
    while (length) {
        size_t inPage = offset % SampleMemory::pageSize;
        auto& page = bank.pages[offset / SampleMemory::pageSize];
        size_t count = std::min<size_t>(length, (page.size() - inPage) / stride);
        if (highOnly) {
            for (size_t i = 0; i < count; i++) {
                page[inPage + i * 2] = 0;
                page[inPage + i * 2 + 1] = source[i];
            }
        } else std::memcpy(page.data() + inPage, source, count);
        offset += count * stride; source += count; length -= static_cast<UINT32>(count);
    }
}
void mute(void* data, UINT32 mask) {
    auto& d = device(data);
    d.mask = mask;
    for (int i = 0; i < 32; i++) {
        bool muted = (mask & (uint32_t(1) << i)) != 0;
        if (d.otto) d.otto->set_mute(i, muted); else d.otis->set_mute(i, muted);
    }
}
template<class Core> void mix(Device& d, Core& core, int shift) {
    int left = 0, right = 0;
    for (int i = 0; i < core.enabled_voices(); i++) {
        left += core.voice_lout(i); right += core.voice_rout(i);
    }
    d.left = std::max(-32768, std::min(32767, left >> shift));
    d.right = std::max(-32768, std::min(32767, right >> shift));
}
void update(void* data, UINT32 frames, DEV_SMPL** out) {
    auto& d = device(data);
    for (UINT32 i = 0; i < frames; i++) {
        // 16 master clocks per voice slot. The standalone host resamples the resulting stereo stream.
        if (d.otto) {
            d.slots += 32;
            auto voices = d.otto->enabled_voices();
            while (d.slots >= voices) { d.otto->tick_perf(); d.slots -= voices; }
            mix(d, *d.otto, 4);
        } else {
            for (int slot = 0; slot < 32; slot++) d.otis->tick_perf();
            mix(d, *d.otis, 0);
        }
        out[0][i] = d.left; out[1][i] = d.right;
    }
}
DEVDEF_RWFUNC funcs[] = {
    {RWF_REGISTER|RWF_WRITE, DEVRW_A8D8, 0, (void*)write8},
    {RWF_REGISTER|RWF_WRITE, DEVRW_A8D16, 0, (void*)write16},
    {RWF_MEMORY|RWF_WRITE, DEVRW_MEMSIZE, 0, (void*)romSize},
    {RWF_MEMORY|RWF_WRITE, DEVRW_BLOCK, 0, (void*)romWrite},
    {RWF_CHN_MUTE|RWF_WRITE, DEVRW_ALL, 0, (void*)mute},
    {0,0,0,nullptr}
};
template<class Core> void voice(Core& core, int index, double fraction, double volumeMax, double& ratio,
                                float& left, float& right, int& flags) {
    ratio = core.pitch(index) / fraction;
    left = float(core.left_volume(index) / volumeMax);
    right = float(core.right_volume(index) / volumeMax);
    flags = index < core.enabled_voices() && core.playing(index) ? 1 : 0;
}
}
extern "C" const DEV_DEF mf_es550x_core = {
    "ES550x", "vgsound_emu", 0x56475345, start, stop, reset, update,
    nullptr, mute, nullptr, nullptr, nullptr, nullptr, funcs
};
extern "C" void mf_es550x_voice(void* data, int i, double& frequency, double& ratio, float& left,
                                float& right, float& level, int& flags) {
    auto& d = device(data);
    if (d.otto) voice(*d.otto, i, 2048, 65535, ratio, left, right, flags);
    else voice(*d.otis, i, 512, 255, ratio, left, right, flags);
    auto voices = d.otto ? d.otto->enabled_voices() : d.otis->enabled_voices();
    frequency = ratio * d.clock / (16.0 * voices);
    level = flags ? std::max(left,right) : 0;
}
