Magick.Native-Q8-x64.dll is rebuilt for MirrorFlux without AVC/H.264 or HEVC decoder implementations.
The managed Magick.NET 14.17.2 wrapper remains; its original native DLL is excluded from distribution.
HEIC/HEIF/AVCI files use Windows WIC codecs installed by the user. AVIF remains available via the bundled AV1 decoders.

Corresponding source, full third-party licenses and modifications are supplied in the separate MirrorFlux source/license companion archives.
Build with scripts/Build-Magick.ps1 and scripts/Prepare-MagickSources.py; exact upstream revisions and archive hashes are in licenses/sources.lock.json.
BUILD.json records this DLL hash, toolchain and the two source configuration patches.
NOTICE.txt is generated from the selected dependency build; LICENSE.txt is Magick.Native's Apache-2.0 license.
Upstream license texts can mention optional components absent from this build; the source lock and BUILD.json identify the compiled profile.
Users may rebuild/modify this DLL and place the same filename beside mf.exe; that file takes priority over the embedded copy.
