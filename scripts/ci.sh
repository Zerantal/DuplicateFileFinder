#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="${CONFIGURATION:-Release}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARTIFACTS_DIR="${ROOT_DIR}/artifacts"
TEST_RESULTS_DIR="${ARTIFACTS_DIR}/test-results"

mkdir -p "${TEST_RESULTS_DIR}"

echo "==> dotnet --info"
dotnet --info

echo "==> Restore"
dotnet restore "${ROOT_DIR}"

echo "==> Build (${CONFIGURATION})"
dotnet build "${ROOT_DIR}" -c "${CONFIGURATION}" --no-restore

echo "==> Test (${CONFIGURATION})"
dotnet test "${ROOT_DIR}" -c "${CONFIGURATION}" --no-build \
  --logger "trx;LogFileName=test_results.trx" \
  --results-directory "${TEST_RESULTS_DIR}" \
  --collect "XPlat Code Coverage"

