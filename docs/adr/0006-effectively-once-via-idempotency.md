# ADR-0006: Effectively-once notifications via deterministic alert keys

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0

## Context
Across a crash, a stream processor can give you *at-most-once* (may lose) or *at-least-once* (may duplicate).
"Exactly-once" in Kafka only covers Kafka-to-Kafka with transactions. It never covers an HTTP webhook or an email.
The goal is **zero duplicate notifications** after forced crashes.

## Decision
- The engine is **at-least-once** (ADR-0005 guarantees nothing is lost).
- Every candidate carries an `alertKey` computed **only from replay-stable data**: `ruleId:ruleVersion:entityKey:episodeStart` (event time). It never uses wall clock, random IDs or the instance name.
- The notifier dedups on `(alertKey, status)` with a unique constraint, in the same transaction that records the decision.
- Outgoing requests carry `Idempotency-Key`. Our sink records receipts, so duplicates are *measured*, not assumed.

**at-least-once delivery + idempotent processing = effectively-once results.**

## Alternatives considered
- **Kafka transactions (EOS v2)**: they protect the engine→`alert-candidates` hop, but not the notifier→outside-world hop. We would still need idempotency at the edge, so it adds complexity without removing that need.
- **Dedup by `candidateId`**: random per emission, so a replayed candidate gets a new ID. This is wrong and is a common mistake.

## Consequences
- Good: the same mechanism also absorbs the simulator's duplicate events and naive-mode spam.
- Cost: alert-key determinism is now a contract. Tests must assert that replaying the same records yields identical keys.
