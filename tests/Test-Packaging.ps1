$ErrorActionPreference = 'Stop'
$build = Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Build-Package.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('MirrorFlux-package-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    foreach ($mode in @('publish-failure', 'zip-failure', 'swap-failure', 'missing-template', 'success', 'success-without-rom')) {
        $project = Join-Path $root $mode
        New-Item -ItemType Directory -Path $project -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $project 'docs') | Out-Null
        Set-Content -LiteralPath (Join-Path $project 'docs/external-data.txt') -Value 'user supplied sound data'
        $versionFile = Join-Path $project 'Version.props'
        Set-Content -LiteralPath $versionFile -Value '<Project><PropertyGroup><Version>0.9.1</Version></PropertyGroup></Project>'
        $package = Join-Path $project 'publish\win-x64'
        New-Item -ItemType Directory -Path $package -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $package 'mf.exe') -Value 'old executable'
        Set-Content -LiteralPath (Join-Path $package 'mf.ini') -Value 'custom settings'
        if ($mode -ne 'missing-template') { Set-Content -LiteralPath (Join-Path $project 'mf.default.ini') -Value 'public defaults' }
        $zip = Join-Path $project 'publish\MirrorFlux_win-x64_Portable.zip'
        Set-Content -LiteralPath $zip -Value 'old zip'
        $localRom = Join-Path $package 'YM2608_ADPCM.ROM'
        if ($mode -ne 'success-without-rom') { Set-Content -LiteralPath $localRom -Value 'private user ROM' }
        $beforeRom = if (Test-Path -LiteralPath $localRom) { (Get-FileHash -LiteralPath $localRom).Hash } else { $null }
        $beforeExe = (Get-FileHash -LiteralPath (Join-Path $package 'mf.exe')).Hash
        $beforeIni = (Get-FileHash -LiteralPath (Join-Path $package 'mf.ini')).Hash
        $beforeZip = (Get-FileHash -LiteralPath $zip).Hash
        $fake = Join-Path $project 'fake-dotnet.ps1'
        if ($mode -eq 'publish-failure') {
            Set-Content -LiteralPath $fake -Value 'Set-Content -LiteralPath (Join-Path $PSScriptRoot ran.txt) -Value ran; $global:LASTEXITCODE = 9'
        } else {
            Set-Content -LiteralPath $fake -Value @'
Set-Content -LiteralPath (Join-Path $PSScriptRoot ran.txt) -Value ran
$output = $args[[Array]::IndexOf($args, '-o') + 1]
New-Item -ItemType Directory -Path $output -Force | Out-Null
Set-Content -LiteralPath (Join-Path $output 'MirrorFlux.App.exe') -Value 'new executable'
Set-Content -LiteralPath (Join-Path $output 'YM2608_ADPCM.ROM') -Value 'rhythm ROM'
$global:LASTEXITCODE = 0
'@
        }
        $env:MF_TEST_BUILD = $build
        $env:MF_TEST_PROJECT = $project
        $env:MF_TEST_DOTNET = $fake
        if ($mode -eq 'zip-failure') {
            $childCommand = 'function Compress-Archive { throw "Injected ZIP failure" }; & $env:MF_TEST_BUILD -ProjectRoot $env:MF_TEST_PROJECT -DotnetCommand $env:MF_TEST_DOTNET'
        } elseif ($mode -eq 'swap-failure') {
            $childCommand = 'function Move-Item { param($LiteralPath, $Destination); if ($LiteralPath -match "\.build-[a-f0-9]{32}\\MirrorFlux_win-x64_Portable\.zip$") { throw "Injected swap failure" }; Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination }; & $env:MF_TEST_BUILD -ProjectRoot $env:MF_TEST_PROJECT -DotnetCommand $env:MF_TEST_DOTNET'
        } else {
            $childCommand = '& $env:MF_TEST_BUILD -ProjectRoot $env:MF_TEST_PROJECT -DotnetCommand $env:MF_TEST_DOTNET'
        }
        # Give each fault injection its own PowerShell session and inspect the actual build-script exit code.
        $runner = [PowerShell]::Create()
        try {
            $null = $runner.AddScript($childCommand + '; $LASTEXITCODE')
            $result = $runner.Invoke()
            if ($result.Count -eq 0) { throw "No build result ($mode): $($runner.Streams.Error)" }
            $code = [int]$result[-1]
        } finally {
            $runner.Dispose()
        }
        if (-not (Test-Path -LiteralPath (Join-Path $project 'ran.txt'))) { throw 'Build was not invoked' }
        if ((Get-FileHash -LiteralPath (Join-Path $package 'mf.ini')).Hash -ne $beforeIni) { throw 'Settings changed' }
        if ($beforeRom) {
            if ((Get-FileHash -LiteralPath $localRom).Hash -ne $beforeRom) { throw 'User ROM changed' }
        } elseif (Test-Path -LiteralPath $localRom) { throw 'Build output unexpectedly supplied a ROM' }
        $expectedVersion = if ($mode -like 'success*') { '0.9.2' } else { '0.9.1' }
        if ((Get-Content -LiteralPath $versionFile -Raw) -notmatch "<Version>$expectedVersion</Version>") { throw 'Version did not follow build result' }
        if ($mode -notlike 'success*') {
            if ($code -eq 0) { throw 'Failure reported success' }
            if ((Get-FileHash -LiteralPath (Join-Path $package 'mf.exe')).Hash -ne $beforeExe) { throw 'Old executable lost' }
            if ((Get-FileHash -LiteralPath $zip).Hash -ne $beforeZip) { throw 'Old ZIP lost' }
        } else {
            if ($code -ne 0) { throw 'Success case failed' }
            if ((Get-Content -LiteralPath (Join-Path $package 'mf.exe') -Raw).Trim() -ne 'new executable') { throw 'New executable missing' }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $archive = [IO.Compression.ZipFile]::OpenRead($zip)
            try {
                if (($archive.Entries.FullName | Sort-Object) -join ',' -ne 'EXTERNAL-DATA.txt,mf.exe,mf.ini') { throw 'Invalid package contents' }
                $reader = [IO.StreamReader]::new($archive.GetEntry('mf.ini').Open())
                try {
                    if ($reader.ReadToEnd().Trim() -ne 'public defaults') { throw 'Portable ZIP leaked local settings' }
                } finally { $reader.Dispose() }
            } finally { $archive.Dispose() }
        }
        Write-Host "PASS $mode (settings and previous outputs protected)"
    }
} finally {
    $resolved = (Resolve-Path -LiteralPath $root).Path
    if ((Split-Path $resolved -Parent) -eq ([IO.Path]::GetTempPath().TrimEnd('\')) -and
        (Split-Path $resolved -Leaf) -match '^MirrorFlux-package-tests-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    Remove-Item Env:MF_TEST_BUILD, Env:MF_TEST_PROJECT, Env:MF_TEST_DOTNET -ErrorAction SilentlyContinue
}
