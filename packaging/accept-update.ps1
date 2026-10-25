param([Parameter(Mandatory)][string]$Package, [Parameter(Mandatory)][string]$EvidenceDirectory)
. (Join-Path $PSScriptRoot 'environment.ps1')
$mdclPackage = (Resolve-Path -LiteralPath $Package).Path
$mdclEvidence = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Force -Path $mdclEvidence | Out-Null
$mdclTestRoot = Join-Path $mdclRoot ('artifacts/work/update-acceptance-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $mdclTestRoot | Out-Null
$mdclPrivate = Join-Path $mdclTestRoot 'temporary-private.pem'
$mdclPublic = Join-Path $mdclTestRoot 'temporary-public.pem'
$mdclRsa = [Security.Cryptography.RSA]::Create(2048)
$mdclVersion = ([xml](Get-Content (Join-Path $mdclRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$mdclPublished = (Get-Content (Join-Path $mdclRoot 'artifacts/publish-index.json') -Raw | ConvertFrom-Json).'win-x64'
$mdclUpdater = Join-Path $mdclPublished 'updater/MDCL.Updater.exe'
$mdclTool = Join-Path $mdclRoot 'src/MDCL.ReleaseTool/bin/Release/net10.0/MDCL.ReleaseTool.dll'
$mdclLog = Join-Path $mdclEvidence 'updater-cli.log'
function Invoke-MdclChecked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments 2>&1 | Tee-Object -FilePath $mdclLog -Append | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Test command failed: $Executable ($LASTEXITCODE)" }
}
function New-MdclEnvelope([string]$Archive, [string]$Name) {
    $manifest = Join-Path $mdclTestRoot ($Name + '-manifest.json')
    $envelope = Join-Path $mdclTestRoot ($Name + '-envelope.json')
    @{ version=$mdclVersion; channel='preview'; rid='win-x64'; packageUri='https://example.invalid/test.zip';
       size=(Get-Item -LiteralPath $Archive).Length; sha256=(Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant();
       expiresAt=[DateTimeOffset]::UtcNow.AddHours(2).ToString('O'); minimumLauncherVersion='0.1.0' } |
        ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
    Invoke-MdclChecked $mdclDotnet @($mdclTool,'sign','--manifest',$manifest,'--key',$mdclPrivate,'--out',$envelope)
    return $envelope
}
try {
    [IO.File]::WriteAllText($mdclPrivate,$mdclRsa.ExportPkcs8PrivateKeyPem())
    [IO.File]::WriteAllText($mdclPublic,$mdclRsa.ExportSubjectPublicKeyInfoPem())
    Invoke-MdclChecked $mdclDotnet @('build',(Join-Path $mdclRoot 'src/MDCL.ReleaseTool'),'-c','Release','--no-restore')
    $target = Join-Path $mdclTestRoot 'application'
    $data = Join-Path $mdclTestRoot 'user-data'
    New-Item -ItemType Directory -Path $target,$data | Out-Null
    'old-app' | Set-Content -LiteralPath (Join-Path $target 'old.txt')
    'user-data-preserved' | Set-Content -LiteralPath (Join-Path $data 'canary.txt')
    $before = (Get-FileHash -LiteralPath (Join-Path $data 'canary.txt')).Hash
    $envelope = New-MdclEnvelope $mdclPackage 'good'
    Invoke-MdclChecked $mdclUpdater @('verify','--envelope',$envelope,'--public-key',$mdclPublic,'--rid','win-x64','--channel','preview','--current','0.1.0')
    Invoke-MdclChecked $mdclUpdater @('apply','--envelope',$envelope,'--package',$mdclPackage,'--target',$target,'--public-key',$mdclPublic,'--rid','win-x64','--channel','preview','--current','0.1.0')
    if (-not (Test-Path -LiteralPath (Join-Path $target 'MDCL.Desktop.exe'))) { throw 'New desktop missing.' }
    $statePath = Join-Path $mdclTestRoot 'mdcl-update-state.json'
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $transaction = Get-Content -LiteralPath $state.transactionState -Raw | ConvertFrom-Json
    if ($transaction.State -ne 'Completed') { throw 'Transaction did not complete health check.' }
    Invoke-MdclChecked $mdclUpdater @('rollback','--state',$statePath)
    if ((Get-Content -LiteralPath (Join-Path $target 'old.txt') -Raw).Trim() -ne 'old-app') { throw 'Old app not restored.' }
    $badDirectory = Join-Path $mdclTestRoot 'bad-payload'
    New-Item -ItemType Directory -Path $badDirectory | Out-Null
    [IO.File]::WriteAllText((Join-Path $badDirectory 'MDCL.Desktop.exe'),'Invalid executable for negative health-check fixture')
    $badPackage = Join-Path $mdclTestRoot 'bad.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($badDirectory,$badPackage)
    $badEnvelope = New-MdclEnvelope $badPackage 'bad'
    & $mdclUpdater apply --envelope $badEnvelope --package $badPackage --target $target --public-key $mdclPublic --rid win-x64 --channel preview --current 0.1.0 2>&1 | Tee-Object -FilePath $mdclLog -Append
    if ($LASTEXITCODE -eq 0) { throw 'Invalid desktop unexpectedly passed health check.' }
    if ((Get-Content -LiteralPath (Join-Path $target 'old.txt') -Raw).Trim() -ne 'old-app') { throw 'Health failure did not restore old app.' }
    if ((Get-FileHash -LiteralPath (Join-Path $data 'canary.txt')).Hash -ne $before) { throw 'User data changed.' }
    @{ version=$mdclVersion; rid='win-x64'; packageSha256=(Get-FileHash -LiteralPath $mdclPackage).Hash.ToLowerInvariant();
       signatureVerified=$true; realDesktopHealthPassed=$true; manualRollbackPassed=$true;
       invalidDesktopAutomaticRollbackPassed=$true; userDataUnchanged=$true;
       productionSigning=$false; macRuntimeTested=$false; temporaryPrivateKeyDeleted=$true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $mdclEvidence 'updater-cli-result.json') -Encoding utf8
    Write-Output 'Windows updater verify/apply/real-health/manual-rollback/failed-health-auto-rollback: PASS'
} finally {
    $mdclRsa.Dispose()
    if (Test-Path -LiteralPath $mdclPrivate) { Remove-Item -LiteralPath $mdclPrivate -Force }
}
