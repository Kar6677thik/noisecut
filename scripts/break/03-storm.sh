#!/usr/bin/env bash
# Break experiment: storm. See docs/experiments/ and ADR-0013. Extra arguments are passed through (e.g. --rounds 20, --no-build).
set -euo pipefail
cd "$(dirname "$0")/../.."
exec dotnet run --project tools/NoiseCut.Experiments -- storm "$@"
