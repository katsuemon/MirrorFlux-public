Game_Music_Emu 0.6.5 - MirrorFlux reproducible source build
Copyright Shay Green and the upstream contributors.
Library: LGPL-2.1-or-later; emu2413: MIT; zlib: Zlib.
The upstream source is unchanged except exclusion of optional test.nsf/test.vgz.
All upstream formats remain enabled; the default Nuked YM2612 is used.
No user ROM, recorded waveform or music is included.

Build.ps1 -Compiler <LLVM-MinGW clang.exe> -CMake <cmake.exe> -Ninja <ninja.exe>
Tested: LLVM-MinGW 20261020 UCRT x64, Clang 23.1.4, CMake 4.4.4, Ninja 1.13.2.
CMake/Ninja and the compiler are external build tools; source builds need no network.
The zlib source is linked statically. Runtime imports are Windows/UCRT only.
The old MSYS2 libstdc++, libgcc, libwinpthread and zlib DLLs are no longer bundled.
LICENSES contains compiler runtime notices. See provenance.json for source identity.

Place a compatible x64 gme.dll beside the original mf.exe to replace the embedded
library. Reverse engineering to debug modifications of this LGPL library is
permitted. No signature check or installation key is required. The embedded
library and its source/notices are extracted into a content-addressed temp folder.
The application is GPL-3.0-or-later; original library notices remain applicable.
