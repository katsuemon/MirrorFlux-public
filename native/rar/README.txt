MirrorFlux RAR adapter (mf_rar.dll)
RAR 1.5: XADMaster LGPL-2.1-or-later C adaptation (rar15.c).
RAR 2/3/4: unarr 1.2.0, LGPL-3.0, with public-domain components.
RAR 5: libarchive 3.8.11, BSD-2-Clause and per-file exceptions.
BLAKE2 uses its CC0 option. Preserve all individual file notices.
MirrorFlux bridge/build controls: GPL-3.0-or-later.
See provenance.json for fixed sources and local modifications.

Build from an extracted source.zip, using LLVM-MinGW, CMake and Ninja:
./Build.ps1 -Compiler C:/tools/llvm-mingw/bin/clang.exe -CMake C:/tools/cmake/bin/cmake.exe -Ninja C:/tools/ninja.exe
Output is build/ when extracted separately, or Assets/Rar in the app repository.
Place a rebuilt mf_rar.dll beside the original mf.exe to override the embedded copy.
No signing or installation key is needed. Library modification and debugging
(including reverse engineering for that purpose) are permitted.

RAR reads remain read-only; encrypted archives are not supported, as before.
Solid and multi-volume archives are supported. RAR4 split streams are joined
by the managed adapter in an owned temporary file, without modifying originals.
External compatibility fixtures are not included in source distributions.
