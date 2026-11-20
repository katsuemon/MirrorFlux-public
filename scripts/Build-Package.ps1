param(
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$DotnetCommand = 'dotnet',
    # 指定すると末尾を上げる代わりにこの版でビルドする（例: build.bat -Version 1.0.0）。
    [string]$Version = ''
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$dist = Join-Path $ProjectRoot 'publish'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$buildLock = [IO.File]::Open((Join-Path $dist '.build.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$versionFile = Join-Path $ProjectRoot 'Version.props'
$originalVersionFile = $null
$versionChanged = $false
$package = Join-Path $dist 'win-x64'
$zip = Join-Path $dist 'MirrorFlux_win-x64_Portable.zip'
$work = Join-Path $dist ('.build-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'output'
$next = Join-Path $work 'win-x64'
$nextZip = Join-Path $work 'MirrorFlux_win-x64_Portable.zip'
$backup = Join-Path $work 'previous'
$backupZip = Join-Path $work 'previous.zip'
$keepWork = $false
$packageInstalled = $false
$zipInstalled = $false
try {
    if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $env:LOCALAPPDATA 'MirrorFlux\.dotnet' }
    if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $env:DOTNET_CLI_HOME '.nuget\packages' }
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $originalVersionFile = [IO.File]::ReadAllText($versionFile)
    $match = [regex]::Match($originalVersionFile, '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
    if (-not $match.Success) { throw 'Version.props must contain a three-part Version.' }
    if ($Version) {
        if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw '-Version must be a three-part version such as 1.0.0.' }
        $nextVersion = $Version
    }
    else { $nextVersion = '{0}.{1}.{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, ([int]$match.Groups[3].Value + 1) }
    $updatedVersionFile = $originalVersionFile.Substring(0, $match.Index) + '<Version>' + $nextVersion + '</Version>' +
        $originalVersionFile.Substring($match.Index + $match.Length)
    [IO.File]::WriteAllText($versionFile, $updatedVersionFile, [Text.UTF8Encoding]::new($false))
    $versionChanged = $true
    New-Item -ItemType Directory -Path $next -Force | Out-Null
    & $DotnetCommand publish (Join-Path $ProjectRoot 'MirrorFlux.csproj') -c Release -r win-x64 --self-contained true -p:UseSharedCompilation=false -nodeReuse:false -o $stage -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    # Keep the public executable name while the internal assembly has a unique DLL name.
    Copy-Item -LiteralPath (Join-Path $stage 'MirrorFlux.App.exe') -Destination (Join-Path $next 'mf.exe')
    $ini = Join-Path $package 'mf.ini'
    if (-not (Test-Path -LiteralPath $ini)) { $ini = Join-Path $ProjectRoot 'mf.default.ini' }
    Copy-Item -LiteralPath $ini -Destination (Join-Path $next 'mf.ini')
    $rhythm = Join-Path $package 'YM2608_ADPCM.ROM'
    # Preserve the user's optional local ROM; never source it from build output.
    if (Test-Path -LiteralPath $rhythm) { Copy-Item -LiteralPath $rhythm -Destination (Join-Path $next 'YM2608_ADPCM.ROM') }
    # Keep the local INI, but always ship the reviewed template in the portable ZIP.
    $portable = Join-Path $work 'portable'
    New-Item -ItemType Directory -Path $portable -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $next 'mf.exe') -Destination $portable
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'mf.default.ini') -Destination (Join-Path $portable 'mf.ini')
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'docs/external-data.txt') -Destination (Join-Path $portable 'EXTERNAL-DATA.txt')
    # Short readme (usage, disclaimer, source location) and the license texts that must travel with the binary.
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'docs/readme-dist.txt') -Destination (Join-Path $portable 'README.txt')
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'LICENSE') -Destination (Join-Path $portable 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'COPYRIGHT.txt') -Destination $portable
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'THIRD_PARTY_NOTICES.md') -Destination $portable
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'licenses') -Destination (Join-Path $portable 'licenses') -Recurse
    # release-status.json tracks the release audit, not a license notice; keep it in the repository only.
    Remove-Item -LiteralPath (Join-Path $portable 'licenses/release-status.json') -Force
    # Windows PowerShell's Compress-Archive writes '\' in nested entry names; write entries with '/' explicitly.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $portableFiles = @('mf.exe', 'mf.ini', 'README.txt', 'EXTERNAL-DATA.txt', 'LICENSE.txt', 'COPYRIGHT.txt', 'THIRD_PARTY_NOTICES.md' | ForEach-Object { Get-Item -LiteralPath (Join-Path $portable $_) }) +
        @(Get-ChildItem -LiteralPath (Join-Path $portable 'licenses') -Recurse -File | Sort-Object FullName)
    $archive = [IO.Compression.ZipFile]::Open($nextZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $portableFiles) {
            $entryName = $file.FullName.Substring($portable.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
    # Publish only after both outputs exist. Retain originals for rollback.
    try {
        if (Test-Path -LiteralPath $package) { Move-Item -LiteralPath $package -Destination $backup }
        if (Test-Path -LiteralPath $zip) { Move-Item -LiteralPath $zip -Destination $backupZip }
        Move-Item -LiteralPath $next -Destination $package
        $packageInstalled = $true
        Move-Item -LiteralPath $nextZip -Destination $zip
        $zipInstalled = $true
    } catch {
        try {
            if ($zipInstalled) { Move-Item -LiteralPath $zip -Destination $nextZip }
            if ($packageInstalled) { Move-Item -LiteralPath $package -Destination $next }
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $package }
            if (Test-Path -LiteralPath $backupZip) { Move-Item -LiteralPath $backupZip -Destination $zip }
        } catch {
            $keepWork = $true
            Write-Warning "Restore failed. Previous package is retained in: $work"
        }
        throw
    }
    Write-Host "Package created successfully: $zip (Ver.$nextVersion)"
} catch {
    if ($versionChanged) { [IO.File]::WriteAllText($versionFile, $originalVersionFile, [Text.UTF8Encoding]::new($false)) }
    Write-Error $_ -ErrorAction Continue
    exit 1
} finally {
    $buildLock.Dispose()
    if (-not $keepWork -and (Test-Path -LiteralPath $work)) {
        $resolvedWork = (Resolve-Path -LiteralPath $work).Path
        $resolvedDist = (Resolve-Path -LiteralPath $dist).Path
        if ((Split-Path $resolvedWork -Parent) -ne $resolvedDist -or (Split-Path $resolvedWork -Leaf) -notmatch '^\.build-[a-f0-9]{32}$') {
            throw "Unsafe cleanup path: $resolvedWork"
        }
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
