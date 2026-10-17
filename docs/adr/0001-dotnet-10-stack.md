# ADR-0001: C# / .NET 10 for every service

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0

## Context
The project guide's default stack is C#/ASP.NET Core with xUnit, and Project 3 explicitly suggests ".NET worker services".
The author's other deployed project (Slate) is .NET 10, so CI patterns, Dockerfiles and k3s deployment habits carry over.
Python is the main alternative (Faust/Bytewax/Quix Streams exist for streaming).

## Decision
All five services and the shared libraries are C# on **.NET 10** (LTS), with the SDK pinned in `global.json`.
- Workers use the Generic Host (`BackgroundService`).
- Kafka: **Confluent.Kafka** (wraps librdkafka, the most battle-tested client).
- Time: the built-in `TimeProvider` abstraction, so tests can use `FakeTimeProvider`.

## Alternatives considered
- **Python + a streaming library**: these libraries hide windows, watermarks and checkpoints, which are exactly the things this project exists to teach. Writing them by hand in Python would be fine, but it splits the stack across projects for no learning gain.
- **Kafka Streams / Flink (JVM)**: the "real" tools for this job. We deliberately *re-implement a tiny subset* to understand them. Reading Flink's docs on watermarks afterwards should feel familiar.

## Consequences
- Good: one language, one test framework, and strong typing for the event and rule models.
- Cost: we hand-write stream-processing machinery that a framework would provide. That is intentional.
- Follow-up: the M5 report compares our design to Kafka Streams/Flink concepts.
