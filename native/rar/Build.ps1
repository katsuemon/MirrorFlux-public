param(
    [Parameter(Mandatory=$true)][string]$Compiler,
    [string]$CMake = 'cmake', [string]$Ninja = 'ninja',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) {
    $project = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    if (Test-Path (Join-Path $project 'MirrorFlux.csproj')) { $OutputDirectory = Join-Path $project 'Assets/Rar' }
    else { $OutputDirectory = Join-Path $PSScriptRoot 'build' }
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('mf-rar-build-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $cc = (Resolve-Path -LiteralPath $Compiler).Path
    $ninjaPath = (Get-Command $Ninja -ErrorAction Stop).Source
    & $CMake -S $PSScriptRoot -B $work -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninjaPath" "-DCMAKE_C_COMPILER=$cc" -DCMAKE_BUILD_TYPE=Release
    if ($LASTEXITCODE -ne 0) { throw 'RAR configure failed' }
    & $CMake --build $work --target mf_rar --parallel 4
    if ($LASTEXITCODE -ne 0) { throw 'RAR build failed' }
    Copy-Item -LiteralPath "$work/mf_rar.dll" -Destination "$OutputDirectory/mf_rar.dll"
    Copy-Item -LiteralPath "$PSScriptRoot/README.txt", "$PSScriptRoot/provenance.json" -Destination $OutputDirectory
    Get-ChildItem -LiteralPath "$PSScriptRoot/LICENSES" -File | Copy-Item -Destination $OutputDirectory
    $sourceNames = @('libarchive','unarr','xadmaster','LICENSES','Build.ps1','CMakeLists.txt','bridge.c','rar15.c','rar15.h','README.txt','provenance.json','source-files.sha256.json')
    $sourcePaths = $sourceNames | ForEach-Object { Join-Path $PSScriptRoot $_ }
    Compress-Archive -LiteralPath $sourcePaths -DestinationPath "$OutputDirectory/source.zip" -Force
    [ordered]@{ compiler=(& $cc --version | Select-Object -First 1); cmake=(& $CMake --version | Select-Object -First 1); sha256=(Get-FileHash "$OutputDirectory/mf_rar.dll" -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json | Set-Content "$OutputDirectory/BUILD.json" -Encoding UTF8
} finally {
    $resolved = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ((Split-Path $resolved -Parent) -ne $tempRoot -or (Split-Path $resolved -Leaf) -notmatch '^mf-rar-build-[a-f0-9]{32}$') { throw 'Unsafe build cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
