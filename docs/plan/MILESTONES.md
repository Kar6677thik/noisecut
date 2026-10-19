# NoiseCut — Milestones

Each milestone is **one pull request** into `main`, reviewed and merged by the repo owner.
Every PR updates [`docs/JOURNAL.md`](../JOURNAL.md) (what happened, why, when) and adds learning notes for the concepts it introduced.

The order follows the project guide's loop: **Learn → Build → Break → Prove**, then Deploy and Report.

| # | Branch | Theme | Depends on |
|---|--------|-------|------------|
| M0 | `m0/spec` | Specification: remove ambiguity before code | — |
| M1 | `m1/build` | Smallest end-to-end version (naive) | M0 |
| M2 | `m2/break` | Reproduce and measure each failure | M1 |
| M3 | `m3/prove` | Fix each failure, measure again, scale to 3 | M2 |
| M4 | `m4/deploy` | k3s + GitHub Actions + public showcase | M3 (M4a CI can start after M1) |
| M5 | `m5/report` | One-page report, README diagram, demo recording | M3, M4 |

---

## M0 — Specification ✅

**Why first:** streaming systems have subtle semantics (time, ordering, duplicates). Deciding them in prose is cheap.
Discovering them in code is expensive.

Deliverables: [`SPEC.md`](../spec/SPEC.md), the ADRs, this plan, [`CONCEPTS.md`](../learning/CONCEPTS.md), and the journal.

**Exit:** the owner has read and approved the spec, and every open question has an answer or a default (SPEC §17).

---

## M1 — Build (the smallest thing that works end to end) ✅

> Done 2026-10-18. The demo detected all three incidents (40 s / 186 s / 266 s). Results and spec changes are in the
> [journal](../JOURNAL.md), and the code tour is in [`learning/M1-build.md`](../learning/M1-build.md).
> Deviations: the Engine polls rules every 30 s (LISTEN/NOTIFY hot reload moves to M4, together with rule editing), and the integration
> test project was started in M1, ahead of M3, because the outbox and intake deserved real-database tests from day one.

**Goal:** events flow from the simulator to a notification on the dashboard and the webhook sink. Everything runs in **naive** mode on purpose.

Tasks:
1. Solution skeleton: `global.json` (.NET 10), `Directory.Build.props` (nullable, warnings-as-errors, analyzers), projects from SPEC §3.2, and `.editorconfig`.
2. `deploy/compose`: Redpanda, Redpanda Console, PostgreSQL, Mailpit, plus the services. Topic creation runs as a one-shot `rpk` container.
3. `Core`: event envelope, rule model + validation, panes, the three evaluators (processing-time variant), and `EveryMatch` emit. **Unit tests first.**
4. `Migrator`: schema v1, seed zones/restaurants/rules, seed 7 days of `zone_hourly_stats`.
5. `Simulator`: discrete-event scheduler, sim clock, timing model, seed, the 4 scenarios, control HTTP API, and ticks.
6. `Engine`: one consumer, `InMemory` state, auto-commit, all three rules (zone rule computed locally, which is correct with 1 consumer).
7. `Notifier`: pass-through policy, outbox + dispatcher, `dashboard` + `webhook` channels.
8. `Api`: read-only dashboard (feed, alerts, rules), SSE, webhook sink with HMAC + receipts, and scenario forwarding (no auth yet, local only).
9. `scripts/demo.(sh|ps1)`: start the stack, inject each scenario, and assert that each expected alert appears.

**Exit:** `docker compose up` → run the demo script → all three reference incidents are visible on the dashboard. CI is green on unit tests.
**You'll learn:** producers/consumers, consumer groups, offsets, keys → partitions, windows, the Generic Host, Dapper, and the outbox.

---

## M2 — Break (measure the failures; change no fixes) ✅

> Done 2026-10-19. Numbers are in [docs/experiments/](../experiments/README.md), and the lessons are in [learning/M2-break.md](../learning/M2-break.md).
> Deviations: experiments run from a .NET harness instead of shell scripts (ADR-0013); a fifth scenario, `slow-kitchen`, was added as a
> negative control for Break #2; and Break #4 was run on two zones, because the outcome depends on partition skew.

**Goal:** turn each failure into a reproducible script and a recorded number. Each one gets a page in `docs/experiments/`.

