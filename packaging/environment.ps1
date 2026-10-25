# Dot-source before running build tools. Keep all generated state under artifacts.
$ErrorActionPreference = 'Stop'
$mdclRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$mdclWorkspace = Split-Path -Parent $mdclRoot
$mdclBundledDotnet = Join-Path $mdclWorkspace '.dotnet10/dotnet.exe'
$mdclDotnet = if (Test-Path -LiteralPath $mdclBundledDotnet) { $mdclBundledDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
if (Test-Path -LiteralPath $mdclBundledDotnet) {
    $env:DOTNET_ROOT = Split-Path -Parent $mdclDotnet
    $env:PATH = $env:DOTNET_ROOT + [IO.Path]::PathSeparator + $env:PATH
    $mdclExistingPackages = Join-Path $mdclWorkspace '.nuget/packages'
    if (Test-Path -LiteralPath $mdclExistingPackages) { $env:NUGET_PACKAGES = $mdclExistingPackages }
}
$env:DOTNET_CLI_HOME = Join-Path $mdclRoot 'artifacts/work/dotnet-home'
$env:TEMP = Join-Path $mdclRoot 'artifacts/work/tmp'
$env:TMP = $env:TEMP
$env:TMPDIR = $env:TEMP
$env:NUGET_HTTP_CACHE_PATH = Join-Path $mdclRoot 'artifacts/work/nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $mdclRoot 'artifacts/work/nuget-plugins'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:TEMP, $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_PLUGINS_CACHE_PATH | Out-Null
