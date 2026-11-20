LhASsA LH5 encoder, BSD-2-Clause
Upstream: https://github.com/amigazen/LhASsA
Commit: e1eaf42d9da4189a7dac25432ed1dd31bb5262f4
Only the independent encoder and its headers are used; Amiga UI code is not included.
MirrorFlux writes level-2 headers with CP932 names; incompressible data uses LH0.
The upstream source files are unmodified. bridge.c provides a fixed-width Windows C ABI.
Build: native/lha/Build.ps1 -Compiler <path to clang.exe from llvm-mingw>
Compiler used: llvm-mingw-20261020-ucrt-x86_64, Clang 23.1.4.
The DLL imports only Windows system/UCRT libraries.
A compatible mf_lha.dll beside the original mf.exe overrides the bundled binary.
The source and license are embedded and extracted alongside the DLL on first use.
