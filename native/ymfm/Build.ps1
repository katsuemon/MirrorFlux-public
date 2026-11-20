param([Parameter(Mandatory=$true)][string]$Compiler, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) {
    $project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if (Test-Path (Join-Path $project 'MirrorFlux.csproj')) { $OutputDirectory = Join-Path $project 'Assets/Ymfm' }
    else { $OutputDirectory = Join-Path $PSScriptRoot 'build' }
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$out = Join-Path $OutputDirectory 'mf_ymfm.dll'
$sources = @('bridge.cpp', 'vgm_chips.cpp', 'upstream/ymfm_opm.cpp', 'upstream/ymfm_opn.cpp',
    'upstream/ymfm_opl.cpp', 'upstream/ymfm_ssg.cpp', 'upstream/ymfm_adpcm.cpp', 'upstream/ymfm_pcm.cpp') |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
& $Compiler -shared -O2 -std=c++14 -static "-ffile-prefix-map=$PSScriptRoot=." @sources -o $out
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'upstream/LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE.txt')
Compress-Archive -Path (Join-Path $PSScriptRoot 'upstream'), (Join-Path $PSScriptRoot 'bridge.cpp'), (Join-Path $PSScriptRoot 'vgm_chips.cpp'), (Join-Path $PSScriptRoot 'Build.ps1') -DestinationPath (Join-Path $OutputDirectory 'source.zip') -Force
