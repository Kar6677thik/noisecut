# Engineering journal

A dated log of **what** was done, **why**, and **what's next**. Newest entry at the top.
Each milestone PR adds at least one entry. Decisions that need more than a line get an ADR (`docs/adr/`), and the entry links to it.

---

## 2026-10-18 — M1: Build (naive, end to end)

**What**
- A .NET 10 solution with seven projects (SPEC §3.2). Shared build settings: nullable on, warnings are errors, central package versions.
- `Core`: event envelope + codec, rule model + validator, panes, the three evaluators, `RuleEngine`, a deterministic order simulator, and notification text. **98 unit tests**, including whole-scenario tests that run the simulator straight into the rules with no Kafka.
- `Migrator`: a hand-written migration runner (advisory lock + journal), schema v1, and seeds for zones, restaurants, the 3 rules and 7 days of baselines.
- `Simulator`, `Engine`, `Notifier`, `Api` services, a single Dockerfile, a Compose stack (Redpanda, Console, PostgreSQL, Mailpit + services), and `scripts/demo.(ps1|sh)`.
- **20 integration tests** against real PostgreSQL (Testcontainers): migrations, seeding, rule loading, baseline fallback, idempotent intake, the outbox with two competing dispatchers, backoff, and HMAC.
- `ci.yml`: build, unit tests, integration tests, compose validation and image builds on every PR.

**Result (exit criterion)**: one `scripts/demo.ps1` run from an empty stack, on a laptop (14 vCPU / 7.7 GB available to Docker), demo rate 600 orders/sim-min:

| Incident | Rule | Detected after (wall clock) |
|----------|------|-----------------------------|
| kitchen-meltdown r-017 | restaurant-delay-burst | 40 s |
| rider-shortage z-04 | stuck-pickup | 186 s |
| zone-cancel-spike z-02 | zone-cancellation-spike | 266 s |

Naive noise in the same run: **186 candidates → 120 alerts → 372 notifications, 46 duplicate webhooks** at the sink.
These are not M2 measurements, since nothing was controlled, but they show the problem exists before we measure it properly.

**Why it is naive on purpose**: auto-commit, in-memory state, processing time and EveryMatch are each a Break experiment in M2.
Robust state/time modes and cooldown/grouping/escalation are *refused at startup* in M1 (`EnsureSupported`) instead of silently ignored.
`EmitMode=OnTransition` and `Notifier:Dedup` already work, because episodes and the intake log are needed for keys anyway. They get measured in M3.

**Spec changes found by building it** (all in SPEC.md, marked "M1")
| Finding | Change |
|---------|--------|
| A no-incident test fired a false cancellation spike at `minDenominator` 200 | Raised to 300; minBaselineRate is a floor (`max`) |
| A rider shortage would also trigger rule A through pickup delays | Rule A counts `stage = kitchen` only |
| "Drop absence state at deadline + 2 min" made late pickups unable to resolve | Keep fired state until pickup/cancel, bounded by `MaxOrderAge` (3 h) |
| The sim clock outruns the 7 seeded days in about 17 real hours | Baseline lookup falls back a week at a time (same hour-of-week) |
| A restarted simulator would send event time backwards | It resumes after the newest event in the log |
| "EveryMatch" was undefined for timers and ratios | Defined per rule kind (SPEC §6.5) |
| Deliveries' unique key would have hidden naive duplicates | `notification_key` is not unique in v1; robust mode stops repeats at intake |

**Tooling notes**
- .NET 10 SDK + xUnit v3 needs the Microsoft Testing Platform runner (`global.json` → `"test": {"runner": …}`), and tests run with `dotnet test --project <path>`.
- Npgsql logs a harmless `libgssapi_krb5` warning in slim containers. `GSS Encryption Mode=Disable` in the connection string stops the probe.

**Next:** M2 Break. Prometheus + Grafana, the metrics from SPEC §10, the simulator truth log and chaos knobs, then experiments 1–4 with numbers.

---

## 2026-10-17 — M0: Specification

**What**
- Read Project 3 in the project guide and the matching Field Guide sections (14, 18, 24, 28).
- Surveyed the home server to ground deployment decisions: single-node k3s (no Docker engine), about 7.6 GiB RAM shared with other apps, an existing Cloudflare Tunnel for public hostnames, and an existing GHCR + Tailscale CI pattern from another project.
- Wrote `docs/spec/SPEC.md`, 12 ADRs, `docs/plan/MILESTONES.md` and `docs/learning/CONCEPTS.md`.

**Why a spec first**
Streaming systems hide their hardest bugs in *semantics*: which clock a window uses, what "late" means, what counts as a duplicate,
what happens on a rebalance. Writing these down first turned vague phrases from the guide into testable statements. For example,
"alerts repeat after restart" became "0 duplicate notifications across ≥ 20 forced crashes, measured at the webhook sink".

**Decisions taken with the owner**
- Public subdomain with a **read-only showcase**. Admin login is required for any change (ADR-0010).
- **Local notification sinks** first (Mailpit + built-in webhook sink). Real Slack is a config change later.
- **One PR per milestone.**
- **Naive behaviours stay as config flags**, so every failure can be re-run (ADR-0008).

**Ambiguities found in the guide, and how they were resolved**
| Ambiguity | Resolution |
|-----------|------------|
| "Marked ready": the lifecycle had no ready event | Added `order.food_ready` (SPEC §4.2) |
| "Same hour last week": the simulator can't wait a week | Seed 7 days of hourly aggregates. Live data replaces them over time (SPEC §5.3) |
| Waiting a real hour per window makes demos slow | Sim clock with `TimeScale` (10×). Event time makes the engine indifferent to speed-up |
| "Partition by restaurant" breaks the per-zone rule at 3 consumers | Repartition topic `zone-partials`, plus a new Break #4 (ADR-0004) |
| Idle partitions stall watermarks | Source ticks (ADR-0012) |
| "Zero duplicate alerts": where is it measured? | At the sink, via `Idempotency-Key` receipts (ADR-0006) |
| Cooldown/escalation clock under a sped-up sim | Detection uses event time. Notification policy uses wall time (SPEC §5) |
| How CI reaches a private server without cluster-admin | Tailscale + namespace-scoped ServiceAccount (ADR-0009) |

**Next:** M1 Build. Solution skeleton, Compose stack, `Core` with unit tests first.
