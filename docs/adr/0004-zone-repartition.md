# ADR-0004: Cross-partition zone rules via a repartition topic

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0

## Context
With events keyed by restaurant (ADR-0003), a zone's orders are spread over several partitions.
With 3 engine instances, each one sees only part of each zone. A zone cancellation rate computed locally would be wrong.
This bug is invisible with 1 consumer and appears only when scaling, so it is a great "Break" experiment (#4).

## Decision
Two-stage processing, the same idea as Kafka Streams' `repartition()` and Flink's `keyBy()`:
1. **Order stage** keeps per-partition *cumulative* `(placed, cancelled)` per `(zone, hour)` and publishes them to `zone-partials`, keyed by `zoneId`.
2. **Zone stage** consumes `zone-partials` and keeps the latest partial per source partition (by `seq`). Its totals are the sum of those partials, and its watermark is the minimum across source partitions.

Partials are **cumulative, not deltas**. Re-delivering or replaying one never double-counts, which makes the stage naturally idempotent.

## Alternatives considered
- **Every instance consumes everything for zone rules** (a second consumer group per instance): this works, but it is broadcast, not scaling. It falls over as the stream grows.
- **Shared counters in PostgreSQL/Redis**: a network round trip per event, plus write contention. It also hides the streaming lesson.
- **Re-key the source by zone**: see ADR-0003.

## Consequences
- Good: correct at any consumer count, and it teaches shuffle, idempotent aggregation and min-watermark merging.
- Cost: one more topic, one more checkpointed stage, and slightly higher latency for rule B (it waits for pane closes). That is acceptable because hourly ratios are not latency-critical.
