# ADR-0013: Experiments run from a .NET harness, not shell scripts

- **Status:** Accepted
- **Date:** 2026-10-19
- **Milestone:** M2

## Context
SPEC §3.2 and the milestone plan promised `scripts/break/*.sh` with PowerShell equivalents. Each experiment, though, is a small
program, not a one-liner:
- start a fresh stack with per-experiment settings;
- inject scenarios, kill, pause or scale services at precise moments;
- poll until something happens or a deadline passes;
- query Prometheus and the truth log, then write a reproducible results file.

Writing every experiment twice, in bash and PowerShell, doubles the code and guarantees the two copies drift apart.

## Decision
One console project, `tools/NoiseCut.Experiments`, runs all experiments:
`dotnet run --project tools/NoiseCut.Experiments -- <restart|out-of-order|storm|scale|all>`.
- It drives the stack from the outside, like an operator: `docker compose` plus the Api's admin endpoints. It never reaches into the services' internals.
- Per-experiment settings go into a generated Compose override file. The committed `docker-compose.yml` never changes for an experiment.
- Each run writes `docs/experiments/results/<name>-<timestamp>.json` with the commit, machine, configuration, a timestamped step log and the numbers.
- `scripts/break/0N-*.sh|ps1` remain as one-line wrappers, so the names in the plan still work.

## Alternatives considered
- **bash + PowerShell scripts**: what was planned. Rejected for the duplication and the weak JSON handling in bash without `jq`.
- **xUnit tests that run the stack**: they would run on every `dotnet test`, take an hour, and pass/fail is the wrong output. An experiment produces *numbers*, not a verdict.

## Consequences
- Good: one implementation, typed JSON, and the same code on Windows, macOS and Linux. The .NET SDK is already a prerequisite.
- Cost: running an experiment needs the .NET SDK, not just Docker.
