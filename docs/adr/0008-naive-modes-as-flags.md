# ADR-0008: Keep naive behaviours as config flags

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0

## Context
The project's method is Build → Break → Prove. If the fix *replaces* the naive code, the failure can only be reproduced by
checking out an old commit, which also has old tests, old metrics and old tooling. That makes before/after numbers hard to compare.

## Decision
Naive behaviours stay in the codebase behind explicit settings (`Engine:StateMode`, `Engine:TimeMode`, `Engine:EmitMode`,
`Notifier:Dedup|Cooldown|Grouping|Escalation`), with `NoiseCut:Profile=naive|robust` presets. Each fix can be switched on alone,
so its individual effect can be measured.

## Alternatives considered
- **Git tags only**: simpler code, but experiments can't share the current metrics and scripts.

## Consequences
- Good: every experiment in `docs/experiments/` can be re-run on the latest commit with one env var.
- Cost: more branches in the code. Mitigated by keeping each mode behind a small interface (e.g. `IStateStore`: `InMemoryStateStore` vs `CheckpointedStateStore`), which is the Strategy pattern from the Field Guide (§3).
- Guardrail: production manifests pin `Profile=robust`, and the Api's `/about` page shows the active profile.
