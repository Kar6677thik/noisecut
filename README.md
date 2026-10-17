# NoiseCut

**A real-time business alerting engine that catches real incidents early without drowning people in noise.**
A learning-scale rebuild of the problem behind Swiggy's Klaxon platform.

> **Status:** M0 — specification. No code yet. See the [milestones](docs/plan/MILESTONES.md).

```
Simulator ──▶ Redpanda ──▶ Engine ×3 ──▶ alert-candidates ──▶ Notifier ──▶ dashboard · webhook · email
 (orders)    (order-events)  (windows, watermarks,           (dedup, cooldown,
                              checkpoints)                     grouping, escalation)
```

Rules it detects (from JSON definitions in PostgreSQL):
- **Delay burst:** a restaurant has 5+ orders delayed by over 15 min within 10 min.
- **Cancellation spike:** a zone's cancellation rate is 2× the same hour last week.
- **Stuck pickup:** an order is not picked up 20 min after it was ready.

What it is really about: event time vs. processing time, watermarks and late data, checkpointed state,
idempotent consumers, repartitioning, and alert-fatigue controls. Each one is shown first as a measured failure, then as a fix.

**Stack:** .NET 10 · Redpanda · PostgreSQL · Prometheus + Grafana · Docker Compose (local) · k3s + GitHub Actions (deploy)

📚 Start here: [`docs/README.md`](docs/README.md)
