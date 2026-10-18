# NoiseCut

**A real-time business alerting engine that catches real incidents early without drowning people in noise.**
A learning-scale rebuild of the problem behind Swiggy's Klaxon platform.

> **Status:** M1 done. It works end to end in the deliberately **naive** profile. Next: M2 (measure each failure).
> See the [milestones](docs/plan/MILESTONES.md).

```
Simulator ──▶ Redpanda ──▶ Engine ──▶ alert-candidates ──▶ Notifier ──▶ dashboard (SSE) · webhook
 (orders)    (order-events)  (windows,                      (intake log,
                              rules)                         outbox)
```

Rules it detects (from JSON definitions in PostgreSQL):
- **Delay burst:** a restaurant has 5+ orders delayed by over 15 min in the kitchen within 10 min.
- **Cancellation spike:** a zone's cancellation rate is 2× the same hour last week.
- **Stuck pickup:** an order is not picked up 20 min after it was ready.

What it is really about: event time vs. processing time, watermarks and late data, checkpointed state,
idempotent consumers, repartitioning, and alert-fatigue controls. Each one is shown first as a measured failure, then as a fix.

**Stack:** .NET 10 · Redpanda · PostgreSQL · Docker Compose (local) · k3s + GitHub Actions (deploy, M4)

## Quick start

Needs Docker (and the .NET 10 SDK only if you want to run the tests).

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --build
./scripts/demo.ps1 -NoStart        # or: NO_START=1 ./scripts/demo.sh
```

The demo injects the three reference incidents and waits for each alert. That takes about 5 minutes; the simulated clock runs 10× faster.

| URL | What |
|-----|------|
| http://localhost:8080 | Dashboard: live feed, alerts, rules |
| http://localhost:8080/admin | Inject scenarios (local-only admin bypass) |
| http://localhost:8090 | Redpanda Console: topics, partitions, messages |
| http://localhost:8081/status | Simulator status (internal API) |

Tests: `dotnet test --project tests/NoiseCut.Core.Tests` (fast, no Docker) and `dotnet test --project tests/NoiseCut.IntegrationTests` (needs Docker).

Stop: `docker compose -f deploy/compose/docker-compose.yml down` (add `-v` to also delete the local data volumes).

📚 Start here: [`docs/README.md`](docs/README.md)
