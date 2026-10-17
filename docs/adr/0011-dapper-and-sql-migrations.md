# ADR-0011: Dapper + plain SQL migrations instead of EF Core

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0

## Context
The interesting database work here is precise SQL: `ON CONFLICT DO NOTHING` for idempotency, `FOR UPDATE SKIP LOCKED` for the outbox,
single-transaction checkpoint upserts, and `LISTEN/NOTIFY`. An ORM would hide exactly these.

## Decision
Use **Npgsql + Dapper** with hand-written SQL, and versioned `.sql` migrations applied by `NoiseCut.Migrator` (DbUp-style journal).

## Alternatives considered
- **EF Core**: great for CRUD and migrations, but `SKIP LOCKED`, upsert semantics and raw `jsonb` end up as raw SQL anyway.

## Consequences
- Good: every query is visible and reviewable, and integration tests hit real PostgreSQL via Testcontainers.
- Cost: more boilerplate mapping code.
