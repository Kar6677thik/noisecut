# ADR-0005: Checkpoint window state and offsets together in PostgreSQL

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0

## Context
The engine's windows are in memory. If the process dies, we must restore **both** the state and the exact position in the log
that the state corresponds to. If they disagree, events are either counted twice (state ahead of offset) or lost (offset ahead of state).
Kafka's normal offset commit is separate from wherever we store state, so the two cannot be committed atomically.

## Decision
Store `(stage, topic, partition) → (next_offset, watermark, epoch, state)` in one row and write it in **one PostgreSQL transaction**.
PostgreSQL, not Kafka's committed offset, is the source of truth for where to resume. On partition assignment, load the row and `Seek()`.
Before writing a checkpoint, flush the producer, so every candidate caused by the records before `next_offset` is durable in the broker first.

## Alternatives considered
- **Kafka offsets + state in a compacted changelog topic** (how Kafka Streams does it): elegant, but it needs Kafka transactions to be atomic. That is a lot of machinery to learn at once. A possible stretch goal.
- **Embedded RocksDB + periodic snapshots** (how Flink does it): powerful, but overkill at this scale, and more ops work.
- **Rebuild state by replaying from the window start on every restart**: works for short windows, but the 1-hour windows and absence timers make restarts slow and the logic complex.

## Consequences
- Good: simple to reason about. After a crash, the state is exactly what it was at the checkpoint's offset. Replay from there is deterministic.
- Cost: replay re-emits candidates produced after the last checkpoint. These are absorbed by ADR-0006.
- Cost: zombie writers after a rebalance are only partially fenced (an epoch check). This is documented in SPEC §6.3.
