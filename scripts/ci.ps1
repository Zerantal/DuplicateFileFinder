Param(
  [string]$Configuration = $env:CONFIGURATION
)

if ([string]::IsNullOrWhiteSpace($Configuration)) { $Configuration = "Release" }

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")
$ArtifactsDir = Join-Path $RootDir "artifacts"
$TestResultsDir = Join-Path $ArtifactsDir "test-results"

New-Item -ItemType Directory -Force -Path $TestResultsDir | Out-Null

Write-Host "==> dotnet --info"
dotnet --info

Write-Host "==> Restore"
dotnet restore $RootDir

Write-Host "==> Build ($Configuration)"
dotnet build $RootDir -c $Configuration --no-restore

Write-Host "==> Test ($Configuration)"
dotnet test $RootDir -c $Configuration --no-build `
  --logger "trx;LogFileName=test_results.trx" `
  --results-directory $TestResultsDir `
  --collect "XPlat Code Coverage"

