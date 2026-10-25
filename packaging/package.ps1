param(
    [switch]$SkipPublish,
    [ValidateSet('win-x64','osx-arm64','osx-x64')][string[]]$RuntimeIdentifiers = @('win-x64','osx-arm64','osx-x64')
)
. (Join-Path $PSScriptRoot 'environment.ps1')
if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') -RuntimeIdentifiers $RuntimeIdentifiers }
$python = @('python','python3') | ForEach-Object { Get-Command $_ -ErrorAction SilentlyContinue } |
    Where-Object { $_.Source -notlike '*WindowsApps*' } | Select-Object -First 1 -ExpandProperty Source
if (-not $python) { throw 'Python 3.10+ is required; Windows Store command aliases are not a Python runtime.' }
& $python -c 'import sys; sys.exit(0 if sys.version_info >= (3,10) else 1)'
if ($LASTEXITCODE -ne 0) { throw 'Python 3.10+ is required.' }
& $python (Join-Path $PSScriptRoot 'package.py') --root $mdclRoot --rids $RuntimeIdentifiers
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
