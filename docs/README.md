# Documentation map

| Question | Where |
|----------|-------|
| **What** are we building, exactly? | [`spec/SPEC.md`](spec/SPEC.md) |
| **Why** is it designed this way? | [`adr/`](adr/README.md) (one file per decision) |
| **When** does each piece happen, and in what order? | [`plan/MILESTONES.md`](plan/MILESTONES.md) |
| What happened so far, and what's next? | [`JOURNAL.md`](JOURNAL.md) |
| What concepts do I need to understand it? | [`learning/CONCEPTS.md`](learning/CONCEPTS.md) |
| How does the code actually work? | [`learning/M1-build.md`](learning/M1-build.md) (code tour: one event, end to end) |
| How do we measure and break it? | [`learning/M2-break.md`](learning/M2-break.md) (metrics, truth log, chaos) |
| How was each failure fixed? | [`learning/M3-prove.md`](learning/M3-prove.md) (checkpoints, event time, noise policy, zone stage) |
| How is it deployed? | [`learning/M4-deploy.md`](learning/M4-deploy.md) (images, k3s objects, least-privilege CI) |
| What did each experiment measure? | [`experiments/`](experiments/README.md) |
| How do I operate it? | [`runbooks/`](runbooks/): [bootstrap](runbooks/bootstrap-home-server.md), [deploy · roll back · inspect](runbooks/deploy.md), [rotate secrets](runbooks/rotate-secrets.md), [local k3s rehearsal](runbooks/k3s-rehearsal.md) |
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
