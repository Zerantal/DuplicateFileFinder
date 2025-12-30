Param(
  [string]$Configuration = $env:CONFIGURATION
)

if ([string]::IsNullOrWhiteSpace($Configuration)) { $Configuration = "Release" }

$RootDir = Resolve-Path (Join-Path $PSScriptRoot "..")
$ArtifactsDir = Join-Path $RootDir "artifacts"
$TestResultsDir = Join-Path $ArtifactsDir "test-results"

New-Item -ItemType Directory -Force -Path $TestResultsDir | Out-Null

dotnet --info

Write-Host "==> Restore"
dotnet restore (Join-Path $RootDir "DuplicateFileFinder.sln")

Write-Host "==> Build ($Configuration)"
dotnet build (Join-Path $RootDir "DuplicateFileFinder.sln") -c $Configuration --no-restore

Write-Host "==> Test (DuplicateFileFinderLibTests)"
dotnet test (Join-Path $RootDir "DuplicateFileFinderLibTests\DuplicateFileFinderLibTests.csproj") `
  -c $Configuration `
  --no-build `
  --logger "trx;LogFileName=test_results.trx" `
  --results-directory $TestResultsDir `
  --collect "XPlat Code Coverage"