| Exp | Script | Measure |
|-----|--------|---------|
| 1 | `scripts/break/01-restart.sh`: kill the engine mid-scenario N times | missed incidents, duplicate notifications (sink receipts) |
| 2 | `scripts/break/02-out-of-order.sh`: chaos on, plus a forced consumer pause and lag | false alerts/hour by rule |
| 3 | `scripts/break/03-storm.sh`: `alert-storm` scenario | candidates/min, notifications/min |
| 4 | `scripts/break/04-scale.sh`: 3 engines (zone stage not built yet) | zone-spike detected? zone counts vs. truth |

Also added in M2: Prometheus + Grafana in Compose, the metrics from SPEC §10, and a "truth log" in the simulator. The truth log records which incidents the simulator *actually* injected, so "missed" and "false" alerts can be computed rather than eyeballed.

**Exit:** four experiment pages with numbers, screenshots and exact commands.
**You'll learn:** at-most/at-least-once in practice, event time vs. processing time, alert fatigue, and why per-partition state breaks global aggregates.

---

## M3 — Prove (fix, re-measure, scale)

Each fix lands with its own commits and tests. Then the matching experiment is re-run in robust mode and its page gets an "After" section.

1. **Checkpointing** (SPEC §6.3, ADR-0005): `CheckpointedStateStore`, rebalance handlers, producer flush → checkpoint, epoch fencing. Integration test: kill mid-stream → identical alert keys.
2. **Event time** (SPEC §6.1–6.2, ADR-0012): watermarks, ticks, allowed lateness, `late-events`, order-insensitive absence state.
3. **Transitions + noise policy** (SPEC §6.5, §7): episodes, dedup, cooldown, grouping, escalation, ack, the `email` channel via Mailpit.
4. **Zone stage** (SPEC §6.4, ADR-0004): `zone-partials`, cumulative partials, min-watermark merge, `zone_hourly_stats` live writes.
5. **Scale to 3 consumers:** cooperative-sticky assignment. Re-run Experiment 4.
6. **Experiment 5 (load):** detection latency p50/p95/p99 at 1×/2×/4× load, and consumer lag.
7. **≥ 20 forced crashes under load** with 0 duplicate notifications (acceptance criterion 3).

**Exit:** experiments 1–5 have before/after numbers. Robust mode meets SPEC §16 criteria 1–4.
**You'll learn:** checkpoints, idempotent consumers, watermarks, repartitioning, rebalancing, tail latency.

---

## M4 — Deploy (GitHub Actions → home server)

Split into two PRs if useful: **M4a CI** (can start right after M1) and **M4b CD + public**.

1. `ci.yml`: restore/build/test (unit + Testcontainers), build all images, `kubectl kustomize` render check.
2. Dockerfiles: one parameterized multi-stage Dockerfile, non-root user, chiseled/alpine runtime, and `SOURCE_REVISION_ID` label.
3. `deploy/k3s`: namespace, StatefulSets (Redpanda, PostgreSQL) with PVCs, Deployments, Services, NodePorts 30530/30531, ConfigMaps, Secrets (templated, never committed), resource limits, probes, the migrator Job, the topic-init Job, PodDisruptionBudget for the engine, NetworkPolicies.
4. Admin auth + rate limiting (ADR-0010). Grafana anonymous Viewer under `/grafana`.
5. **One-time bootstrap runbook** (`docs/runbooks/bootstrap-home-server.md`), done once by the owner: namespace-scoped ServiceAccount + RBAC + kubeconfig, Tailscale ACL for `tag:ci`, Cloudflare Tunnel ingress rule + DNS route, GitHub secrets.
6. `deploy.yml`: on push to `main` after CI passes, push SHA-tagged images → join the tailnet → apply → migrate → rollout status → smoke test the public URL.
7. Runbooks: deploy, roll back (re-run the deploy for an older SHA), inspect topics, reset state, rotate secrets.

**Exit:** a merge to `main` deploys automatically. The public URL shows the read-only showcase, admin actions need login, and nothing internal is reachable from outside.
**You'll learn:** container images, Kubernetes objects, RBAC, least-privilege CI, private networking, and tunnels.

---

## M5 — Report

1. `docs/report/REPORT.md`: one page with numbers, machine specs, commit SHA and exact commands (project guide "Prove").
2. README: architecture diagram (Mermaid), quick start, a GIF/asciinema of Break #1 vs. robust mode.
3. Résumé line filled in **only with measured numbers**.
4. Retrospective: what we would do differently, and how this maps to Kafka Streams/Flink.

**Exit:** SPEC §16 is fully satisfied.
