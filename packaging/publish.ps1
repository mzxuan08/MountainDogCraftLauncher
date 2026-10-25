param(
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64','osx-arm64','osx-x64')][string[]]$RuntimeIdentifiers = @('win-x64','osx-arm64','osx-x64'),
    [switch]$SkipTests
)
. (Join-Path $PSScriptRoot 'environment.ps1')
Push-Location $mdclRoot
try {
    & $mdclDotnet restore MDCL.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & $mdclDotnet build MDCL.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        & $mdclDotnet test MDCL.sln -c $Configuration --no-build --logger trx --results-directory artifacts/verification/release
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    }
    $buildId = [Guid]::NewGuid().ToString('N')
    $mdclPython = (Get-Command python -ErrorAction Stop).Source
    $mdclRevision = & $mdclPython packaging/source_info.py $mdclRoot
    if ($LASTEXITCODE -ne 0) { throw 'Cannot verify source provenance.' }
    $index = @{}
    foreach ($rid in $RuntimeIdentifiers) {
        $output = Join-Path $mdclRoot "artifacts/publish/$buildId/$rid"
        & $mdclDotnet publish src/MDCL.Desktop/MDCL.Desktop.csproj -c $Configuration -r $rid --self-contained true --no-restore -p:PublishTrimmed=false -p:PublishSingleFile=false -o $output
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid." }
        & $mdclDotnet publish src/MDCL.Updater/MDCL.Updater.csproj -c $Configuration -r $rid --self-contained true --no-restore -o (Join-Path $output 'updater')
        if ($LASTEXITCODE -ne 0) { throw "Updater publish failed for $rid." }
        @{ rid=$rid; commit=$mdclRevision; sdk=(& $mdclDotnet --version); signed=$false } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'build-info.json') -Encoding utf8
        $index[$rid] = $output
    }
    $index | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $mdclRoot 'artifacts/publish-index.json') -Encoding utf8
} finally { Pop-Location }
