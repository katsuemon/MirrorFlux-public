// MirrorFlux chip adapter: no libvgm types or playback implementation.
// Chip implementations are ymfm, BSD-3-Clause; see upstream/LICENSE.
#include <algorithm>
#include <array>
#include <cstdint>
#include <deque>
#include <memory>
#include <vector>
#include <type_traits>
#include "upstream/ymfm_opm.h"
#include "upstream/ymfm_opn.h"
#include "upstream/ymfm_opl.h"
namespace mf {
struct Environment : ymfm::ymfm_interface {
    std::array<std::vector<uint8_t>,ymfm::ACCESS_CLASSES> memory;
    uint64_t time = 0, busyUntil = 0;
    int64_t deadlines[2] = {-1,-1};
    void ymfm_set_timer(uint32_t timer, int32_t cycles) override {
        if (timer < 2) deadlines[timer] = cycles < 0 ? -1 : int64_t(time + cycles);
    }
    void ymfm_set_busy_end(uint32_t cycles) override { busyUntil = time + cycles; }
    bool ymfm_is_busy() override { return time < busyUntil; }
    uint8_t ymfm_external_read(ymfm::access_class bank, uint32_t address) override {
        const auto& bytes = memory[bank]; return address < bytes.size() ? bytes[address] : 0xff;
    }
    void ymfm_external_write(ymfm::access_class bank, uint32_t address, uint8_t value) override {
        auto& bytes = memory[bank]; if (address < bytes.size()) bytes[address] = value;
    }
    void tick(uint32_t cycles) {
        const uint64_t target = time + cycles;
        for (;;) {
            int next = -1;
            for (int i = 0; i != 2; ++i)
                if (deadlines[i] >= 0 && uint64_t(deadlines[i]) <= target &&
                    (next < 0 || deadlines[i] < deadlines[next])) next = i;
            if (next < 0) break;
            time = uint64_t(deadlines[next]); deadlines[next] = -1;
            m_engine->engine_timer_expired(next);
        }
        time = target;
    }
};
struct AudioChip {
    Environment host;
    double fmGain = 1, ssgGain = 1;
    virtual ~AudioChip() = default;
    virtual bool write(uint8_t port,uint8_t reg,uint8_t value) = 0;
    virtual void render(int32_t* output,uint32_t frames) = 0;
    virtual bool memory(uint32_t bank,uint32_t size,uint32_t offset,const uint8_t* data,uint32_t length)=0;
};
template<class Core,int Layout> struct Synth final : AudioChip {
    Core core;
    struct Register { uint8_t port=0,reg=0,value=0;uint32_t size=0,offset=0;std::vector<uint8_t> bytes; };
    std::deque<Register> queue;
    std::array<uint64_t,32> keyDeadline{};
    uint64_t ticks = 0;
    uint32_t nativeRate, outputRate, cycles;
    uint64_t phase = 0;
    int32_t previous[2] = {}, current[2] = {};
    Synth(uint32_t clock,uint32_t rate) : core(host),nativeRate(core.sample_rate(clock)),outputRate(rate),
        cycles(clock / nativeRate) {
        if (std::is_same<Core,ymfm::ym2608>::value) host.memory[ymfm::ACCESS_ADPCM_B].resize(0x40000,0xff);
        if (std::is_same<Core,ymfm::ymf278b>::value) host.memory[ymfm::ACCESS_PCM].resize(0x400000,0xff);
        core.reset();
    }
    static int key(const Register& r) {
        if (std::is_base_of<ymfm::ym2151,Core>::value) return r.reg == 8 ? r.value & 7 : -1;
        if (std::is_same<Core,ymfm::ym2203>::value || std::is_same<Core,ymfm::ym2608>::value ||
            std::is_base_of<ymfm::ym2610,Core>::value || std::is_base_of<ymfm::ym2612,Core>::value)
            return r.port == 0 && r.reg == 0x28 ? r.value & 7 : -1;
        if (std::is_base_of<ymfm::opll_base,Core>::value)
            return r.reg >= 0x20 && r.reg <= 0x28 ? r.reg - 0x20 : r.reg == 0x0e ? 31 : -1;
        return r.reg >= 0xb0 && r.reg <= 0xb8 ? r.port * 9 + r.reg - 0xb0 : r.reg == 0xbd ? 31 : -1;
    }
    bool write(uint8_t port,uint8_t reg,uint8_t value) override {
        if (port > (std::is_same<Core,ymfm::ymf278b>::value ? 2 : 1) || queue.size() >= 1048576) return false;
        queue.push_back({port,reg,value}); return true;
    }
    bool memory(uint32_t bank,uint32_t size,uint32_t offset,const uint8_t* bytes,uint32_t length) override {
        if(queue.size()>=1048576)return false;
        Register event;event.port=uint8_t(bank+0x80);event.size=size;event.offset=offset;
        if(length)event.bytes.assign(bytes,bytes+length);
        queue.push_back(std::move(event));return true;
    }
    void step() {
        constexpr uint32_t spacing = std::is_same<Core,ymfm::ym2203>::value || std::is_same<Core,ymfm::ym2608>::value ? 18 :
            std::is_base_of<ymfm::ym2610,Core>::value ? 9 : 1;
        while (!queue.empty()) {
            const auto& r = queue.front();
            if(r.port>=0x80) {
                auto& bytes=host.memory[r.port-0x80];if(bytes.size()<r.size)bytes.resize(r.size,0xff);
                std::copy(r.bytes.begin(),r.bytes.end(),bytes.begin()+r.offset);queue.pop_front();continue;
            }
            if(std::is_same<Core,ymfm::ym2608>::value && r.port==0 && r.reg==0x10 && host.memory[ymfm::ACCESS_ADPCM_A].empty()) {
                queue.pop_front();continue;
            }
            int channel = key(r);
            if (channel >= 0 && ticks < keyDeadline[channel]) break;
            core.write(r.port * 2,r.reg); core.write(r.port * 2 + 1,r.value);
            if (channel >= 0) keyDeadline[channel] = ticks + spacing;
            queue.pop_front();
        }
        typename Core::output_data bus;
        core.generate(&bus); host.tick(cycles); ++ticks;
        double left = bus.data[0] * fmGain, right = left;
        if (Layout == 0) {
            for (unsigned i = 1; i < Core::OUTPUTS; ++i) left += bus.data[i] * (std::is_same<Core,ymfm::ym2203>::value ? ssgGain : fmGain);
            right = left;
        } else if (Layout == 3) {
            left += bus.data[2] * ssgGain;
            right = bus.data[1] * fmGain + bus.data[2] * ssgGain;
        } else if (Layout == 6) {
            left=(bus.data[0]+bus.data[2]+bus.data[4])*fmGain;
            right=(bus.data[1]+bus.data[3]+bus.data[5])*fmGain;
        } else if (Layout == 4) {
            left += bus.data[2] * fmGain;
            right = (bus.data[1] + bus.data[3]) * fmGain;
        } else right = bus.data[1] * fmGain;
        current[0] = int32_t(left); current[1] = int32_t(right);
    }
    void render(int32_t* output,uint32_t frames) override {
        // Integrate each source sample's overlap with an output frame when reducing
        // the rate. Point sampling aliases the high-frequency ADPCM content.
        // phase is an integer remainder, so callback boundaries cannot change sound.
        if (nativeRate >= outputRate) {
            for (uint32_t i = 0; i < frames; ++i) {
                uint64_t remaining = nativeRate;
                int64_t sum[2] = {};
                while (remaining) {
                    if (!phase) { step(); phase = outputRate; }
                    uint64_t part = std::min(phase,remaining);
                    for (int c = 0; c < 2; ++c) sum[c] += int64_t(current[c]) * part;
                    phase -= part; remaining -= part;
                }
                for (int c = 0; c < 2; ++c) output[2*i+c] = int32_t(sum[c] / nativeRate);
            }
        } else {
            for (uint32_t i = 0; i < frames; ++i) {
                while (phase >= outputRate) {
                    std::copy_n(current,2,previous); step(); phase -= outputRate;
                }
                for (int c = 0; c < 2; ++c)
                    output[2*i+c] = int32_t(previous[c] + (double(current[c])-previous[c]) * phase / outputRate);
                phase += nativeRate;
            }
        }
    }
};
}
#define API extern "C" __declspec(dllexport)
API void* mf_ymfm_chip_create(uint32_t type,uint32_t variant,uint32_t clock,uint32_t rate) {
    if (clock < 100000 || clock > 100000000 || rate < 8000 || rate > 192000) return nullptr;
    try {
        using namespace mf;
        switch (type) {
        case 1: return variant ? static_cast<AudioChip*>(new Synth<ymfm::ds1001,0>(clock,rate)) : new Synth<ymfm::ym2413,0>(clock,rate);
        case 2: return variant ? static_cast<AudioChip*>(new Synth<ymfm::ym3438,2>(clock,rate)) : new Synth<ymfm::ym2612,2>(clock,rate);
        case 3: return new Synth<ymfm::ym2151,2>(clock,rate);
        case 6: return new Synth<ymfm::ym2203,0>(clock,rate);
        case 7: return new Synth<ymfm::ym2608,3>(clock,rate);
        case 8: return variant ? static_cast<AudioChip*>(new Synth<ymfm::ym2610b,3>(clock,rate)) : new Synth<ymfm::ym2610,3>(clock,rate);
        case 9: return new Synth<ymfm::ym3812,0>(clock,rate);
        case 10: return new Synth<ymfm::ym3526,0>(clock,rate);
        case 11: return new Synth<ymfm::y8950,0>(clock,rate);
        case 12: return new Synth<ymfm::ymf262,4>(clock,rate);
        case 13: return new Synth<ymfm::ymf278b,6>(clock,rate);
        default: return nullptr;
        }
    } catch (...) { return nullptr; }
}
API void mf_ymfm_chip_close(void* p) { delete static_cast<mf::AudioChip*>(p); }
API int mf_ymfm_chip_write(void* p,uint8_t port,uint8_t reg,uint8_t value) {
    try { return p && static_cast<mf::AudioChip*>(p)->write(port,reg,value) ? 0 : -1; } catch (...) { return -1; }
}
API void mf_ymfm_chip_render(void* p,int32_t* output,uint32_t frames) { static_cast<mf::AudioChip*>(p)->render(output,frames); }
API void mf_ymfm_chip_mix(void* p,double fm,double ssg) {
    auto* chip = static_cast<mf::AudioChip*>(p); chip->fmGain = fm; chip->ssgGain = ssg;
}
API void mf_ymfm_chip_mask(void* p,uint32_t mask) { static_cast<mf::AudioChip*>(p)->host.mf_mute_mask = mask; }
API int mf_ymfm_chip_memory(void* p,uint32_t bank,uint32_t size,uint32_t offset,const uint8_t* bytes,uint32_t length) {
    if (!p || bank < 1 || bank > 3 || size > 64*1024*1024 || offset > size || length > size-offset) return -1;
    try {
        return static_cast<mf::AudioChip*>(p)->memory(bank,size,offset,bytes,length)?0:-1;
    } catch (...) { return -1; }
}
API uint32_t mf_ymfm_chip_phase_steps(void* p,uint32_t* steps,uint32_t count) {
    if (!p) return 0;
    const auto& host = static_cast<mf::AudioChip*>(p)->host;
    if (steps && count) {
        uint32_t n = std::min<uint32_t>(count,32);
        for (uint32_t i = 0; i < n; ++i) steps[i] = host.mf_phase_step[i];
    }
    return host.mf_fm_div;
}
