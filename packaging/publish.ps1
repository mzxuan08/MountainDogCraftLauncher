param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workspace = (Resolve-Path (Join-Path $root '..')).Path
$dotnet = Join-Path $workspace '.dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $workspace '.dotnet-home'
$env:LOCALAPPDATA = Join-Path $workspace '.localappdata'
$env:NUGET_PACKAGES = Join-Path $workspace '.nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $workspace '.nuget\http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $workspace '.nuget\plugins-cache'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TEMP = Join-Path $workspace '.tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:LOCALAPPDATA, $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_PLUGINS_CACHE_PATH, $env:TEMP | Out-Null

Push-Location $root
try {
    & $dotnet restore MDCL.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & $dotnet build MDCL.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($rid in @('win-x64', 'osx-arm64', 'osx-x64')) {
        $output = Join-Path $root "artifacts\$rid"
        if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
        & $dotnet publish src/MDCL.Desktop/MDCL.Desktop.csproj -c $Configuration -r $rid --self-contained true --no-restore -o $output
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid." }
    }
}
finally {
    Pop-Location
}
