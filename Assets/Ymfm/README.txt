MirrorFlux standalone Yamaha sound engine

ymfm upstream: https://github.com/aaronsgiles/ymfm
Pinned commit: 81aec25ccbb98f4873a255f7551ac4dadac59b4a
License: BSD-3-Clause (LICENSE.txt).
OPM, OPN, OPL, SSG, ADPCM and their common dependencies are compiled.
The new files were extracted from the separately BSD-licensed ymfm subtree of
our pinned source archive; no libvgm player, device wrappers or mixing code was
copied. Source retains MirrorFlux channel-mask hooks in ymfm.
bridge.cpp preserves the standalone MDX ABI. vgm_chips.cpp adds our chip C ABI,
register queue, timers, external memory and integer-phase sample-rate conversion.
Downsampling integrates source-frame overlap; upsampling interpolates samples.
VGM parsing, scheduling and file admission are separately implemented in C#.
The DLL has no libvgm or MDSound dependency and contains no external sample-ROM or recorded-waveform payloads.
OPLL synthesis presets are part of the licensed ymfm chip source.

Build: LLVM-MinGW 20261020 UCRT x86_64, clang 23.1.4, -O2 -std=c++14 -static.
Extract source.zip and run Build.ps1 -Compiler <clang++.exe>.
Optional -OutputDirectory chooses a destination; standalone default is ./build.
The source and script also live in native/ymfm in the application repository.
LLVM/MinGW runtime notices are provided alongside the ymfm license.
SHA-256 mf_ymfm.dll: 56ec19bf6d6c4cbe49251554b0d4acc3a76f7a95f8e9cfd7db03b7d4ca383e10

The application extracts all these assets to a content-addressed temp directory.
A compatible mf_ymfm.dll beside the original mf.exe takes precedence.
MDX always uses this engine; there is no INI core selector.
VGM/S98 Yamaha synthesis also uses this DLL, including YMF278B PCM/FM.
The old compatibility engine has been removed. Register and ROM/RAM operations
are queued in order; full-device mute covers OPL4 PCM as well as FM.
See docs/vgm-compatibility.md for tested coverage and remaining fidelity limits.
Standalone builds emit two upstream template array-bound warnings in an
unreachable OPN rhythm branch with Clang 23; the build and audio checks pass.
