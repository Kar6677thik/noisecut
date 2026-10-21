# ADR-0014: Checkpoint fencing with a PostgreSQL epoch, plus Kafka static membership

- **Status:** Accepted (refines the fencing note in ADR-0005 and SPEC §6.3)
- **Date:** 2026-10-21
- **Milestone:** M3

## Context
SPEC §6.3 planned to fence "zombie" writers with the consumer-group generation: each checkpoint row would carry the generation,
and a write with a lower one would be rejected. While building it we hit two problems:
- The .NET Kafka client does not expose the group generation as a number.
- A generation counts rebalances of the *whole group*. What we need to know is narrower: "is this process still the owner of this partition's checkpoint?"

A separate problem showed up in the first crash test. A SIGKILLed consumer stays a group member until its session times out (45 s by default).
The restarted process has to wait that long before it gets its partitions back, so detection pauses for 45 s after every crash.

## Decision
1. **Fencing token from PostgreSQL.** Taking a partition (`CheckpointStore.AcquireAsync`) atomically bumps `engine_checkpoints.epoch` for that row and returns the new value.
   - Every checkpoint write includes `WHERE epoch = @myEpoch`.
   - A process that lost the partition without noticing writes 0 rows. It sees that, and drops the partition locally.
   - This is the classic *fencing token* pattern (Kleppmann, "How to do distributed locking").
2. **Static membership (KIP-345).** Each robust stage sets `group.instance.id = <hostname>-<pid>-<stage>`, and `session.timeout.ms` drops to 10 s.
   - In a container the pid is always 1, so the id survives a restart, and the restarted process reclaims its assignment immediately.
   - The broker itself fences the old incarnation.
   - With `--scale`, each replica has its own hostname.

## Alternatives considered
- **Group generation**: not available from the client, and coarser than needed.
- **Kafka transactions** (transactional producer + `sendOffsetsToTransaction`): this fences zombies inside Kafka. But our state lives in PostgreSQL, so it would need two-phase commit across systems. ADR-0006 already rejected that.
- **A shorter session timeout without static membership**: smaller stalls, but every restart still causes a full rebalance.

## Consequences
- Good: fencing is per partition, testable against real PostgreSQL (`CheckpointStoreTests`), and independent of Kafka client internals.
- Good: a crashed-and-restarted engine resumes within seconds.
- Cost: each assignment costs one PostgreSQL round trip per partition. That is negligible at 6 partitions.
- Cost: with static membership a clean shutdown does not leave the group immediately. Its partitions wait up to 10 s (the session timeout) before moving to another instance.
