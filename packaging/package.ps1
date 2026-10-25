param(
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifacts = Join-Path $root 'artifacts'
$packages = Join-Path $artifacts 'packages'
$version = '0.1.0-alpha'
$releaseRoot = Join-Path $packages "MDCL-$version"

if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'publish.ps1') }
if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

$winSource = Join-Path $artifacts 'win-x64'
$winStage = Join-Path $releaseRoot 'windows'
New-Item -ItemType Directory -Force -Path $winStage | Out-Null
Copy-Item -Path (Join-Path $winSource '*') -Destination $winStage -Recurse
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $winStage
Copy-Item -LiteralPath (Join-Path $root 'docs\evidence\release-matrix.md') -Destination $winStage
$winZip = Join-Path $releaseRoot "MDCL-$version-win-x64.zip"
Compress-Archive -Path (Join-Path $winStage '*') -DestinationPath $winZip -CompressionLevel Optimal

foreach ($rid in @('osx-arm64', 'osx-x64')) {
    $source = Join-Path $artifacts $rid
    if (-not (Test-Path -LiteralPath $source)) { throw "Missing publish output for $rid" }
    $stage = Join-Path $releaseRoot $rid
    $app = Join-Path $stage 'MDCL.app'
    $macos = Join-Path $app 'Contents\MacOS'
    $resources = Join-Path $app 'Contents\Resources'
    New-Item -ItemType Directory -Force -Path $macos, $resources | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $macos -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'macos\Info.plist') -Destination (Join-Path $app 'Contents\Info.plist')
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $resources
    Copy-Item -LiteralPath (Join-Path $root 'docs\evidence\release-matrix.md') -Destination $resources
    Set-Content -LiteralPath (Join-Path $stage 'UNSIGNED-CROSSBUILD.txt') -Value @'
This macOS package was assembled from a Windows cross-build.
It is not signed, not notarized, and its Unix executable bits were not validated on macOS.
Run packaging/macos/sign-and-notarize.sh on a Mac before any distribution.
'@
    $tar = Join-Path $releaseRoot "MDCL-$version-$rid-UNSIGNED.app.tar.gz"
    & tar -czf $tar -C $stage 'MDCL.app' 'UNSIGNED-CROSSBUILD.txt'
    if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
}

Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $releaseRoot
$lockDir = Join-Path $releaseRoot 'dependency-lockfiles'
New-Item -ItemType Directory -Force -Path $lockDir | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Filter packages.lock.json | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '_')
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $lockDir $relative)
}

$files = Get-ChildItem -LiteralPath $releaseRoot -Recurse -File | Sort-Object FullName
$hashes = foreach ($file in $files) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [pscustomobject]@{
        path = [IO.Path]::GetRelativePath($releaseRoot, $file.FullName).Replace('\', '/')
        size = $file.Length
        sha256 = $hash
    }
}
$hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.json') -Encoding UTF8
$hashes | ForEach-Object { "$($_.sha256)  $($_.path)" } | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding UTF8
@{
    product = 'MDCL'
    version = $version
    builtAt = (Get-Date).ToUniversalTime().ToString('O')
    targets = @(
        @{ rid = 'win-x64'; package = "MDCL-$version-win-x64.zip"; status = 'built'; signed = $false; macVerified = $false },
        @{ rid = 'osx-arm64'; package = "MDCL-$version-osx-arm64-UNSIGNED.app.tar.gz"; status = 'cross-built-unverified'; signed = $false; macVerified = $false },
        @{ rid = 'osx-x64'; package = "MDCL-$version-osx-x64-UNSIGNED.app.tar.gz"; status = 'cross-built-unverified'; signed = $false; macVerified = $false }
    )
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') -Encoding UTF8

Write-Output "Release directory: $releaseRoot"
Write-Output "Windows package: $winZip"
Get-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt')
