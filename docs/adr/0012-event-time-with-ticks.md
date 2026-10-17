# ADR-0012: Event-time processing with source ticks for watermarks

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0

## Context
Rules talk about *when things happened* ("within 10 minutes", "20 minutes after ready"). Processing time breaks under lag, restarts and
out-of-order delivery (Break #2). Event time needs **watermarks** to decide when a window is complete or a timer is due.
A per-partition watermark only advances when that partition receives events, so a quiet partition stalls every timer behind it.
The simulator also runs faster than real time (`TimeScale`), so the wall clock can't stand in for event time.

## Decision
- Windows and timers use `occurredAt`. Per-partition watermark = max event time − `MaxOutOfOrderness` (30 s), with `AllowedLateness` 2 min.
- The **source emits `clock.tick` events to every partition every wall second** (a "punctuation" or source-watermark technique), so watermarks advance even on idle partitions.
- Late but allowed events are applied (and can still trigger alerts). Events beyond that go to `late-events` and are counted.

## Alternatives considered
- **Idle-partition timeout using the wall clock** (Flink's `withIdleness`): needs a wall↔sim clock mapping, and is harder to reason about in tests.
- **Processing time**: kept only as the naive mode, to demonstrate the failure.

## Consequences
- Good: deterministic. Replaying the same partition log gives the same watermarks, timers and alert keys (needed by ADR-0006).
- Cost: ticks are a contract with the producer. Real systems with many independent producers need different watermark strategies (discussed in the learning notes).
