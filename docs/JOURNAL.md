# Engineering journal

A dated log of **what** was done, **why**, and **what's next**. Newest entry at the top.
Each milestone PR adds at least one entry. Decisions that need more than a line get an ADR (`docs/adr/`), and the entry links to it.

---

## 2026-10-21 — M4: Deploy (GitHub Actions → k3s)

**What**
- **Images:** one multi-stage Dockerfile that builds all five services.
  - Runtime is the *chiseled* .NET image: no shell or package manager, and non-root.
  - The binary is renamed to `/app/service`, and OCI labels carry the commit SHA.
  - The api image is 236 MB. librdkafka was checked to work without a shell.
- **Manifests:** `deploy/k3s` holds a kustomize base plus `home` and `local` overlays (ADR-0016).
  - Redpanda, PostgreSQL and a 3-replica engine run as StatefulSets; Jobs create the topics and run the migrations.
  - Prometheus discovers pods with `kubernetes_sd` inside the namespace only. Grafana is served under `/grafana`.
  - NetworkPolicies: default-deny, plus same-namespace traffic, plus the two public NodePorts.
  - Every container is non-root, with a read-only root filesystem and limits.
- **CI identity:** a bootstrap manifest for a ServiceAccount bound to `admin` *in `noisecut` only*, and `make-kubeconfig.sh`, which emits its kubeconfig (with `tls-server-name`).
- **Workflows:**
  - `ci.yml` gained a `manifests` job: render both overlays, validate them with kubeconform against k8s 1.36, and syntax-check the scripts.
  - `deploy.yml` runs after a green ci on `main`, or by hand with a SHA to roll back:
    1. SHA-tagged images to GHCR;
    2. an ephemeral Tailscale node (`tag:ci`);
    3. `scripts/deploy/apply.sh`;
    4. a smoke test that also asserts an anonymous admin call gets 401.
  - If the secrets aren't set yet, the deploy is skipped with a notice instead of failing.
- **Admin security** (ADR-0010):
  - cookie login with a PBKDF2-SHA256 hash (600k iterations; the Api has a `hash-password` command), plus lockout after 5 failures;
  - rate limits: 240/min per client globally, 10/min on login, 3 concurrent SSE streams;
  - `Secure`/`HttpOnly`/`SameSite=Strict` cookies, and antiforgery on every form.
- **Docs:** ADR-0016, four runbooks, `learning/M4-deploy.md`, and SPEC §8.2 and §15 updated.
- **Tests:** 37 integration tests, including the new hash and lockout tests.

**Rehearsal (k3s v1.36.4 in Docker, the same version as the server), each item verified**
| Check | Result |
|-------|--------|
| Full deploy, then a redeploy with `apply.sh` as the scoped ServiceAccount | both succeed; migrations finish before any workload starts |
| Deployer permissions (`kubectl auth can-i`) | it can manage Deployments, Secrets, Roles and NetworkPolicies in `noisecut`; it **cannot** touch `default`, read `kube-system` secrets, create namespaces or list nodes |
| Public surface | dashboard and JSON are public; admin POST → 401; `/admin` → 302 to login; an unsigned webhook → 401; a login POST without the antiforgery token → 400 |
| NetworkPolicies, tested from a pod in `default` | the Api is reachable; PostgreSQL and Redpanda are not |
| Force-delete `engine-1` | back and ready in 3 s, holding the same partitions (epoch 2, restored state); the other engines didn't rebalance |
| Detection on k3s | the kitchen meltdown alerts in about 10 s; Prometheus discovers all 6 app pods |

**Bugs the rehearsal caught before the server did**
- Grafana: `CreateContainerConfigError`. The base referenced a ConfigMap generated in the overlay, and kustomize doesn't rewrite that
  name's hash suffix across the boundary. Fix: `disableNameSuffixHash` on `noisecut-public`.
- Mailpit crashed on the read-only root filesystem. Fix: an `emptyDir` at `/tmp`.

**Deferred:** rule editing + LISTEN/NOTIFY hot reload (SPEC §8.3). The read-only showcase never edits rules; the engine still polls every 30 s.

**Owner action before the first deploy:** the one-time [bootstrap](runbooks/bootstrap-home-server.md) (ServiceAccount, Tailscale
policy, tunnel rule, GitHub secrets). Until then the deploy job skips itself.

**Next:** M5 Report. One page of numbers, the README diagram, a recording of Break #1 vs. robust.

---

## 2026-10-21 — M3: Prove (fix every failure, measure again)

**What**
- **Checkpointed, per-partition order stage** (`Engine/Robust/`):
  - one `RuleEngine` per partition, with versioned JSON state;
  - checkpoints = producer flush, then state + next offset in one PostgreSQL transaction (migration 0003), then a Kafka commit for lag visibility;
  - cooperative-sticky rebalancing with restore on assign and checkpoint on revoke;
  - a failed produce stops the stage.
- **Fencing and fast restarts** (ADR-0014): PostgreSQL epoch fencing tokens, plus Kafka static membership.
- **Event time:** per-partition watermarks, 2 minutes of allowed lateness, a `late-events` topic, and order-insensitive absence.
- **Zone stage:** `ZonePartialEmitter` → `zone-partials` → `ZoneStageEngine` (latest-by-seq merge, minimum source watermark), plus live `zone_hourly_stats` writes.
- **Notifier policy:** cooldown, grouping (`GroupFlusher`), escalation (`Escalator`), email via Mailpit (MailKit), and `POST /api/alerts/{key}/ack`.
- **Harness:** profile-tagged result files, a `--rate`/`--round-seconds` restart for the crash test, and the `load` experiment (Experiment 5).
- **Defaults:** Compose defaults to `robust` now; `NOISECUT_PROFILE=naive` brings M2 back.
- **Tests:** 118 unit tests (determinism across checkpoint/restore, event time, lateness, cohorts, zone merge) and 28 integration tests (fencing, cooldown, grouping, escalation).

