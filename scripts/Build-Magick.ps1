param(
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [Parameter(Mandatory=$true)][string]$VisualStudioPath,
    [string]$SourceCache, [string]$SourceBundleRoot,
    [string]$PythonCommand = 'python', [switch]$Resume,
    [int]$ParallelProjects = 4
)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($WorkDirectory)
# The compiler embeds absolute source paths (assert/__FILE__) into the DLL, and the DLL is shipped.
# Refuse work folders inside a user profile so the account name cannot end up in the release.
$profileRoot = [Environment]::GetFolderPath('UserProfile')
if ($profileRoot -and $work.StartsWith($profileRoot, [StringComparison]::OrdinalIgnoreCase) -or $work -match '(?i)^[A-Z]:\\Users\\') {
    throw 'Use a short neutral work directory outside the user profile (for example C:\mfb).'
}
$msbuild = Join-Path $VisualStudioPath 'MSBuild/Current/Bin/amd64/MSBuild.exe'
if (!(Test-Path -LiteralPath $msbuild)) { throw 'Visual Studio 2026 C++ Build Tools are required.' }
if ($ParallelProjects -lt 1) { throw 'ParallelProjects must be positive.' }
if (!$Resume) {
    if ($SourceCache -and !$SourceBundleRoot) { $inputArgs=@('--cache',$SourceCache) }
    elseif ($SourceBundleRoot -and !$SourceCache) { $inputArgs=@('--bundle-root',$SourceBundleRoot) }
    else { throw 'Specify exactly one of SourceCache or SourceBundleRoot.' }
    & $PythonCommand "$PSScriptRoot/Prepare-MagickSources.py" --work $work @inputArgs
    if ($LASTEXITCODE -ne 0) { throw 'Source preparation failed.' }
} elseif (!(Test-Path -LiteralPath "$work/.mirrorflux-magick-source.json")) { throw 'Resume requires an existing prepared source directory.' }
if ((Get-Content "$work/.mirrorflux-magick-source.json" -Raw | ConvertFrom-Json).profile -ne "no-avc-hevc") { throw "Use a new work directory for the no-AVC/HEVC profile." }
function Build-Project([string]$project,[string]$configuration,[string]$log) {
    & $msbuild $project /nologo /verbosity:minimal "/m:$ParallelProjects" /p:BuildInParallel=true "/p:Configuration=$configuration" /p:Platform=x64 "/p:CL_MPCount=$ParallelProjects" *> (Join-Path $work $log)
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $work $log) -Tail 25; throw "Build failed: $log" }
}
function Install-Configure([string]$target) {
    New-Item -ItemType Directory -Path "$target/Configure" -Force | Out-Null
    foreach ($part in @('ColorProfiles','Configs','Installer','OpenCL','PerlMagick','Tools')) {
        Copy-Item -LiteralPath "$work/Configure/$part" -Destination "$target/Configure" -Recurse -Force
    }
    Copy-Item -LiteralPath "$work/Configure/Artifacts/Release/x64/bin/Configure.Release.x64.exe" -Destination "$target/Configure"
}
function Configure-Project([string]$directory,[string[]]$options,[string]$log) {
    Push-Location "$directory/Configure"
    try {
        & ./Configure.Release.x64.exe @options *> (Join-Path $work $log)
        if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $work $log) -Tail 25; throw "Configure failed: $log" }
    } finally { Pop-Location }
}
Write-Host 'Building the fixed Configure tool.'
Build-Project "$work/Configure/Configure.sln" Release '01-configure-build.log'
Install-Configure "$work/Dependencies"
Write-Host 'Building the fixed static image dependencies.'
if (!$Resume -or !(Test-Path "$work/Dependencies/IM7.Static.x64.sln")) {
    Configure-Project "$work/Dependencies" @('/noWizard','/VS2026','/x64','/static','/noOpenMP','/linkRuntime','/includeNonWindows') '02-dependencies-configure.log'
}
Build-Project "$work/Dependencies/IM7.Static.x64.sln" Release '03-dependencies-build.log'
# Configure uses this manifest to preserve static dependency headers and enable their delegates.
Get-ChildItem -LiteralPath "$work/Dependencies/Artifacts/lib" -Filter '*.lib' | Sort-Object Name | ForEach-Object Name | Set-Content "$work/Dependencies/Artifacts/pre-build-libs.txt" -Encoding ascii
$image = "$work/Native/src/ImageMagick"
Install-Configure $image
Copy-Item -LiteralPath "$work/Dependencies/Artifacts" -Destination $image -Recurse -Force
Write-Host 'Building ImageMagick Q8 and Magick.Native.'
Configure-Project $image @('/noWizard','/VS2026','/static','/Q8','/opencl','/noDpc','/noHdri','/noOpenMP','/x64','/linkRuntime') '04-imagemagick-configure.log'
'<Project><ItemDefinitionGroup Condition="''$(MSBuildProjectName)'' == ''CORE_MagickCore''"><ClCompile><PreprocessorDefinitions>%(PreprocessorDefinitions);MAGICKCORE_LIBRARY_NAME="Magick.Native-Q8-x64.dll"</PreprocessorDefinitions></ClCompile></ItemDefinitionGroup></Project>' | Set-Content "$image/Directory.Build.targets" -Encoding UTF8
Build-Project "$image/IM7.Static.x64.sln" Release '05-imagemagick-build.log'
Build-Project "$work/Native/Magick.Native.sln" ReleaseQ8 '06-magick-native-build.log'
$dll = "$work/Native/src/Magick.Native/bin/ReleaseQ8/x64/Magick.Native-Q8-x64.dll"
if (!(Test-Path -LiteralPath $dll)) { throw 'Native DLL was not produced.' }
[ordered]@{ dll=$dll; sha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant(); configuration='ReleaseQ8 x64 noHdri noOpenMP static delegates and compiler runtime; no AVC/H.264 or HEVC codecs'; msbuildVersion=(& $msbuild -version -nologo | Select-Object -Last 1) } | ConvertTo-Json | Set-Content "$work/BUILD.json" -Encoding UTF8
Write-Host "Magick rebuild completed: $dll"
