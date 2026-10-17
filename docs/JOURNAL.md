# Engineering journal

A dated log of **what** was done, **why**, and **what's next**. Newest entry at the top.
Each milestone PR adds at least one entry. Decisions that need more than a line get an ADR (`docs/adr/`), and the entry links to it.

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
