param([Parameter(Mandatory=$true)][string]$Compiler,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
if (!$OutputDirectory) {
 $project=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
 if(Test-Path (Join-Path $project 'MirrorFlux.csproj')){$OutputDirectory=Join-Path $project 'Assets/Chips'}
 else{$OutputDirectory=Join-Path $PSScriptRoot 'build'}
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$buildDirectory=Join-Path ([IO.Path]::GetTempPath()) ('mf-chips-build-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $buildDirectory | Out-Null
try {
$sources=@((Join-Path $PSScriptRoot 'bridge.c'),(Join-Path $PSScriptRoot 'qsound_adapter.c'),(Join-Path $PSScriptRoot 'qsound/qsound.c'))+@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'cores') -Filter '*.c' | Where-Object Name -ne 'scsplfo.c' | ForEach-Object FullName)
$objects=@()
foreach($source in $sources) {
 $object=Join-Path $buildDirectory ((Split-Path $source -Leaf)+'.o')
 # ビルドした場所のパスをDLLに残さない（assertの__FILE__などを相対パスにする）。
 & $Compiler -c -O2 -std=c11 "-ffile-prefix-map=$PSScriptRoot=." $source -o $object
 if($LASTEXITCODE -ne 0){throw "C compilation failed: $source"}
 $objects+=$object
}
$cpp=Join-Path (Split-Path $Compiler -Parent) 'clang++.exe'
$sources=@((Join-Path $PSScriptRoot 'extras.cpp'),(Join-Path $PSScriptRoot 'es550x.cpp'),(Join-Path $PSScriptRoot 'wonderswan/wswansound.cpp'),(Join-Path $PSScriptRoot 'gme/Nes_Fds_Apu.cpp'),(Join-Path $PSScriptRoot 'gme/Blip_Buffer.cpp'))+@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'vgsound_emu/es550x') -Filter '*.cpp' | ForEach-Object FullName)
& $cpp -shared -O2 -std=c++17 -static "-ffile-prefix-map=$PSScriptRoot=." @sources @objects -o (Join-Path $OutputDirectory 'mf_chips.dll')
if ($LASTEXITCODE -ne 0) { throw 'Standalone chip build failed.' }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.txt') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'provenance.json') -Destination $OutputDirectory
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'LICENSES') -File | Copy-Item -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'qsound/LICENSE') -Destination (Join-Path $OutputDirectory 'QSound-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'vgsound_emu/LICENSE') -Destination (Join-Path $OutputDirectory 'ES550x-LICENSE.txt')
$members=@('bridge.c','qsound_adapter.c','extras.cpp','es550x.cpp','host.h','constants.h','Build.ps1','README.txt','provenance.json','cores','qsound','gme','wonderswan','vgsound_emu','LICENSES') | ForEach-Object {Join-Path $PSScriptRoot $_}
Compress-Archive -LiteralPath $members -DestinationPath (Join-Path $OutputDirectory 'source.zip') -Force
Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'mf_chips.dll') -Algorithm SHA256
} finally {
 $resolved=[IO.Path]::GetFullPath($buildDirectory)
 $temporary=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
 if(!$resolved.StartsWith($temporary,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'mf-chips-build-*'){throw 'Unexpected build directory'}
 if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
