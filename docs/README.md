# Documentation map

| Question | Where |
|----------|-------|
| **What** are we building, exactly? | [`spec/SPEC.md`](spec/SPEC.md) |
| **Why** is it designed this way? | [`adr/`](adr/README.md) (one file per decision) |
| **When** does each piece happen, and in what order? | [`plan/MILESTONES.md`](plan/MILESTONES.md) |
| What happened so far, and what's next? | [`JOURNAL.md`](JOURNAL.md) |
| What concepts do I need to understand it? | [`learning/CONCEPTS.md`](learning/CONCEPTS.md) |
| How does the code actually work? | [`learning/M1-build.md`](learning/M1-build.md) (code tour: one event, end to end) |
| What did each experiment measure? | `experiments/` (from M2) |
| How do I operate it? | `runbooks/` (from M4) |
| What are the final numbers? | `report/REPORT.md` (M5) |

## Suggested reading order
1. `learning/CONCEPTS.md`: vocabulary.
2. `spec/SPEC.md` §1–§3: the problem and the shape of the system.
3. `adr/` 0003 → 0006 and 0012: the core streaming decisions.
4. The rest of the spec, then `plan/MILESTONES.md`.

## Conventions
- Docs change **in the same PR** as the code they describe.
- New hard-to-reverse decision → new ADR. Never edit an accepted ADR's decision; supersede it.
- Experiments record: commit SHA, machine specs, config, exact commands, raw numbers.
- Never commit secrets, server addresses or credentials. Use placeholders such as `<server-tailnet-host>`.
