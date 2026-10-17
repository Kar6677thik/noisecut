# ADR-0002: Redpanda as the Kafka-compatible broker

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0

## Context
We need a durable, partitioned, replayable log with consumer groups. That means the Kafka model.
The home server has about 7.6 GiB of RAM, shared with other apps.

## Decision
Use **Redpanda** (single node). It speaks the Kafka protocol, ships as one binary with no ZooKeeper,
and can be capped to `--smp 1 --memory 1G`. Clients are standard Kafka clients, so all knowledge transfers to Apache Kafka.

## Alternatives considered
- **Apache Kafka (KRaft)**: heavier JVM footprint on a shared home server, with no extra learning value at this scale.
- **RabbitMQ / Redis Streams**: different semantics (queues or simpler streams). Partition-ordered replay, offsets and consumer-group rebalancing are core to the lesson.
- **PostgreSQL as a queue**: good to know, wrong tool for replayable event streams.

## Consequences
- Good: light, fast to start in tests (Testcontainers), and it has a built-in console/`rpk` CLI for inspecting topics.
- Cost: single node means no broker replication (`replication.factor=1`). Accepted for a lab, and listed in the spec's non-goals.
