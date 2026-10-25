param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Package,
    [Parameter(Mandatory)] [string] $PrivateKey,
    [Parameter(Mandatory)] [string] $PublicKey,
    [Parameter(Mandatory)] [string] $BaseUrl,
    [string] $Channel = 'stable',
    [string] $Rid = 'win-x64',
    [int] $ValidDays = 30
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workspace = (Resolve-Path (Join-Path $root '..')).Path
$dotnet = Join-Path $workspace '.dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
$packagePath = (Resolve-Path -LiteralPath $Package).Path
$outputDirectory = Join-Path $root 'artifacts\update'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$fileName = [IO.Path]::GetFileName($packagePath)
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    version = $Version
    channel = $Channel
    rid = $Rid
    packageUri = ($BaseUrl.TrimEnd('/') + '/' + [Uri]::EscapeDataString($fileName))
    size = (Get-Item -LiteralPath $packagePath).Length
    sha256 = $hash
    expiresAt = (Get-Date).ToUniversalTime().AddDays($ValidDays).ToString('yyyy-MM-ddTHH:mm:ssZ')
    minimumLauncherVersion = '0.1.0'
}
$manifestPath = Join-Path $outputDirectory 'update-manifest.json'
$envelopePath = Join-Path $outputDirectory 'update-envelope.json'
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8

Push-Location $root
try {
    & $dotnet run --project src/MDCL.ReleaseTool/MDCL.ReleaseTool.csproj -c Release -- sign --manifest $manifestPath --key $PrivateKey --out $envelopePath
    if ($LASTEXITCODE -ne 0) { throw 'sign failed' }
    & $dotnet run --project src/MDCL.Updater/MDCL.Updater.csproj -c Release -- verify --envelope $envelopePath --public-key $PublicKey --channel $Channel --rid $Rid --current 0.0.0
    if ($LASTEXITCODE -ne 0) { throw 'verify failed' }
}
finally { Pop-Location }

Write-Output "manifest: $manifestPath"
Write-Output "envelope: $envelopePath"
Write-Output "sha256:   $hash"
