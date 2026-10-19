# Break experiment: storm. See docs/experiments/ and ADR-0013. Extra arguments are passed through (e.g. --rounds 20, --no-build).
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '../..')
try { dotnet run --project tools/NoiseCut.Experiments -- storm @args; exit $LASTEXITCODE } finally { Pop-Location }
