# MirrorFlux third-party notices

MirrorFlux original work is GPL-3.0-or-later. See LICENSE (LICENSE.txt in the
portable ZIP) and COPYRIGHT.txt.
Third-party components retain their own licenses and copyright notices; this
file does not relicense them. License texts and notices are in the licenses/
folder. Paths under Assets/, native/ and third_party/ refer to the source code.

## Components

| Component | Terms / evidence |
| --- | --- |
| Magick.NET 14.17.2 / Magick.Native 2026.927.1314 | Apache-2.0; original NOTICE in licenses/nuget/Magick.NET-Q8-x64-14.17.2 |
| ImageMagick 7.1.2-32 and image delegates | ImageMagick license plus per-library terms; licenses/upstream and license-selections.json |
| Game_Music_Emu 0.6.5 / Nuked OPN2 | LGPL-2.1-or-later; Assets/Gme and native/gme |
| GME emu2413 | MIT, Mitsutaka Okazaki; Assets/Gme/emu2413-LICENSE.txt |
| GME zlib | Zlib; Assets/Gme/zlib-LICENSE.txt and source.zip |
| Independent chip host | GPL-2.0-or-later cores combined with BSD/MIT/zlib/LGPL components; Assets/Chips/provenance.json and notices |
| ymfm | BSD-3-Clause, Aaron Giles; Assets/Ymfm |
| LhASsA | BSD-2-Clause; Assets/Lha |
| MultiPCM display | BSD; Assets/MultiPCM/LICENSE.txt |
| AvalonEdit | MIT; Assets/AvalonEdit and licenses/upstream/AvalonEdit |
| Hst.Compression / Hst.Core | MIT; licenses/nuget and licenses/upstream/hst-core |
| NAudio | MIT; licenses/upstream/NAudio |
| Markdig | BSD-2-Clause; licenses/upstream/markdig |
| SharpCompress 0.50.7 modified | MIT and per-file notices; all RAR implementation removed; third_party/SharpCompress/MODIFICATIONS.json |
| libarchive 3.8.11 (RAR5) | BSD-2-Clause, per-file BSD-3-Clause/public domain; BLAKE2 CC0 option; Assets/Rar |
| unarr 1.2.0 (RAR2/3/4) | LGPL-3.0 with public-domain exceptions; Assets/Rar |
| XADMaster RAR1.5 C adaptation | LGPL-2.1-or-later, MacPaw Way Ltd.; native/rar/xadmaster and rar15.c |
| .NET / WindowsDesktop and Microsoft packages | Package-specific notices in licenses/dotnet and licenses/nuget |
| LLVM-MinGW compiler runtime | LLVM exceptions / MinGW notices in each native asset group |

Parts of this software are copyright (c) the FreeType Project
(https://www.freetype.org). All rights reserved.
This software is based in part on the work of the Independent JPEG Group.

## Selected alternatives

Cairo is used under its LGPL 2.1 option, not MPL 1.1. LibRaw uses its LGPL
option, not CDDL. FreeType uses FTL; Highway uses Apache-2.0; zstd uses BSD.
The original notices (including alternative license texts) remain unmodified.
The combined work must meet GPLv3 terms as well as applicable library notices.

The Windows image build uses static delegate libraries and a static compiler
runtime. Its outer DLL boundary alone does not satisfy all LGPL requirements.
Exact source archives and build controls are fixed in licenses/sources.lock.json.
The source bundle must accompany the matching binary release, at no additional
charge. A source URL list alone is not the selected compliance mechanism.

Magick uses the separate corresponding-source companion and scripts/Build-Magick.ps1.
GME, RAR, ymfm, LHA and independent chip DLLs can be rebuilt from their source.zip
and replaced beside the original mf.exe. Modification, relinking and reverse
engineering to debug library modifications are permitted. There are no signing
keys or installation keys required by MirrorFlux. Preserve all notices when
redistributing modified versions.

Unused System.Data.SqlClient and proprietary native SNI are excluded from
runtime assets. A package appearing in the restore graph is not proof that its
binary is distributed; licenses/nuget-runtime.json records that distinction.

No game ROM, recorded waveform or user music is licensed or distributed by
MirrorFlux. See docs/external-data.txt. The original application icon was made
by katsuemon, as confirmed on 2026-11-11; it is covered by the application license.

## Image codec distribution profile

The original Magick.NET native asset is excluded. Assets/Image contains our
rebuild without OpenH264 or libde265; neither AVC/H.264 nor HEVC implementations
are bundled. HEIC/HEIF/AVCI decoding uses the user's installed Windows codecs.
AVIF uses the retained AV1 libraries. BUILD.json and sources.lock.json describe
the current input; historical evidence and original upstream notices may mention
optional codecs not present in this build. All original notices are preserved.
