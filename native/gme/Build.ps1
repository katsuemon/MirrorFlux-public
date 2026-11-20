param(
    [Parameter(Mandatory=$true)][string]$Compiler,
    [string]$CMake = 'cmake', [string]$Ninja = 'ninja',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) {
    $project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if (Test-Path (Join-Path $project 'MirrorFlux.csproj')) { $OutputDirectory = Join-Path $project 'Assets/Gme' }
    else { $OutputDirectory = Join-Path $PSScriptRoot 'build' }
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('mf-gme-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    # zlib's CMake renames zconf.h: build a disposable copy, never edit shipped sources.
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'zlib') -Destination $work -Recurse
    $cc = (Resolve-Path -LiteralPath $Compiler).Path
    $cxx = Join-Path (Split-Path $cc) 'clang++.exe'
    $ninjaPath = (Get-Command $Ninja -ErrorAction Stop).Source
    & $CMake -S "$work/zlib" -B "$work/zlib-build" -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninjaPath" "-DCMAKE_C_COMPILER=$cc" -DCMAKE_BUILD_TYPE=Release -DZLIB_BUILD_TESTING=OFF -DZLIB_BUILD_SHARED=OFF
    if ($LASTEXITCODE -ne 0) { throw 'zlib configure failed' }
    & $CMake --build "$work/zlib-build" --target zlibstatic --parallel 4
    if ($LASTEXITCODE -ne 0) { throw 'zlib build failed' }
    & $CMake -S "$PSScriptRoot/upstream" -B "$work/gme-build" -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninjaPath" "-DCMAKE_C_COMPILER=$cc" "-DCMAKE_CXX_COMPILER=$cxx" -DCMAKE_BUILD_TYPE=Release -DGME_BUILD_SHARED=ON -DGME_BUILD_STATIC=OFF -DGME_BUILD_EXAMPLES=OFF -DGME_BUILD_TESTING=OFF -DBUILD_TESTING=OFF -DGME_YM2612_EMU=Nuked '-DCMAKE_SHARED_LINKER_FLAGS=-static -Wl,--no-insert-timestamp' "-DZLIB_INCLUDE_DIR=$work/zlib" "-DZLIB_LIBRARY=$work/zlib-build/libzs.a"
    if ($LASTEXITCODE -ne 0) { throw 'GME configure failed' }
    & $CMake --build "$work/gme-build" --parallel 4
    if ($LASTEXITCODE -ne 0) { throw 'GME build failed' }
    Copy-Item -LiteralPath "$work/gme-build/gme/libgme.dll" -Destination "$OutputDirectory/gme.dll"
    Copy-Item -LiteralPath "$PSScriptRoot/upstream/license.txt" -Destination "$OutputDirectory/LICENSE.txt"
    Copy-Item -LiteralPath "$PSScriptRoot/upstream/gme/ext/LICENSE" -Destination "$OutputDirectory/emu2413-LICENSE.txt"
    Copy-Item -LiteralPath "$PSScriptRoot/zlib/LICENSE" -Destination "$OutputDirectory/zlib-LICENSE.txt"
    Copy-Item -LiteralPath "$PSScriptRoot/README.txt", "$PSScriptRoot/provenance.json" -Destination $OutputDirectory
    Get-ChildItem -LiteralPath "$PSScriptRoot/LICENSES" -File | Copy-Item -Destination $OutputDirectory
    Compress-Archive -LiteralPath "$PSScriptRoot/upstream", "$PSScriptRoot/zlib", "$PSScriptRoot/LICENSES", "$PSScriptRoot/Build.ps1", "$PSScriptRoot/README.txt", "$PSScriptRoot/provenance.json" -DestinationPath "$OutputDirectory/source.zip" -Force
    [ordered]@{ compiler=(& $cc --version | Select-Object -First 1); cmake=(& $CMake --version | Select-Object -First 1); sha256=(Get-FileHash "$OutputDirectory/gme.dll" -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json | Set-Content "$OutputDirectory/BUILD.json" -Encoding UTF8
} finally {
    $resolved = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ((Split-Path $resolved -Parent) -ne $tempRoot -or (Split-Path $resolved -Leaf) -notmatch '^mf-gme-build-[a-f0-9]{32}$') { throw 'Unsafe build cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
