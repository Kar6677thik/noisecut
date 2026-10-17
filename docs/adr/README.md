# Architecture Decision Records (ADRs)

An ADR answers **"why is it like this?"** for a decision that is hard or expensive to reverse.
Each one is short: the context, the decision, the alternatives we rejected, and the consequences we accept.

- ADRs are **immutable once accepted**. To change a decision, write a new ADR that *supersedes* the old one, and link both ways.
- Status values: `Proposed` → `Accepted` → (`Superseded by ADR-XXXX` | `Deprecated`).
- Template: [`0000-template.md`](0000-template.md).

| # | Decision | Status |
|---|----------|--------|
| [0001](0001-dotnet-10-stack.md) | C# / .NET 10 for every service | Accepted |
| [0002](0002-redpanda-broker.md) | Redpanda as the Kafka-compatible broker | Accepted |
| [0003](0003-partition-by-restaurant.md) | Key `order-events` by restaurant, 6 partitions | Accepted |
| [0004](0004-zone-repartition.md) | Cross-partition zone rules via a repartition topic | Accepted |
| [0005](0005-checkpoint-state-with-offsets.md) | Checkpoint window state and offsets together in PostgreSQL | Accepted |
| [0006](0006-effectively-once-via-idempotency.md) | Effectively-once notifications via deterministic alert keys | Accepted |
| [0007](0007-transactional-outbox.md) | Transactional outbox for notification delivery | Accepted |
| [0008](0008-naive-modes-as-flags.md) | Keep naive behaviours as config flags | Accepted |
| [0009](0009-deploy-k3s-ghcr-tailscale.md) | Deploy to k3s via GHCR + Tailscale + scoped ServiceAccount | Accepted |
| [0010](0010-public-readonly-dashboard.md) | Razor Pages dashboard, public read-only, admin login | Accepted |
| [0011](0011-dapper-and-sql-migrations.md) | Dapper + plain SQL migrations instead of EF Core | Accepted |
| [0012](0012-event-time-with-ticks.md) | Event-time processing with source ticks for watermarks | Accepted |
