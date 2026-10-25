param(
    [switch]$SkipPublish,
    [ValidateSet('win-x64','osx-arm64','osx-x64')][string[]]$RuntimeIdentifiers = @('win-x64','osx-arm64','osx-x64')
)
. (Join-Path $PSScriptRoot 'environment.ps1')
if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') -RuntimeIdentifiers $RuntimeIdentifiers }
$python = if (Get-Command python3 -ErrorAction SilentlyContinue) { 'python3' } else { 'python' }
& $python (Join-Path $PSScriptRoot 'package.py') --root $mdclRoot --rids $RuntimeIdentifiers
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
