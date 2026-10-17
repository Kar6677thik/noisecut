# Concepts primer: what you need to know to understand NoiseCut

Read this before M1. Each concept has: **what it is**, **why NoiseCut needs it**, and **where it shows up**.
Field Guide section numbers are in brackets, e.g. [FG 18].
Later milestones append their own "what I learned" notes in this folder.

---

## 1. Event streaming basics [FG 18, 28]

### Topic, partition, offset
A **topic** is a named, append-only log. It is split into **partitions**. Each partition is an ordered sequence of records,
and each record has an **offset** (its position: 0, 1, 2, …). Records are not deleted when read. They expire by retention.
- *Why here:* we can replay history, which makes crash recovery and experiments possible.
- *Where:* `order-events`, `zone-partials`, `alert-candidates`, … (SPEC §4.4).

### Keys decide partitions
A producer hashes the record **key** to pick a partition. Same key → same partition → **ordered** relative to each other.
There is no ordering guarantee *across* partitions.
- *Why here:* keying by `restaurantId` keeps an order's `food_ready` and `picked_up` in order (ADR-0003).

### Consumer groups and rebalancing
All consumers with the same `group.id` share the topic's partitions. **Each partition is owned by exactly one member at a time.**
When members join or leave (scale-up, crash), the group **rebalances**: partitions are revoked from some members and assigned to others.
- *Why here:* scaling to 3 engines means each one owns 2 of the 6 partitions. On rebalance, a new owner must get the old owner's state.
  That is why checkpoints are per partition (ADR-0005).
- *Gotcha:* more consumers than partitions → some consumers sit idle.

### Committing offsets
A consumer **commits** "I've processed up to offset N". On restart, it resumes from the committed offset.
- Commit **before** processing → a crash loses work (**at-most-once**).
- Commit **after** processing → a crash redoes work (**at-least-once**).
- Auto-commit commits on a timer, unrelated to your processing. That gives you whichever of the two the timing happens to produce. This is naive mode.

### Delivery semantics
| Semantics | Can lose? | Can duplicate? | How |
|-----------|-----------|----------------|-----|
| At-most-once | yes | no | commit first |
| At-least-once | no | yes | commit after |
| "Exactly-once" | no | no | only *inside* Kafka with transactions; to the outside world it's **at-least-once + idempotency** (ADR-0006) |

---

## 2. Time [FG 28]

