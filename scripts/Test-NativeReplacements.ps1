param(
    [Parameter(Mandatory=$true)][string]$ExePath,
    [Parameter(Mandatory=$true)][string]$ReplacementsPath,
    [Parameter(Mandatory=$true)][string]$ReportPath,
    [string]$DotnetCommand = 'dotnet',
    [switch]$EmbeddedImage
)
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ('mf-native-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $app = New-Item -ItemType Directory -Path "$work/app"
    Copy-Item -LiteralPath $ExePath -Destination "$app/mf.exe"
    $names = @('gme.dll','mf_rar.dll','mf_ymfm.dll','mf_chips.dll','mf_lha.dll','Magick.Native-Q8-x64.dll')
    foreach ($name in $names) { if ($EmbeddedImage -and $name -eq 'Magick.Native-Q8-x64.dll') { continue }; Copy-Item -LiteralPath (Join-Path $ReplacementsPath $name) -Destination $app }
    $probe = Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/NativeReplacementProbe'
    Copy-Item -LiteralPath "$probe/Probe.cs", "$probe/Probe.csproj" -Destination $work
    & $DotnetCommand build "$work/Probe.csproj" -c Release -o "$work/hook" -nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed' }
    $ReportPath = [IO.Path]::GetFullPath($ReportPath)
    $start = [Diagnostics.ProcessStartInfo]::new("$app/mf.exe")
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $app.FullName
    $start.Environment['DOTNET_STARTUP_HOOKS'] = "$work/hook/Probe.dll"
    $start.Environment['MF_NATIVE_PROBE_REPORT'] = $ReportPath
    if ($EmbeddedImage) { $start.Environment['MF_NATIVE_PROBE_IMAGE_HASH'] = (Get-FileHash -LiteralPath (Join-Path $ReplacementsPath 'Magick.Native-Q8-x64.dll')).Hash }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'Native replacement probe timed out' }
        if ($process.ExitCode -ne 0) { throw "Native replacement probe failed; see $ReportPath" }
    } finally { $process.Dispose() }
    if ($EmbeddedImage) { Write-Host 'Published EXE loaded the embedded image DLL with its expected hash.' }
    else { Write-Host 'Published EXE loaded all six replacement DLLs beside its original process path.' }
} finally {
    $resolved = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ((Split-Path $resolved -Parent) -ne $tempRoot -or (Split-Path $resolved -Leaf) -notmatch '^mf-native-probe-[a-f0-9]{32}$') { throw 'Unsafe probe cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
