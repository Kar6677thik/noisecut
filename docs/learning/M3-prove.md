# M3 — fixing each failure, and proving it

M2 measured four failures. M3 fixes each one with a specific mechanism, then re-runs the same experiment on the same machine.
The numbers are in the "After" sections of [`docs/experiments/`](../experiments/README.md). This note explains the mechanisms.

---

## 1. Checkpoints: make a crash boring (fixes Break #1)

**The idea.** Recovery needs two things that must agree: *where to resume reading* (the offset) and *what we knew at that point*
(the window state). Save them **in one atomic write**, and recovery becomes "load, seek, replay" (`CheckpointedStage.cs`).

**The order of operations is the whole trick** (SPEC §6.3):
1. `producer.Flush()`: every candidate caused by records before the offset is acknowledged by the broker.
2. One PostgreSQL transaction: `state` + `next_offset` for each partition (`CheckpointStore.SaveAsync`).
3. Commit the offset to Kafka too, but only so lag dashboards are right.

Swap steps 1 and 2 and a crash can lose candidates: the offset says "done", but the broker never got them. With this order
a crash can only cause *repeats*: records after the checkpoint are processed again, and they produce **the same alert keys**,
because in event time the engine's output depends only on the records (`Replaying_records_after_the_checkpoint_repeats_the_same_alert_keys`).
The notifier's `(alertKey, status)` intake log turns the repeats into no-ops. That is "effectively once": at-least-once plus idempotency.

**Per-partition state.** A checkpoint belongs to one partition, so the robust order stage runs one `RuleEngine` *per partition*.
When a partition moves to another engine, its state moves with it.

**Fencing.** A frozen or partitioned process might wake up and write an old checkpoint over a newer one. Each owner gets an
**epoch** when it takes a partition, and every write must present it (`WHERE epoch = @mine`). The old owner's write changes 0 rows,
so it knows it lost (ADR-0014, `A_zombie_with_an_old_epoch_cannot_overwrite_the_new_owners_checkpoint`).

**Static membership.** A killed consumer stays in its group until its session times out. With `group.instance.id`, the restarted
process reclaims its partitions immediately.

## 2. Event time: let the data say what time it is (fixes Break #2)

- Each partition's **watermark** is `max(occurredAt) − 30 s`, so time moves only as fast as the data proves it has. A paused consumer
  wakes up at the same event time it stopped at, and the backlog replays in event-time order. Timers fire when the *data* says 20 minutes
  passed (`A_stalled_consumer_does_not_fire_timers_in_event_time`).
- **Ticks** keep a quiet partition's watermark moving (ADR-0012).
- **Allowed lateness (2 min):** an event behind the watermark is still applied, while older ones go to `late-events` and are counted (`Engine.Classify`).
- **Order-insensitive absence:** a pickup that arrives before its `food_ready` is remembered, so the late `food_ready` never arms a timer.

## 3. Transitions and the noise policy (fixes Break #3)

| Stage | Where | What it removes |
|-------|-------|-----------------|
| `OnTransition` | engine | re-emitting a firing alert on every matching event |
| Dedup | notifier intake | replays and duplicates of the same (alert, transition) |
| Cooldown | notifier intake | a new episode for the same rule + entity soon after the last notification |
| Grouping | notifier intake + `GroupFlusher` | N alerts of one rule in one zone within `groupWait` → 1 message |
| Escalation | `Escalator` | nothing. It *adds* a louder message when nobody acknowledged |

Two rules that are easy to miss:
- **Never suppress a resolution by cooldown**, but **skip a resolution nobody was told about**.
- **Escalation is wall clock.** People live in wall time, even when detection is event time (SPEC §5).

## 4. The zone stage: a shuffle (fixes Break #4)

When the aggregation key (zone) differs from the partition key (restaurant), each partition can only produce a **partial** answer.
The fix is a second stage, keyed by the aggregation key:
1. Each order-stage partition publishes **cumulative** per-zone counts to `zone-partials`, keyed by zone, at every pane close (`ZonePartialEmitter`).
2. The zone stage keeps the **latest partial per source partition** (by `seq`) and sums them (`ZoneStageEngine`).
3. Its watermark is the **minimum** source watermark: it can't be sure about a zone until every partition has reported.

Cumulative, not deltas: a replayed or duplicated partial carries the same or an older total, so applying it changes nothing.
That is why the shuffle survives crashes without any extra machinery. This is MapReduce's shuffle, Kafka Streams' `repartition()`,
and Flink's `keyBy()`, built by hand.

## 5. A rule that measured the wrong thing (ADR-0015)

The first robust demo raised a cancellation spike in a zone where nothing happened. The rule divided *cancellations arriving
this hour* by *orders placed this hour*. Cancellations trail placements by up to 35 minutes, so whenever demand drops, the trailing
cancellations of the busier period inflate the rate. The simulator's daily curve halves demand at 15:00, so this would have fired every afternoon.
The fix is **cohort attribution**: a cancellation counts towards the hour its order was placed in.

Lesson: check that a ratio's numerator and denominator describe the **same population**. Many real dashboards don't.

## 6. Try it

- `docker compose ... up -d`, inject a meltdown, then `docker compose ... kill engine`, wait 5 s, `start engine`. The engine log says
  `took partition N (epoch 2), resuming at <offset> with restored state`, and `/admin/sinks/webhook` shows no duplicates.
- `docker exec noisecut-postgres-1 psql -U noisecut -d noisecut -c "select stage, partition, epoch, next_offset, pg_size_pretty(length(state::text)::bigint) from engine_checkpoints"`
  shows what is actually saved.
- `NOISECUT_PROFILE=naive` brings back every M2 failure; each `Engine:*` / `Notifier:*` key can be flipped on its own (ADR-0008).
