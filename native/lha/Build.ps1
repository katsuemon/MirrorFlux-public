param([Parameter(Mandatory=$true)][string]$Compiler, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) {
    $project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if (Test-Path (Join-Path $project 'MirrorFlux.csproj')) { $OutputDirectory = Join-Path $project 'Assets/Lha' }
    else { $OutputDirectory = Join-Path $PSScriptRoot 'build' }
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$out = Join-Path $OutputDirectory 'mf_lha.dll'
& $Compiler -shared -O2 -DHOST -std=c99 ("-I" + (Join-Path $PSScriptRoot "upstream")) (Join-Path $PSScriptRoot "bridge.c") (Join-Path $PSScriptRoot "upstream/lh_lh5enc.c") -o $out
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'upstream/LICENSE.md') -Destination (Join-Path $OutputDirectory 'LICENSE.txt')
Compress-Archive -Path (Join-Path $PSScriptRoot 'upstream'), (Join-Path $PSScriptRoot 'bridge.c'), (Join-Path $PSScriptRoot 'Build.ps1') -DestinationPath (Join-Path $OutputDirectory 'source.zip') -Force
