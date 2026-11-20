MirrorFlux standalone chip host

The application owns VGM decoding, scheduling, stream control and mixing.
This DLL exposes register/memory/render/mute/voice operations for individual
licensed chip implementations. It contains no libvgm player or runtime framework.
The compatible host declarations, rational clock/pan helpers and sample-rate
converter are implemented here; this is not a formal clean-room claim.

Sources: provenance.json records exact upstream revisions and original hashes.
cores/: BSD-3-Clause MAME-derived chips; Mikey MIT; NES, MSM5232, PWM and VSU
GPL-2.0-or-later. The combined DLL includes GPL code, not a BSD-only library.
wonderswan/: BSD-3-Clause MAME, framework removed, memory/panning adapter added.
qsound/: BSD-3-Clause ValleyBell/Ian Karlsson HLE, preserving all 19 voices.
gme/: LGPL-2.1-or-later GME FDS/Blip_Buffer, adapted only to this host.
vgsound_emu/: zlib ES5505/5506, with the original license retained.
Per-file copyright notices and LICENSES/ must accompany redistribution.
Application source licensing must be made compatible before public release.
No external ROM, recorded waveform or user music is included.

Local changes: headers use host.h; standalone lifecycle, register and memory
bridges; integer-phase resampling; per-voice inspection/mute hooks; linked T6W28;
MultiPCM bank helper; QSound bounds/return fixes and masks; FDS/WS adapters.
All adapted sources required to rebuild are included. Coefficients and synthesis
constants are part of the licensed chip algorithms, not separate ROM payloads.

Build: LLVM-MinGW 20261020 UCRT x86_64, clang 23.1.4.
Run Build.ps1 -Compiler <path-to-clang.exe> (clang++.exe in the same directory).
No network, libvgm checkout, SDK or user sound data is required.
-OutputDirectory overrides the default Assets/Chips in the repository or ./build
when this source archive is extracted by itself. The build also packages source.
A rebuilt mf_chips.dll beside the original mf.exe overrides the embedded DLL.
The loader extracts DLL, source and notices into a content-addressed temp folder.
