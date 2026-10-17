# ADR-0007: Transactional outbox for notification delivery

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0

## Context
The notifier must (1) record that it decided to notify and (2) actually call the webhook/SMTP server.
Calling the external system inside a DB transaction is a "dual write": if one side succeeds and the other fails, they diverge.

## Decision
Write the decision and a `deliveries` row in **one transaction** (the outbox). A separate dispatcher loop claims due rows
with `FOR UPDATE SKIP LOCKED`, sends them, and marks them sent, or reschedules with exponential backoff + jitter. After 8 attempts a row becomes `dead`.

## Alternatives considered
- **Send inline, then commit**: a crash between the send and the commit causes a duplicate on retry, or the reverse causes a lost notification.
- **Publish to another Kafka topic for delivery**: just moves the dual write somewhere else.

## Consequences
- Good: crash-safe. Retries are visible in the DB. `SKIP LOCKED` lets several notifier replicas share the work with no coordinator.
- Cost: delivery adds the dispatcher poll interval (default 250 ms) to latency.
