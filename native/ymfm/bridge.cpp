// MirrorFlux standalone YM2151 adapter. ymfm: BSD-3-Clause.
// No libvgm headers, player code or binary dependency.
#include <algorithm>
#include <cstdint>
#include <deque>
#include "upstream/ymfm_opm.h"
class Host : public ymfm::ymfm_interface {
public:
    double now = 0, timers[2] = {-1, -1}, busy = 0;
    void ymfm_set_timer(uint32_t n, int32_t clocks) override { timers[n] = clocks < 0 ? -1 : now + clocks; }
    void ymfm_set_busy_end(uint32_t clocks) override { busy = now + clocks; }
    bool ymfm_is_busy() override { return now < busy; }
    void advance(double clocks) {
        double end = now + clocks;
        for (;;) {
            int n = timers[0] >= 0 && (timers[1] < 0 || timers[0] <= timers[1]) ? 0 : 1;
            if (timers[n] < 0 || timers[n] > end) break;
            now = timers[n]; timers[n] = -1; m_engine->engine_timer_expired(n);
        }
        now = end;
    }
};
struct Opm {
    Host host;
    ymfm::ym2151 chip;
    struct Write { uint8_t reg, value; };
    std::deque<Write> pending;
    uint64_t sampleCounter = 0, keyReady[8] = {};
    double clocks, phase = 0, ratio;
    int32_t lastL = 0, lastR = 0, nextL = 0, nextR = 0;
    Opm(uint32_t clock, uint32_t rate) : chip(host), clocks(double(clock) / chip.sample_rate(clock)),
        ratio(double(chip.sample_rate(clock)) / rate) { chip.reset(); }
    void sample() {
        // Preserve same-frame key-off/key-on ordering across render callbacks.
        while (!pending.empty()) {
            const auto& w = pending.front(); int ch = w.reg == 8 ? w.value & 7 : -1;
            if (ch >= 0 && sampleCounter < keyReady[ch]) break;
            chip.write(0, w.reg); chip.write(1, w.value);
            if (ch >= 0) keyReady[ch] = sampleCounter + 1;
            pending.pop_front();
        }
        ymfm::ym2151::output_data output;
        chip.generate(&output); host.advance(clocks); ++sampleCounter;
        nextL = output.data[0]; nextR = output.data[1];
    }
};
#define API extern "C" __declspec(dllexport)
API void* mf_ymfm_opm_create(uint32_t clock, uint32_t rate) {
    try { return rate && clock ? new Opm(clock, rate) : nullptr; } catch (...) { return nullptr; }
}
API void mf_ymfm_opm_close(void* p) { delete static_cast<Opm*>(p); }
API int mf_ymfm_opm_write(void* p, uint8_t reg, uint8_t value) {
    try { static_cast<Opm*>(p)->pending.push_back({reg,value}); return 0; } catch (...) { return -1; }
}
API void mf_ymfm_opm_render(void* p, int32_t* output, uint32_t frames) {
    auto& d = *static_cast<Opm*>(p);
    for (uint32_t i = 0; i < frames; ++i) {
        while (d.phase >= 1) { d.lastL = d.nextL; d.lastR = d.nextR; d.sample(); d.phase -= 1; }
        output[2*i] = int32_t(d.lastL + (d.nextL-d.lastL)*d.phase);
        output[2*i+1] = int32_t(d.lastR + (d.nextR-d.lastR)*d.phase);
        d.phase += d.ratio;
    }
}
API void mf_ymfm_opm_phase_steps(void* p, uint32_t* steps) {
    if (!p || !steps) return;
    const auto& d = *static_cast<Opm*>(p);
    for (int i = 0; i < 8; ++i) steps[i] = d.host.mf_phase_step[i];
}