### Event time vs. processing time
- **Event time:** when it happened (`occurredAt`).
- **Processing time:** when your code sees it.
They differ because of network delay, retries, consumer lag and restarts. Rules about the real world must use event time,
otherwise a backlog replayed after a restart looks like a sudden burst (Break #2).

### Windows
- **Tumbling:** fixed, non-overlapping (`[10:00, 11:00)`, `[11:00, 12:00)`). Rule B uses hourly tumbling windows.
- **Sliding / hopping:** fixed size, moving by a step (10 min wide, every 1 min). Rule A.
- **Session:** closes after a gap of inactivity. Not used.
- **Panes:** we implement sliding windows as 1-minute buckets ("panes") and sum the last 10. This is the same "split a range into buckets and sum them" idea as the Grab counter service in Project 2.

### Watermarks
A **watermark** *W* is a claim: "I don't expect any more events with time < *W*." It lets the engine decide:
- that a window is **complete** (so it can say "the count was low", i.e. resolve), and
- that a **timer is due** (rule C: 20 minutes passed and still no pickup).
Our heuristic: `W = maxEventTimeSeen − 30s`. Too small means more late events. Too large means slower decisions. This is a trade-off, not a fact.

### Late events and allowed lateness
An event older than the watermark is **late**. Within `AllowedLateness` (2 min) we still apply it. Older than that, we divert it
to `late-events` and count it. Something always has to give: unbounded lateness means unbounded state.

### Idle partitions and ticks
A watermark advances only when a partition receives events. A quiet partition freezes timers behind it.
Our simulator writes `clock.tick` to every partition every second (ADR-0012).

### Monotonic vs. non-monotonic conditions
"Count ≥ 5" can only become *more* true as events arrive, so we can fire **early**, before the window closes. That is fast detection.
"Ratio ≥ 2×" can go up and down, so we need guards and must be careful about firing on partial data.

---

## 3. State and recovery [FG 14, 18]

### Stateful stream processing
Windows, counts and timers are **state**. In-memory state disappears with the process.

### Checkpoints
A checkpoint stores *state + the offset that state corresponds to* **atomically**. Recovery means restoring the state, seeking to the offset and replaying.
If the two are saved separately, they can disagree and you double-count or lose data.

### Determinism
If processing depends only on the records (not on wall clock or randomness), replaying the same records produces the same output, including the same **alert keys**.
That determinism is what lets downstream deduplication work.

### Repartitioning (shuffle)
When you need to aggregate by a different key than the stream is partitioned by (zone vs. restaurant), you re-key the data into a new topic.
Kafka Streams calls this `repartition()`, Flink calls it `keyBy()`, and MapReduce calls it "the shuffle" (ADR-0004).

---

## 4. Reliable side effects [FG 11, 17, 18]

### Idempotent consumer
Processing the same message twice has the same effect as processing it once. Implemented with a unique constraint and `ON CONFLICT DO NOTHING`.

### Transactional outbox
Never "write the DB and call the API" as two separate steps that can half-fail. Write the intent to an `outbox` table in the same transaction,
and let a dispatcher deliver it with retries (ADR-0007).

### Retries with backoff and jitter
Retry transient failures, waiting longer each time (exponential) with randomness (jitter), so many retries don't synchronize into a thundering herd.

### Poison messages and DLQs
A message that can never be processed (bad JSON) must not block the partition forever. Move it to a dead-letter topic and continue.

---

## 5. Alert quality [FG 24]

| Technique | Problem it solves | NoiseCut setting |
|-----------|-------------------|------------------|
| **Dedup** | The same alert reported twice (replay, retries) | `(alertKey, status)` unique |
| **Episodes / transitions** | A condition that stays true re-alerts on every event | `EmitMode=OnTransition` |
| **Cooldown** | Flapping: true/false/true every minute | `notify.cooldown` |
| **Grouping** | 20 restaurants in one zone → 20 pings | `notify.groupBy`, `groupWait` |
| **Escalation** | Nobody looked → tell someone louder | `notify.escalation` |
| **Guards** | Ratios on tiny samples are noise | `minDenominator`, `minBaselineRate` |

Alert fatigue is a real safety problem: people who get 500 pings learn to ignore all of them, including the one that matters.

---

## 6. Observability [FG 24]

- **Counter / gauge / histogram:** a counter only goes up, a gauge goes up and down, a histogram records a distribution so you can compute p95/p99.
- **Percentiles (p95):** "95% of alerts were detected within X s". Averages hide the slow tail that users actually feel.
- **Cardinality:** each distinct label value creates a new time series. `orderId` as a label would create millions, so never do it.
- **Consumer lag:** how far behind the log head a consumer is. It is the main health signal for any stream processor.

---

## 7. Deployment [FG 21, 22, 25]

- **Image tag = git SHA:** every deployed artifact traces back to an exact commit, and a rollback means deploying an older SHA.
- **Least privilege:** CI gets a ServiceAccount that can only touch the `noisecut` namespace (ADR-0009).
- **Private network for control, tunnel for public traffic:** CI reaches the cluster API over Tailscale. Visitors reach the dashboard through a Cloudflare Tunnel. The cluster API is never public.
- **Probes:** liveness ("restart me if I'm stuck") vs. readiness ("don't send me traffic yet").

---

## Suggested reading order (when you want depth)

1. Martin Kleppmann, *Designing Data-Intensive Applications*, ch. 11 (Stream Processing).
2. Tyler Akidau, "Streaming 101" and "Streaming 102" (O'Reilly articles): the origin of the watermark vocabulary.
3. Kafka documentation, "Consumer" and "Design" sections: groups, offsets, delivery semantics.
4. Flink documentation, "Timely Stream Processing": compare it with our ADR-0012.
5. Chris Richardson, *Transactional Outbox* (microservices.io).
6. Google SRE Book, "Monitoring Distributed Systems": alerting philosophy.
