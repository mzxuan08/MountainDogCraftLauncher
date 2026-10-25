param([string]$Filter = '', [string]$RunName = 'tests')
. (Join-Path $PSScriptRoot 'environment.ps1')
$results = Join-Path $mdclRoot "artifacts/verification/$RunName"
New-Item -ItemType Directory -Force -Path $results | Out-Null
Push-Location $mdclRoot
try {
    & $mdclDotnet restore MDCL.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    $testArgs = @('test', 'MDCL.sln', '-c', 'Release', '--no-restore', '--logger', 'trx', '--results-directory', $results)
    if ($Filter) { $testArgs += @('--filter', $Filter) }
    & $mdclDotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally { Pop-Location }
