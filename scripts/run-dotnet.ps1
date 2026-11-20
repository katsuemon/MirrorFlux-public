$env:DOTNET_CLI_HOME = Join-Path $env:LOCALAPPDATA 'MirrorFlux\.dotnet'
$env:NUGET_PACKAGES = Join-Path $env:DOTNET_CLI_HOME '.nuget\packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

& dotnet $args
exit $LASTEXITCODE
