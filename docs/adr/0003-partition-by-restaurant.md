# ADR-0003: Key `order-events` by restaurant, 6 partitions

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0

## Context
Kafka only guarantees order **within a partition**, and one partition is consumed by exactly one member of a consumer group.
The partition key therefore decides (a) which events are ordered relative to each other and (b) how work splits across consumers.
Rules A and C are per restaurant / per order. Rule B is per zone.

## Decision
Key by `restaurantId`. All events for one order (and one restaurant) land in one partition, in order, on one consumer.
Use **6 partitions**, which split evenly across 1, 2, 3 or 6 consumers.

## Alternatives considered
- **Key by `orderId`**: perfect spreading, but rule A (per-restaurant counts) would then need cross-partition aggregation too.
- **Key by `zoneId`**: makes rule B local, but there are only 6 zones, so one busy zone becomes a hot partition, and we could never scale past 6 consumers in a meaningful way.

## Consequences
- Good: rules A and C are fully local. No coordination needed.
- Cost: rule B needs a repartition step (ADR-0004). This is a real-world lesson in itself (a "shuffle").
- Note: partition count is effectively permanent. Changing it later remaps keys to partitions and breaks per-key ordering during the change.