**Results** (before → after, on the same laptop)
| Experiment | Before | After |
|------------|--------|-------|
| 1: 5 kills mid-incident | 4 extra alerts; detection 58 s when killed early | 0 extra; 19–29 s regardless |
| 1: **20 kills under 2× load** (criterion 3) | — | **21/21 detected, 0 extra alerts, 0 duplicate webhooks** |
| 2A: out-of-order | 43 false alerts/sim-hour | **0** |
| 2B: 120 s stall | 6 + 1,428 false alerts | **0** |
| 3: storm, 8 restaurants | 1,164 notifications, 566 duplicates | **4 notifications, 0 duplicates** |
| 4: z-03, 3 engines | missed | **detected in 222 s** (1 engine: 214 s) |
| 5: load | — | p95 detection latency ≈ 10 ms at 1×, 2× and 4× (782 events/s); p99 ≤ 166 ms |

**Findings (each written up)**
- **Rule B measured two populations** (ADR-0015). The first robust demo raised a false spike after a demand drop, because cancellations
  trail placements by up to 35 min. Cohort attribution fixed it. The Experiment 4 control was re-run on the new rule so before/after compare like with like.
- **Dropping too-late events broke the absence rule** (Exp 2 page). The first robust Experiment 2 had *more* false alerts (86) than naive (61).
  Absence rules now still apply late events. The code had diverged from SPEC §5.4.
- **Restarts used to stall for 45 s** waiting for the dead member's session to expire. Static membership removed that.
- **Group generation as an epoch was not available** from the .NET client. A PostgreSQL fencing token is simpler and testable.
- **A transitive package had published vulnerabilities**: MailKit pulled `System.Security.Cryptography.Pkcs` 10.0.0, and
  warnings-as-errors stopped the build. It is now pinned to 10.0.13 with central transitive pinning.

**Next:** M4 Deploy. Hardened images, k3s manifests, GitHub Actions CD over Tailscale, admin login, and the public read-only showcase.

---

## 2026-10-19 — M2: Break (measure every failure, fix none)

**What**
- **Metrics**: OpenTelemetry → Prometheus exporter on an internal port in every service, with the instruments from SPEC §10
  (`NoiseCutMetrics.cs`). Prometheus and Grafana are in Compose, and the "NoiseCut overview" dashboard is provisioned from git.
- **Truth log**: the simulator records every injected scenario (`sim_scenarios`, migration 0002). SQL views grade every alert as a
  true or false positive, and `/api/truth` serves the result.
- **Chaos**: hold-back (out-of-order), duplicates, a late burst, and a `slow-kitchen` negative-control scenario.
  `Simulator:StartAtLocalHour` pins the demand curve.
- **Experiment harness**: `tools/NoiseCut.Experiments` (ADR-0013) runs each experiment on a fresh stack and writes JSON results.
  `scripts/break/*` are thin wrappers.
- **Tests**: 102 unit tests and 21 integration tests, including the grading views.

**Results** (details on each page in `docs/experiments/`)
| # | Break | Headline number |
|---|-------|-----------------|
| 1 | SIGKILL the engine mid-incident (5 kills) | 0 missed; a second alert for the same incident after **4 of 4** kills that followed detection; 0 duplicate webhooks, because the repeat had a *new* key |
| 2A | 1% of events held back up to 60 s | **43 false stuck-pickups per sim hour** |
| 2B | Engine paused 120 s, with near-miss restaurants | **6 false delay bursts** (exactly the 6 near-miss restaurants) and **1,428 false stuck-pickups** |
| 3 | Alert storm, 8 restaurants | 1 incident → **1,164 notifications**, peak **801/min**, 566 duplicate webhooks |
| 4 | 3 engines, no zone stage | Evenly split zone (z-03, about ⅓ per engine): the spike was **never detected**; 1 engine caught it in 230 s. Skewed zone (z-02, 70/30): detected, but every alert reported one engine's partial counts |

**Surprises (the useful part)**
- **Break #2's biggest effect was not the one predicted.** A processing-time timer fires on the wall clock: after a 2-minute stall
  every pending pickup timer fired before the backlog containing the pickups was read. It was 240× more false alerts than the
  rule-A effect I was looking for.
- **Dedup can't fix a crash on its own.** After a restart the repeated alert had a different key, because episode starts came from
  processing-time panes. The sink saw 0 duplicates while a human got two alerts. Determinism has to come first (checkpoint + event time).
- **Partition skew decides Break #4.** z-02's ten restaurants hash 7/0/3 onto the three engines' partition pairs, so one engine
  saw 70% of the zone and still detected the spike. The break shows up as wrong counts, plus a miss or a delay that depends on the zone.
- **A seeding gap produced false cancellation spikes.** Pinning the sim clock to earlier today put "7 days ago" just before the
  seeded week. The baseline fell back to the 2% floor, and normal 3.5% peak traffic crossed 2×. Fixed: the baseline lookup now also
  searches forward. The contaminated run was discarded and re-run.

**Process notes**
- The first scale run was discarded. It snapshotted counts right after an hour boundary, and compared processing-time hours
  with event-time hours. The redesign starts on an hour and reports *shares*.
- All runs used the M2 working tree (base `04db4c5` + uncommitted changes, so the result files say `+dirty`). The PR commits exactly that code.

**Next:** M3 Prove. Checkpointing, event time + watermarks, transitions + noise policy, zone stage, scale to 3, Experiment 5,
and 20 or more crashes with 0 duplicates.

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
