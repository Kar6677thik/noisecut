# NoiseCut — Specification (v1)

> **Status:** M0 (specification). This document says **what** we build and the exact behaviour we expect.
> The **why** behind each big choice lives in [`docs/adr/`](../adr/). The **when** lives in
> [`docs/plan/MILESTONES.md`](../plan/MILESTONES.md) and [`docs/JOURNAL.md`](../JOURNAL.md).
> If code and this spec disagree, one of them is a bug: fix the code or update the spec in the same PR.

---

## 1. Problem statement

Swiggy's in-house platform **Klaxon** fires over 1.5 million business alerts a day. It runs complex
event processing over order data and routes alerts (fragile items, social-media complaints, delays)
to the right team over Slack, Freshdesk or SMS. The hard part is not *sending* alerts. It is
**catching real patterns early without burying people in noise.**

NoiseCut rebuilds that problem at learning scale:

1. A **simulator** produces a realistic stream of food-delivery order events.
2. A **streaming engine** evaluates JSON-defined rules over time windows.
3. A **notifier** turns rule hits into a small number of useful notifications, using
   deduplication, cooldowns, grouping and escalation.
4. Everything is measured, deliberately broken, then fixed. Each step has before/after numbers.

### 1.1 Goals

| ID | Goal |
|----|------|
| G1 | Detect the three reference incidents (§5) from a live event stream. |
| G2 | Survive consumer crashes: **zero duplicate notifications** and no lost window state. |
| G3 | Handle out-of-order and late events with event-time semantics (watermarks + allowed lateness). |
| G4 | Turn alert storms into a few notifications (dedup, cooldown, grouping), and report how many alerts were suppressed. |
| G5 | Scale the engine horizontally to 3 consumers with correct results, including cross-partition (zone) rules. |
| G6 | Measure event-to-alert detection latency (p95) under load. |
| G7 | Run publicly on the home server: anyone can view a read-only showcase, and only the admin can change anything. Deployed only through GitHub Actions. |
| G8 | Document every step well enough that the author can rebuild it from scratch. |

### 1.2 Non-goals (v1)

- Batch sources (Hive/Snowflake). The only input is the event stream.
- ML or statistical anomaly detection beyond the "ratio vs. baseline" rule.
- Real SMS/Freshdesk integrations. Slack works by pasting a real webhook URL, but the default sinks are local.
- Kafka transactions / broker-level exactly-once. We get *effectively-once notifications* through idempotency instead (ADR-0006).
- High availability for PostgreSQL or Redpanda. Each runs as a single instance on a single node.
- Multi-tenancy, user management (there is exactly one admin), and a rule DSL beyond the three rule kinds below.
- Log aggregation (Loki) and distributed tracing. These are stretch goals only.

---

## 2. Glossary (read this first)

Fuller explanations are in [`docs/learning/CONCEPTS.md`](../learning/CONCEPTS.md).

| Term | Meaning in NoiseCut |
|------|---------------------|
| **Event time** | When something happened in the simulated world (`occurredAt`). Rules are evaluated in event time. |
| **Processing time** | When our code sees the event (wall clock). Used only by naive mode, and by notifier policies (cooldowns, escalation), because humans live in wall-clock time. |
| **Sim clock** | The simulator's clock. It runs `TimeScale`× faster than the wall clock (default 10×), so an hour-long window closes in 6 real minutes. |
| **Pane** | A 1-minute event-time bucket. Sliding windows are built by summing panes. |
| **Watermark** | Per partition, the engine's claim that "no more events older than this should arrive". `watermark = maxEventTimeSeen − MaxOutOfOrderness`. |
| **Allowed lateness** | How far behind the watermark an event may be and still be applied. Anything older goes to the `late-events` topic. |
| **Tick** | A `clock.tick` event the simulator writes to *every* partition every wall second, so watermarks keep advancing even on quiet partitions. |
| **Alert candidate** | What the engine emits when a rule's condition changes (or, in naive mode, on every match). |
| **Episode** | One continuous period during which a rule is true for one entity. Starts with `firing`, ends with `resolved`. |
| **Alert key** | Deterministic identity of an episode: `ruleId:ruleVersion:entityKey:episodeStart`. It is the basis of idempotency. |
| **Notification** | One message delivered to one channel. It can cover one alert or a group of alerts. |
| **Naive / Robust profile** | Config presets: naive reproduces the "Break" failures, robust is the fixed system (ADR-0008). |

---

## 3. Architecture

```
                         ┌──────────────────────────── Redpanda ────────────────────────────┐
 ┌───────────┐  produce  │ order-events (6p, key=restaurantId) ── clock.tick on every part. │
 │ Simulator │──────────▶│                                                                   │
 │ (+control │           │ zone-partials (6p, key=zoneId)      alert-candidates (6p,         │
 │  HTTP API)│           │ late-events (1p)  order-events-dlq  key=ruleId:entityKey)          │
 └─────▲─────┘           └───┬──────────────▲──────┬───────────────────────▲──────┬────────┘
       │                     │ consume       │ produce│ consume               │ produce│consume
       │              ┌──────▼───────────────┴──────▼───────────────────────┴──┐   │
       │              │ Engine ×3  (consumer group noisecut-engine)              │   │
       │              │  • order stage: windows, watermarks, absence timers      │   │
       │              │  • zone stage: merges zone-partials (cross-partition)    │   │
       │              │  • checkpoints state+offsets ──────────────┐             │   │
       │              └────────────────────────────────────────────┼─────────────┘   │
       │                                                           ▼                 │
       │                ┌──────────────────────────── PostgreSQL ───────────────┐    │
       │                │ rules · zone_hourly_stats · engine_checkpoints ·       │    │
       │                │ alerts · alert_candidates · notification_groups ·      │◀───┤
       │                │ deliveries (outbox) · webhook_receipts                 │    │
       │                └───────▲───────────────────────────────▲───────────────┘    │
       │                        │                               │                    │
       │   ┌────────────────────┴───────────┐        ┌──────────┴─────────────────┐  │
       └───│ Api (ASP.NET Core)             │        │ Notifier                   │◀─┘
  forwards │ • public read-only dashboard   │◀─SSE───│ • dedup · cooldown · group │
 scenarios │ • admin: rules, ack, scenarios │ NOTIFY │ • outbox dispatcher        │──▶ webhook sink (Api)
           │ • webhook sink (HMAC-verified) │        │ • escalation scheduler     │──▶ email (Mailpit SMTP)
           └────────────────────────────────┘        └────────────────────────────┘──▶ Slack (optional)

 Prometheus scrapes every service's internal /metrics port → Grafana (public, anonymous Viewer)
```

### 3.1 Services

| Service | Kind | Responsibility | Replicas |
|---------|------|----------------|----------|
| `NoiseCut.Simulator` | .NET worker + minimal HTTP control API | Generates order lifecycles, scenarios, chaos (out-of-order, duplicates), ticks. | 1 |
| `NoiseCut.Engine` | .NET worker | Order stage + zone stage. Window state, watermarks, rule evaluation, checkpoints. | 1 (M1) → 3 (M3) |
| `NoiseCut.Notifier` | .NET worker | Candidate intake, noise policy, outbox, channel dispatch, escalation. | 1 (safe at N) |
| `NoiseCut.Api` | ASP.NET Core (Razor Pages + minimal APIs) | Dashboard, REST, SSE, admin auth, webhook sink, scenario forwarding. | 1 |
| `NoiseCut.Migrator` | .NET console | Applies SQL migrations, seeds rules and baseline history. Runs as a one-shot (Compose) or a Job (k3s). | one-shot |
| Infra | containers | Redpanda, PostgreSQL, Prometheus, Grafana, Mailpit. | 1 each |

### 3.2 Code layout

```
src/
  NoiseCut.Core/            # PURE logic. No I/O, no Kafka, no SQL. Fully unit-testable with a fake clock.
                            # events, rule model + validation, panes/windows, watermarks,
                            # evaluators, episode tracking, notifier policy decisions
  NoiseCut.Infrastructure/  # Kafka (Confluent.Kafka), PostgreSQL (Npgsql + Dapper), serialization,
                            # checkpoint store, outbox, OpenTelemetry metrics setup
  NoiseCut.Simulator/  NoiseCut.Engine/  NoiseCut.Notifier/  NoiseCut.Api/  NoiseCut.Migrator/
tests/
  NoiseCut.Core.Tests/          # xUnit, fast, deterministic
  NoiseCut.IntegrationTests/    # xUnit + Testcontainers (PostgreSQL, Redpanda)
deploy/
  compose/                      # local stack
  k3s/                          # kustomize base + overlays
  observability/                # prometheus config, grafana provisioning + dashboards (as code)
scripts/                        # break demos, chaos, benchmarks (bash + PowerShell equivalents)
docs/                           # spec, ADRs, learning notes, experiments, runbooks, report, journal
```

**Rule of thumb:** if a piece of logic decides *whether* something is an alert, it belongs in `Core`
and has a unit test that does not need Docker.

---

## 4. Event model

### 4.1 Domain

- **Zones**: 6 Bengaluru-style zones (`z-01` Koramangala … `z-06` Malleshwaram).
- **Restaurants**: 60, 10 per zone (`r-001` … `r-060`). Each restaurant belongs to exactly one zone. This is fixed reference data, seeded into PostgreSQL and baked into the simulator.
- **Orders**: one restaurant per order (and therefore one zone).

### 4.2 Order lifecycle

```
placed → accepted → food_ready → picked_up → delivered
   │          │           │            │
   └──────────┴───────────┴────────────┴──▶ cancelled (any time before delivered)
 delayed: emitted 0..n times between accepted and delivered when the predicted ETA slips
```

| Type | `data` payload |
|------|----------------|
| `order.placed` | `{ "itemCount": int, "valueInr": int }` |
| `order.accepted` | `{}` |
| `order.food_ready` | `{}` |
| `order.picked_up` | `{ "riderId": string }` |
| `order.delayed` | `{ "delayMinutes": int, "stage": "kitchen" \| "pickup" \| "transit" }` |
| `order.delivered` | `{}` |
| `order.cancelled` | `{ "reason": "customer" \| "restaurant" \| "no_rider" \| "other" }` |
| `clock.tick` | `{}` (no order/restaurant/zone; carries only `occurredAt`) |

### 4.3 Envelope (JSON, UTF-8)

```json
{
  "eventId": "0192f6c1-7d1e-7b3a-9c41-2f0e1a7b9c11",
  "type": "order.delayed",
  "schemaVersion": 1,
  "occurredAt": "2026-10-17T19:42:10.123Z",
  "orderId": "o-000184213",
  "restaurantId": "r-017",
  "zoneId": "z-02",
  "data": { "delayMinutes": 18, "stage": "kitchen" }
}
```

- `eventId` is a UUIDv7 and unique per *logical* event. Simulator-injected duplicates reuse the same `eventId`.
- `occurredAt` is **event time** on the sim clock.
- The Kafka record **timestamp** (producer `CreateTime`) is the **wall-clock** send time. It is used only for latency measurement and for naive processing-time mode.
- Unknown `schemaVersion` or invalid JSON → the record goes to `order-events-dlq` with the error in a header, and the consumer moves on (poison-message handling).

### 4.4 Topics

| Topic | Partitions | Key | Retention | Purpose |
|-------|-----------|-----|-----------|---------|
| `order-events` | 6 | `restaurantId` (ticks: no key, explicit partition) | 7 days | Source stream. Keying by restaurant keeps one order's events in order on one partition. |
| `zone-partials` | 6 | `zoneId` | 1 day | Repartition topic for cross-partition zone aggregation (ADR-0004). |
| `alert-candidates` | 6 | `ruleId:entityKey` | 3 days | Engine → Notifier. One alert series stays ordered. |
| `late-events` | 1 | original key | 3 days | Events beyond allowed lateness (for inspection and replay). |
| `order-events-dlq` | 1 | original key | 7 days | Unparseable/invalid records. |

Producers use `enable.idempotence=true` and `acks=all`.

---

## 5. Rules

Rules are JSON documents stored in PostgreSQL (`rules.definition jsonb`). They are validated by the Api
on write and by the engine on load. Each update bumps `version`. **A new version starts with fresh
state.** We never carry window state across a rule change, because old counts were computed under
different conditions.

**Durations** use ISO-8601 (`PT10M`). There are two kinds of duration, and mixing them up is a classic bug:
- **Detection fields** (`window`, `timeout`, `baseline.offset`) are **event time** (sim clock).
- **Notification fields** (`notify.cooldown`, `notify.groupWait`, `notify.escalation[].after`) are **wall-clock time**.

### 5.1 Common fields

```json
{
  "id": "restaurant-delay-burst",
  "name": "Restaurant delay burst",
  "description": "5+ orders delayed >15 min at one restaurant within 10 minutes",
  "enabled": true,
  "kind": "windowed_count | ratio_vs_baseline | absence",
  "severity": "low | medium | high | critical",
  "entity": "restaurantId | zoneId | orderId",
  "notify": {
    "channels": ["dashboard", "webhook"],
    "cooldown": "PT15M",
    "groupBy": "zoneId",
    "groupWait": "PT30S",
    "escalation": [ { "after": "PT10M", "channels": ["email"] } ]
  }
}
```

### 5.2 Reference rule A — `windowed_count` (restaurant delay burst)

> A restaurant has **5 or more** orders delayed by **over 15 minutes** in the kitchen within **10 minutes**.

```json
{
  "kind": "windowed_count",
  "entity": "restaurantId",
  "source": { "eventType": "order.delayed",
              "where": [ { "field": "data.delayMinutes", "op": "gt", "value": 15 },
                         { "field": "data.stage", "op": "eq", "value": "kitchen" } ] },
  "distinctBy": "orderId",
  "window": { "type": "sliding", "size": "PT10M", "slide": "PT1M" },
  "condition": { "op": "gte", "value": 5 }
}
```

Semantics:
- *M1 change:* only **kitchen** delays count. A rider shortage also produces large `pickup` delays (20–35 min), and those are rule C's job. Without this filter, one rider shortage would raise a "delay burst" for every busy restaurant in the zone as well as the stuck-pickup alerts.
- Matching events go into the 1-minute pane of their `occurredAt`. The window value at pane *p* is the number of **distinct orders** across panes *(p−9 … p)*. `distinctBy` stops one order that slips three times from counting as three delays.
- **Early firing is allowed.** The count can only grow as events arrive, so as soon as it reaches 5 the condition is definitely true. We emit `firing` immediately and do not wait for the watermark. This is what makes detection fast.
- **Resolution waits for the watermark.** The episode resolves only once the watermark has passed the end of a pane whose trailing window is below threshold. A count can only be known to be *low* after its window has closed.
- `episodeStart` = the start of the earliest pane in the window that first crossed the threshold.

### 5.3 Reference rule B — `ratio_vs_baseline` (zone cancellation spike)

> A zone's cancellation rate is **double** that of the same hour last week.

```json
{
  "kind": "ratio_vs_baseline",
  "entity": "zoneId",
  "numerator":   { "eventType": "order.cancelled" },
  "denominator": { "eventType": "order.placed" },
  "window": { "type": "tumbling", "size": "PT1H" },
  "baseline": { "offset": "P7D", "source": "zone_hourly_stats" },
  "condition": { "op": "gte_multiple", "value": 2.0 },
  "guards": { "minDenominator": 300, "minBaselineRate": 0.02 }
}
```

Semantics:
- `rate = cancelled / placed` for the zone in the current event-time hour. The baseline is the same zone and hour-of-week 7 days earlier, read from `zone_hourly_stats`. If that exact hour is missing, the lookup steps back one week at a time to the most recent hour with the same hour-of-week. *(M1: the sim clock runs 10× faster than the wall clock, so it outruns the seeded week in about 17 real hours.)* If nothing exists further back, it steps *forward* to the nearest later week. *(M2: an experiment that pinned the sim clock to earlier today fell just before the seeded week, used the 2% floor, and raised false spikes at a normal 3.5% peak rate.)*
- *M3 change (ADR-0015):* with `"cohortBy": "orderId"` (the reference rule has it), each cancellation counts towards the hour its **order was placed** in: "of the orders placed this hour, what fraction were cancelled". The arrival-time definition above compares two populations and inflates the rate whenever demand drops; the first robust demo raised a false spike that way. Cohort hours become final 45 minutes after hour end.
- **Guards against noise.** Do not evaluate until `placed ≥ minDenominator`, because 2 cancellations out of 3 orders means nothing. The baseline rate is floored: `effective = max(baseline, minBaselineRate)`. A zero or missing baseline therefore can't make a spike "infinite".
- *M1 change: `minDenominator` 20 → 300.* With 200, the no-incident scenario test (`ScenarioDetectionTests`, seed 1) raised a false spike within 2 simulated hours of normal traffic. The cause is binomial noise: at n = 200 and a true rate of 3.5%, reaching 2× the baseline is only about 2 standard deviations away, and the rule re-checks every minute. A rough estimate for n = 300 puts the chance per evaluation near 0.05%. The no-incident tests (3 seeds × 2 h) now pass. The trade-off is that the rule can only fire once 300 orders have arrived in the hour, so quiet hours are never evaluated. Guards are statistics, not decoration.
- This is a **non-monotonic** condition: the ratio can rise and then fall. It is evaluated at every pane close once the guard passes, and at most one episode exists per (zone, hour). The episode resolves when the watermark closes the hour.
- When the watermark closes an hour, the zone stage writes that hour's final `(placed, cancelled)` to `zone_hourly_stats`. After a week of running, real baselines replace the seeded ones.
- **Cross-partition:** orders for one zone are spread over many partitions, because events are keyed by restaurant. This rule therefore runs in the **zone stage**, fed by `zone-partials` (§6.4, ADR-0004).
- **Baseline seeding:** the Migrator writes 7 days of synthetic `zone_hourly_stats` from the simulator's own model (~3% cancellation rate with lunch/dinner variation), so the rule works on day one. We seed aggregates, not replayed events, because replaying a week of events would be slow and teach nothing.

### 5.4 Reference rule C — `absence` (stuck pickup)

> An order is still not picked up **20 minutes** after it was marked ready.

```json
{
  "kind": "absence",
  "entity": "orderId",
  "trigger":  { "eventType": "order.food_ready" },
  "expected": { "eventType": "order.picked_up" },
  "cancelIf": [ "order.cancelled" ],
  "correlateBy": "orderId",
  "timeout": "PT20M",
  "notify": { "groupBy": "restaurantId", "groupWait": "PT1M" }
}
```

Semantics:
- `food_ready` at time *t* arms a timer with deadline *t + 20m*, in event time.
- The timer fires when **the watermark reaches the deadline** and neither `picked_up` nor `cancelled` has been seen for that order. It is never driven by the wall clock.
- **Order-insensitive.** If `picked_up` arrives *before* `food_ready` (out-of-order delivery), the order's state remembers it, and the later `food_ready` never arms a timer. Naive mode lacks this, which produces a false alert (Break #2).
- A `picked_up` after the timer fired, including a late one, emits `resolved`.
- Per-order state is dropped when the expected or cancelling event arrives. A fired alert waits for that event, since a late pickup resolves it, but only up to `MaxOrderAge` (default `PT3H` event time). After that it is dropped without a `resolved`, so memory stays bounded.
  *(M1 correction: v0 of this spec said "drop at `deadline + allowedLateness`". That would have discarded fired alerts 2 minutes after firing, so a rider arriving 25–40 minutes after food was ready could never resolve them.)*

### 5.5 Rule validation (enforced by `Core`)

- Durations are positive. `window.size` is a whole number of panes, and for sliding windows `size % slide == 0`.
- Referenced event types and fields exist in §4.2. `op` is one of `gt, gte, lt, lte, eq, ne`.
- `notify.channels` contains only configured channels. Escalation steps are ordered and strictly increasing.
- The `id` matches `^[a-z0-9-]{3,64}$`.
- `windowed_count` conditions must be `gt` or `gte` (monotonic), which is what makes early firing safe. `ratio_vs_baseline` must be per `zoneId` with a tumbling `PT1H` window, because baselines are stored per zone and hour. `absence` correlates by `orderId`.
- `notify.groupBy` must be an envelope field (`zoneId`, `restaurantId`, `orderId`).

---

## 6. Engine semantics

### 6.1 Processing loop (per assigned partition)

1. Poll a record → deserialize (invalid → DLQ).
2. **Watermark update:** `wm[p] = max(wm[p], occurredAt − MaxOutOfOrderness)` (ticks included).
3. **Lateness check:** if `occurredAt < wm[p] − AllowedLateness`, send the record to `late-events` and increment `noisecut_late_events_total{outcome="dropped"}`. Window and zone rules skip it, because their windows are closed. *M3 correction:* absence rules still apply it, since a late pickup can only disarm a timer or resolve an alert (§5.4). Dropping it entirely turned Experiment 2A's held-back pickups into false stuck-pickup alerts.
4. Apply to every enabled rule's state for this partition (order stage) and update the zone partial counts.
5. **Watermark-driven work:** close panes, fire absence timers, evaluate resolutions, and emit zone partials for closed panes.
6. Produce alert candidates.
7. Periodically **checkpoint** (§6.3).

Defaults: `MaxOutOfOrderness = PT30S`, `AllowedLateness = PT2M` (event time). Both are configurable.

### 6.2 Time modes (`Engine:TimeMode`)

| Mode | Window assignment uses | Watermarks | Profile |
|------|------------------------|------------|---------|
| `ProcessingTime` | consumer receive time × `TimeScale` | none: panes close by wall clock | naive |
| `EventTime` | `occurredAt` | per partition + ticks | robust |

How processing time is turned into sim time (M1): the engine anchors on the first record it reads,
`assigned = firstOccurredAt + (wallNow − wallAtFirstRecord) × TimeScale`, and ignores `occurredAt` from then on.
Every evaluator is written against (assigned time, watermark), so **this one function is the only difference between the two modes**.
In processing-time mode the watermark is simply the assigned time, because "now" never goes backwards.

### 6.3 State & checkpointing (`Engine:StateMode`)

| Mode | Behaviour | Profile |
|------|-----------|---------|
| `InMemory` | State lives only in process memory. `enable.auto.commit=true` commits offsets every 5 s regardless of state. A crash loses windows, and restart resumes from the last auto-committed offset. | naive |
| `Checkpointed` | State + next offset are saved **in one PostgreSQL transaction** per partition. Kafka auto-commit is off. | robust |

Checkpointed protocol (ADR-0005):
- **Write:** every `CheckpointInterval` (default 5 s wall) or 2,000 records per partition. First **flush the producer**, so every candidate caused by records before the offset is acknowledged by the broker. Then `UPSERT engine_checkpoints(stage, topic, partition) SET offset, watermark, state, epoch`. We also commit the offset to Kafka, but only for lag visibility. **PostgreSQL is the source of truth.**
- **Assign** (rebalance or start): load the checkpoint → restore state → `Seek` to the stored offset. With no checkpoint, start from earliest.
- **Revoke:** take a final checkpoint synchronously, then drop the in-memory state.
- **Invariant:** *never persist an offset whose consequences (candidates) are not durable.* Replay after a crash can therefore re-emit candidates, but it never loses them. Duplicates are absorbed downstream by deterministic alert keys (ADR-0006).
- State is serialized as versioned JSON (readable for learning). If size becomes a problem, switching to a binary format is an allowed later optimization.
- **Known limitation (documented, not solved in v1):** a "zombie" instance that has lost its partition but has not noticed yet could overwrite a newer checkpoint. Mitigation: the revoke handler is synchronous, and each checkpoint row carries the consumer-group generation as `epoch`, and a write is rejected if it carries a lower epoch.
- *As built (M3, ADR-0014):*
  - The epoch is a **fencing token issued by PostgreSQL**, not the group generation. Taking a partition bumps it atomically, and every write must present it (`WHERE epoch = @mine`). A fenced instance drops the partition.
  - The robust stages also use **static membership** (`group.instance.id`, 10 s session timeout), so a restarted engine reclaims its partitions immediately instead of after the dead member's session expires.
  - A failed produce stops the stage, because a later checkpoint must never cover a lost candidate. The restart resumes from the last checkpoint.

### 6.4 Zone stage (cross-partition aggregation)

- Each order-stage partition keeps **cumulative** `(placed, cancelled)` counts per `(zoneId, hourStart)` for its own events. At every pane close it publishes to `zone-partials` (key `zoneId`):
  `{ zoneId, hourStart, sourcePartition, placed, cancelled, sourceWatermark, seq }`.
  It also publishes a watermark-only partial for every zone, so the zone stage can always advance.
- The zone stage keeps, for each `(zoneId, hourStart, sourcePartition)`, the **latest partial by `seq`**. Totals are the sum over source partitions.
- Partials carry cumulative totals rather than deltas, so **replays and duplicates are harmless**: reapplying an older or equal `seq` is a no-op.
- The zone watermark is the min of the latest `sourceWatermark` across all 6 source partitions.
- The zone stage is checkpointed the same way as the order stage (stage = `zone`).
- *As built (M3):*
  - Partials are published every time a source partition closes a pane. Each publish covers the current hour for **every** zone (zeros included, so the zone stage learns the source's watermark), plus any older hour whose counts changed.
  - Partials for an hour that is already final are ignored.
  - Hours become final at hour end + allowed lateness, plus 45 min for cohort rules (ADR-0015).
  - The zone stage writes final totals to `zone_hourly_stats` with `source = 'live'` (idempotent upsert).

### 6.5 Emit modes (`Engine:EmitMode`)

| Mode | Behaviour | Profile |
|------|-----------|---------|
| `EveryMatch` | Emit a `firing` candidate for **every** event while the condition is true. | naive (Break #3: "500 alerts a minute") |
| `OnTransition` | Emit only `firing` (false→true) and `resolved` (true→false) per episode. | robust |

"Every event" per rule kind (M1 clarification):
- `windowed_count`: every matching event for the entity while its window is at or over the threshold.
- `ratio_vs_baseline`: every evaluation (each pane close) while the zone-hour is over the threshold.
- `absence`: no difference. Nothing arrives for a stuck order, so it fires once in both modes.

Both modes track episodes, so a repeated candidate carries the **same** alert key. That is what lets dedup collapse them later.

### 6.6 Alert candidate (topic `alert-candidates`)

```json
{
  "candidateId": "uuidv7",
  "alertKey": "restaurant-delay-burst:3:r-017:2026-10-17T19:33:00Z",
  "status": "firing | resolved",
  "ruleId": "restaurant-delay-burst", "ruleVersion": 3, "severity": "high",
  "entityType": "restaurantId", "entityKey": "r-017",
  "groupKey": "z-02",
  "episodeStart": "2026-10-17T19:33:00Z",
  "windowStart": "2026-10-17T19:33:00Z", "windowEnd": "2026-10-17T19:43:00Z",
  "observed": { "count": 5 }, "threshold": { "op": "gte", "value": 5 },
  "sampleEventIds": ["…", "…"],
  "triggerEventId": "…",
  "triggerProducedAt": "2026-10-17T14:12:59.812Z",
  "detectedAt": "2026-10-17T14:13:00.020Z",
  "engine": { "instance": "engine-7c9f-2", "stage": "order", "partition": 4, "offset": 918244 }
}
```

`alertKey` is computed only from rule identity + entity + `episodeStart` (event time). Replaying the
same records therefore produces **the same key**, and the notifier relies on that.

---

## 7. Notifier semantics

### 7.1 Intake (idempotent consumer)

For each candidate, in **one PostgreSQL transaction**:
1. `INSERT INTO alert_candidates (alert_key, status, …) ON CONFLICT (alert_key, status) DO NOTHING`. If a row already exists, the candidate is a duplicate (crash replay, or `EveryMatch` noise). Count `suppressed{reason="dedup"}`, and stop when dedup is enabled.
2. Upsert `alerts` (one row per `alert_key`) and apply the state machine below.
3. Decide notification (§7.2) → insert into `notification_groups` / `deliveries` (outbox).

Commit the Kafka offset **after** the DB commit (at-least-once intake + idempotent writes = effectively-once).

### 7.2 Noise policy (each piece toggleable; all on in the robust profile)

| Stage | Key | Clock | Rule |
|-------|-----|-------|------|
| **Dedup** | `(alertKey, status)` | — | The same transition for the same episode is processed once, ever. |
| **Cooldown** | `(ruleId, entityKey)` | wall | After a notification for an entity, more `firing` episodes within `cooldown` are recorded but not notified. They are attached to the alert as `suppressedCount`, so the next message says "+12 more since 19:40". `resolved` is never suppressed by cooldown. |
| **Grouping** | `(ruleId, groupKey)` | wall | The first alert opens a group with `flushAt = now + groupWait`. Alerts arriving before `flushAt` join it. At flush, **one** notification lists them all ("4 restaurants in Koramangala: delay bursts"). |
| **Escalation** | `alertKey` | wall | If an alert is still `firing` and not acknowledged after `escalation[i].after`, send to that step's channels. Each step fires at most once (unique `(alert_key, step)`). |

Alert state machine: `firing → acknowledged → resolved`, or `firing → resolved`. Acknowledging
(admin) stops further escalation. Resolution sends a "resolved" notification to the original channels.

*M3 policy details:*
- **Resolved only if notified.** With cooldown or grouping on, a "resolved" goes out only if that episode's "firing" did. Nobody needs to hear that something they were never told about is over.
- **Grouping applies to both kinds.** Firing and resolved notifications are grouped separately per (rule, groupKey, kind). A group notification lists its members; the first member opens the group, and later ones are counted as `suppressed{reason="grouped"}`.
- **Escalation** is driven by `alerts.first_detected_at` (wall clock), for alerts that are still firing, were notified and not acknowledged. Email goes through Mailpit locally.
- **Ack:** `POST /api/alerts/{key}/ack` (admin) sets `acknowledged`, which stops escalation. Resolution still notifies.

**Reported numbers** (§9) come straight from these stages: candidates in, deduped, cooldown-suppressed,
alerts merged by grouping, and notifications out.

### 7.3 Delivery (transactional outbox, ADR-0007)

- `deliveries(id, channel, payload, status, attempts, next_attempt_at, idempotency_key)` is written in the same transaction as the decision.
- A dispatcher claims rows with `SELECT … FOR UPDATE SKIP LOCKED` (safe with N notifier replicas) and sends them. Failures retry with exponential backoff + jitter (1 s, 2 s, 4 s … max 5 min). After 8 attempts the row becomes `dead` and `noisecut_deliveries_dead_total` increments.
- Delivery to an external system is **at-least-once**. Every request carries `Idempotency-Key: <delivery id>`, so receivers can dedup. Our own sink does, and it counts duplicate receipts. That count is how we *prove* "zero duplicate notifications".

### 7.4 Channels

| Channel | Transport | Notes |
|---------|-----------|-------|
| `dashboard` | DB row + PostgreSQL `NOTIFY` → Api SSE | Always on. |
| `webhook` | HTTPS POST JSON, `X-NoiseCut-Signature: sha256=<HMAC>` | Default target: the Api's own sink `/sinks/webhook`. `format: "slack"` produces a Slack-compatible body, so a real Slack incoming-webhook URL works by config alone. |
| `email` | SMTP (MailKit) | Default: Mailpit. Mailpit's UI is never exposed publicly. |

---

## 8. Api & dashboard

Server-rendered **Razor Pages** + a small SSE script. No SPA build toolchain (ADR-0010).

### 8.1 Public (anonymous, read-only, rate-limited)

| Route | Purpose |
|-------|---------|
| `GET /` | Live alert feed (SSE), current firing alerts, suppression counters. |
| `GET /alerts`, `GET /alerts/{key}` | History; per-alert timeline (candidates, suppressed count, deliveries, escalations). |
| `GET /rules`, `GET /rules/{id}` | Rule definitions (read-only) and their recent hits. |
| `GET /about` | What this project is, and links to the repo and Grafana. |
| `GET /api/alerts`, `/api/rules`, `/api/stats`, `/api/stream` | JSON/SSE equivalents. |
| `GET /healthz`, `/readyz` | Liveness and readiness. |
| `/grafana/*` | Grafana with anonymous **Viewer** role (routed by Cloudflare, not by the Api). |

### 8.2 Admin (cookie login, or `X-Admin-Key` header for scripts)

| Route | Purpose |
|-------|---------|
| `GET/POST /admin/login`, `POST /admin/login?handler=Logout` | Single admin. Password hash and key come from a Kubernetes Secret / `.env`. Login is rate-limited (10/min per client) and locks a client out after 5 failures in 15 min (M4). |
| `POST /api/rules`, `PUT /api/rules/{id}`, `POST /api/rules/{id}/(enable\|disable)` | Rule management. Each write bumps the version and notifies the engine. |
| `POST /api/alerts/{key}/ack` | Acknowledge (stops escalation). |
| `POST /api/scenarios/{name}`, `POST /api/simulator/config` | Inject scenarios / change rate or chaos (forwarded to the simulator's internal API). |
| `GET /admin/sinks/webhook` | Webhook-sink inbox with duplicate-receipt counter. |

`POST /sinks/webhook` is reachable on the public hostname but **rejects any request without a valid
HMAC signature**. Antiforgery tokens are on for all admin forms. Admin cookies are `Secure`, `HttpOnly`
and `SameSite=Strict`.

### 8.3 Rule hot reload

The Api writes the rule and runs `NOTIFY rules_changed`. Engines `LISTEN`, with a 30 s polling fallback.
A changed rule version starts with fresh state (§5).

> **Status (M4):** not built yet. Rules are seeded by the migrator and the engine polls them every 30 s. The rule-write
> endpoints and `NOTIFY` were deferred, because the public showcase never needs them; they are listed under M5 follow-ups.

---

## 9. Simulator

- **Discrete-event scheduler.** Each order's lifecycle is scheduled on a priority queue keyed by sim time. Events are released as the sim clock passes them.
- **Sim clock:** `simNow = simStart + (wallElapsed × TimeScale)`. Default `TimeScale = 10`.
- **Load:** `OrdersPerSimMinute` (default 300 at lunch/dinner peaks, following a daily curve). That is about 6 events/order, roughly **300 msgs/s** at 10×. One knob sets the rate for benchmarks.
- **Timing model** (sim time): accept 0–2 m · ready 10–25 m after accept · pickup 2–8 m after ready · deliver 15–35 m after pickup · baseline cancel ≈ 3% (3.5% at peaks, 2.5% otherwise). Cancellations happen before accept (40%), in the kitchen (40%) or while waiting for a rider (20%).
- **Delays** (M1 detail): when the kitchen will overrun, `order.delayed {stage: kitchen}` is emitted **2–5 minutes after accept**. That is when the ETA visibly slips, not when the food is finally ready, which is what makes early detection possible. Organic noise: 5% of orders overrun by 3–12 min and 0.2% by 15–20 min. During a rider shortage, `order.delayed {stage: pickup}` is emitted 8 minutes after food_ready.
- **Daily curve** (Bengaluru time): 00–07 ×0.2 · 07–11 ×0.4 · 11–15 ×1.0 · 15–19 ×0.5 · 19–23 ×1.0 · 23–24 ×0.4.
- **Lazy lifecycle:** each released event schedules only the order's *next* step, and samples its duration at that moment. A scenario that starts mid-way therefore affects orders already in flight.
- **Deterministic:** `Seed` makes runs reproducible, which is essential for before/after experiments. Event IDs are UUIDv7 built from the seeded RNG, so they are reproducible too.
- **Sim clock across restarts:** unless `Simulator:SimStart` is set, the simulator reads the last record of every partition and resumes one second after the newest `occurredAt` (or at wall-clock now if that is later). Event time never goes backwards, and the log is the source of truth.
- **Ticks:** every wall second, a `clock.tick` with `occurredAt = simNow` goes to **each** partition of `order-events`.
- **Control API** (internal port 8081, never public): `GET /status`, `GET /scenarios`, `POST /scenarios/{name}` with an optional `{ target, durationMinutes, intensity }`, and `POST /config { ordersPerSimMinute }`.

**Scenarios** (each has a target, a duration in sim time, and an intensity):

| Name | Effect | Expected rule |
|------|--------|---------------|
| `kitchen-meltdown` | One restaurant: kitchen stage +20–40 min for 15 min | A |
| `zone-cancel-spike` | One zone: cancellation probability ×3 for 1 h | B |
| `rider-shortage` | One zone: pickup 25–40 m after ready | C (grouped per restaurant) |
| `alert-storm` | Many restaurants in a zone melt down at once | A + grouping/cooldown demo |
| `slow-kitchen` (M2) | One restaurant: badly late orders (16–25 min), but never closer than 4 min apart | **none**: a *negative control*. In event time no 10-minute window can hold 5, so any rule-A alert it causes is false (Break #2) |

**Chaos knobs** (M2, `GET/POST /chaos` on the simulator, forwarded by the Api at `/api/simulator/chaos`):
`OutOfOrderProbability` + `MaxHoldBackSeconds` (wall), which delay *sending* without changing `occurredAt`;
`DuplicateProbability`, which resends the same `eventId`; and `POST /chaos/late-burst {seconds}`, which holds every order event
for that long while ticks keep flowing, then releases them together. Ticks are never delayed: time keeps moving, which is exactly
what makes held events *late*.

**Truth log** (M2): every scenario is written to `sim_scenarios`, and `/api/truth` grades alerts against it (see `docs/experiments/README.md`).
`Simulator:StartAtLocalHour` pins the sim clock to a fixed Bengaluru hour, so experiments see the same demand curve whenever they are run.

---

## 10. Observability

OpenTelemetry metrics → Prometheus exporter on an **internal-only** port (`:9464`, never routed publicly).
Logs are structured JSON on stdout.

| Metric | Type | Labels |
|--------|------|--------|
| `noisecut_events_consumed_total` | counter | `stage`, `type` |
| `noisecut_late_events_total` | counter | `outcome` = `applied` \| `dropped` |
| `noisecut_watermark_lag_seconds` | gauge | `stage`, `partition` (wall time − watermark, scaled) |
| `noisecut_candidates_total` | counter | `rule`, `status` |
| `noisecut_detection_latency_seconds` | histogram | `rule` |
| `noisecut_suppressed_total` | counter | `rule`, `reason` = `dedup` \| `cooldown` \| `grouped` |
| `noisecut_notifications_total` | counter | `channel`, `kind` = `firing` \| `resolved` \| `escalation` |
| `noisecut_delivery_latency_seconds` | histogram | `channel` |
| `noisecut_deliveries_dead_total` | counter | `channel` |
| `noisecut_sink_duplicate_receipts_total` | counter | — |
| `noisecut_checkpoint_duration_seconds` | histogram | `stage` |
| consumer lag | gauge | from Redpanda's own metrics |

**As implemented in M2** (`Infrastructure/Telemetry/NoiseCutMetrics.cs`). The OpenTelemetry Prometheus exporter adds `_total` to
counters and `_seconds` to instruments with unit `s`.

| Metric | Type | Labels | Service |
|--------|------|--------|---------|
| `noisecut_events_consumed_total` | counter | `stage`, `type` | engine |
| `noisecut_candidates_total` | counter | `rule`, `status` | engine |
| `noisecut_detection_latency_seconds` | histogram | `rule` | engine |
| `noisecut_consumer_lag_records` | gauge | `stage`, `partition` | engine (computed by the consumer: high watermark − position) |
| `noisecut_record_age_seconds` | gauge | `stage`, `partition` | engine (wall age of the last record processed) |
| `noisecut_engine_zone_hour_orders` | gauge | `zone`, `kind` | engine (what *this* instance counts for rule B) |
| `noisecut_dead_lettered_total` | counter | — | engine |
| `noisecut_intake_candidates_total` | counter | `rule`, `status`, `repeat` | notifier |
| `noisecut_suppressed_total` | counter | `rule`, `reason` | notifier |
| `noisecut_notifications_total` | counter | `channel`, `kind` | notifier |
| `noisecut_delivery_latency_seconds` | histogram | `channel` | notifier |
| `noisecut_delivery_failures_total`, `noisecut_deliveries_dead_total` | counter | `channel` | notifier |
| `noisecut_sink_receipts_total`, `noisecut_sink_duplicate_receipts_total` | counter | — | api |
| `noisecut_sim_events_total` | counter | `type`, `zone` | simulator (ground truth) |
| `noisecut_sim_zone_hour_orders` | gauge | `zone`, `kind` | simulator (ground truth for the current sim hour) |
| `noisecut_sim_active_scenarios`, `noisecut_sim_orders_per_minute`, `noisecut_sim_chaos_total` | gauge/counter | `name` / — / `kind` | simulator |

`noisecut_watermark_lag_seconds`, `noisecut_late_events_total` and `noisecut_checkpoint_duration_seconds` arrive in M3, with the
event-time and checkpoint code they describe. Consumer lag is measured by the engine itself rather than Redpanda: it is simpler,
and it works per replica.

Locally: Prometheus at http://localhost:9090 (scrapes every service on its internal port 9464 every 5 s), and Grafana at
http://localhost:3000 (anonymous Viewer; dashboard provisioned from `deploy/observability/grafana/dashboards/`).

**Latency definitions** (both are wall clock, so the sim speed-up doesn't distort them):
- **Detection latency** = `detectedAt − triggerProducedAt`. The trigger is the event (or tick, for absence timers) that made the condition true. This is the "X s p95" in the résumé line.
- **Delivery latency** = delivered-at − `triggerProducedAt`. It includes the *intentional* `groupWait`, so it is reported separately and labelled as such.

Labels never include `orderId` or `eventId` (cardinality control). Grafana dashboards are provisioned from JSON in `deploy/observability/`.

---

## 11. Data model (PostgreSQL)

Migrations are plain SQL files (`NoiseCut.Migrator`, DbUp-style journal table). Data access uses Dapper,
so every query is visible SQL (ADR-0011).

| Table | Key | Notes |
|-------|-----|-------|
| `zones`, `restaurants` | id | Seeded reference data. |
| `rules` | `id` | `version int`, `enabled`, `definition jsonb`, `updated_at`. |
| `rule_history` | `(id, version)` | Append-only audit of every version. |
| `zone_hourly_stats` | `(zone_id, hour_start)` | `placed`, `cancelled`, `source` = `seed` \| `live`. |
| `engine_checkpoints` | `(stage, topic, partition)` | `next_offset`, `watermark`, `epoch`, `state jsonb`, `updated_at`. |
| `alerts` | `alert_key` | Status, rule, entity, group, timestamps, `suppressed_count`, `acked_at`. |
| `alert_candidates` | `(alert_key, status)` unique | Intake log; also holds counts of deduped repeats. |
| `notification_groups` | id; unique open `(rule_id, group_key)` | `flush_at`, members. |
| `deliveries` | id | Outbox (§7.3). `notification_key = alertKey\|kind\|step\|channel` identifies the logical notification and is sent as `Idempotency-Key`. *Not* unique in schema v1: the naive profile must be able to send the same notification twice, so the sink can count the repeat. Robust mode stops repeats earlier, at intake. |
| `webhook_receipts` | `idempotency_key` | Sink log; `receive_count` > 1 means a duplicate. |

Retention: a nightly cleanup deletes candidates/deliveries older than 14 days and alerts older than 30 days (M3).

Schema history: `0001_initial_schema.sql` (M1) creates everything above **except** `engine_checkpoints` and `notification_groups`.
Those arrive in later migrations, together with the code that uses them, so the migration history reads like the milestone history.

---

## 12. Configuration & profiles

All settings are standard .NET configuration (appsettings → env vars `NoiseCut__Engine__TimeMode=…`).

| Setting | Naive | Robust |
|---------|-------|--------|
| `Engine:StateMode` | `InMemory` | `Checkpointed` |
| `Engine:TimeMode` | `ProcessingTime` | `EventTime` |
| `Engine:EmitMode` | `EveryMatch` | `OnTransition` |
| `Engine:ZoneStage` (M3) | `false` (one engine state per process) | `true` (per-partition order stage + zone stage) |
| `Notifier:Dedup` / `Cooldown` / `Grouping` / `Escalation` | `false` | `true` |

`NoiseCut:Profile=naive|robust` applies the preset. Individual keys override it, so one fix can be
switched on at a time to measure each one in isolation. **Production runs `robust`.** Naive exists for
demos and experiments (ADR-0008).

---

## 13. Testing strategy

| Layer | Tooling | What it proves |
|-------|---------|----------------|
| Unit (`Core.Tests`) | xUnit, `FakeTimeProvider` | Pane math, sliding sums, distinct counting, watermarks, lateness, each evaluator on scripted event sequences (incl. out-of-order), episode transitions, alert-key determinism, cooldown/grouping/escalation decisions. Target ≥ 90% line coverage of `Core`. |
| Integration | xUnit + Testcontainers (PostgreSQL, Redpanda) | Migrations; checkpoint save/restore; "kill mid-stream and resume" gives identical candidate keys; notifier intake idempotency; outbox `SKIP LOCKED` with 2 dispatchers. |
| Scenario | Compose stack + scripts | Each scenario produces exactly the expected alerts within a deadline. |
| Experiments | `scripts/break/*`, `scripts/prove/*` | Before/after numbers recorded in `docs/experiments/`. |
| CI | GitHub Actions | Unit + integration on every PR. Images build. Manifests render. |

Every bug found gets a failing test first, then the fix.

---

## 14. Break → Prove experiments (the measured story)

| # | Break (naive) | Expected symptom | Fix (robust) | Number reported |
|---|---------------|------------------|--------------|-----------------|
| 1 | Kill the engine mid-stream (`InMemory`, auto-commit) | Window counts vanish → missed incidents. Re-read events re-emit alerts → repeats | Checkpoint state+offsets (§6.3) + idempotent intake (§7.1) | Duplicate notifications over **N forced crashes** (target 0), missed incidents |
| 2 | Out-of-order + lag (`ProcessingTime`) | Backlog after a restart lands in one processing-time window → false delay bursts. Pickup-before-ready → false stuck alerts | Event time + watermarks + allowed lateness + order-insensitive absence state | False alerts per hour, late events applied/dropped |
| 3 | `alert-storm` with `EveryMatch` + no policy | One rule fires ~500 times/min | `OnTransition` + dedup + cooldown + grouping | Candidates in vs. notifications out; suppressed by reason |
| 4 | Scale to 3 engines **without** the zone stage | Each instance sees ⅓ of a zone → spike missed or miscounted | Repartition via `zone-partials` (§6.4) | Zone-rule detection with 1 vs. 3 consumers |
| 5 | Load test | — | — | Detection latency p50/p95/p99 at 1×, 2×, 4× load; consumer lag |

Each experiment gets a page in `docs/experiments/` with: hypothesis, setup (machine specs, commit SHA,
config), exact commands, raw numbers, a Grafana screenshot, and a conclusion.

---

## 15. Deployment (summary — details in ADR-0009 and the M4 runbook)

- **Local:** `docker compose up` (`deploy/compose`) runs the whole stack on one machine.
- **Home server:** a single-node **k3s** cluster, namespace `noisecut`, applied with kustomize (`deploy/k3s`, ADR-0016).
  - Redpanda (single node, `--smp 1 --memory 700M`, dev-container mode) and PostgreSQL run as StatefulSets with `local-path` PVCs.
  - The engine is a StatefulSet too (3 replicas, no volumes). Stable pod names keep Kafka static membership across restarts.
  - Resource budget ≈ **3 GiB**: about 2.1 GiB of memory is requested in total (limits add up to about 3.6 GiB). The node has ~7.6 GiB, shared with other apps.
  - Every container runs non-root, with a read-only root filesystem and dropped capabilities. A default-deny NetworkPolicy allows only same-namespace traffic, plus the Api and Grafana.
- **CI/CD (GitHub Actions only, no manual `kubectl` from a laptop):**
  - `ci.yml` (every PR/push): build, unit + integration tests, render both overlays and validate them with kubeconform, build images.
  - `deploy.yml` (after ci succeeds on `main`, or by hand with a SHA to roll back):
    1. push immutable images `ghcr.io/kar6677thik/noisecut-<service>:<git-sha>`;
    2. join the tailnet (`tailscale/github-action`, `tag:ci`);
    3. run `scripts/deploy/apply.sh` as a **namespace-scoped ServiceAccount**: secrets → infrastructure + Jobs → wait for migrations → workloads → `rollout status`;
    4. smoke-test the public URL.
  - The CI kubeconfig sets `tls-server-name`, because the k3s API certificate doesn't list the Tailscale IP. Private images are pulled with a read-only GHCR token (`noisecut-registry`).
- **Public access:** the existing Cloudflare Tunnel routes `noisecut.karthiksurkanti.in` → Api NodePort **30530**, and `noisecut.karthiksurkanti.in/grafana` → Grafana NodePort **30531** (Grafana serves from the `/grafana` sub-path). The anonymous showcase is read-only (§8). `/metrics` ports, Mailpit, Redpanda and PostgreSQL are never routed publicly.
- Runbooks: `docs/runbooks/` (bootstrap, deploy/rollback/inspect, rotate secrets, local k3s rehearsal).

---

## 16. Acceptance criteria (definition of done for the project)

1. `docker compose up` + `scripts/demo.sh` shows all three reference incidents firing on the dashboard.
2. All tests are green in CI. `Core` coverage ≥ 90%.
3. Experiments 1–5 are recorded with numbers. Robust mode shows **0 duplicate notifications across ≥ 20 forced engine crashes** under load.
4. Engine runs as 3 replicas on k3s and the zone rule is correct (Experiment 4).
5. Deployed from GitHub Actions only. The public URL serves the read-only showcase. Admin actions require login.
6. README has an architecture diagram, a recorded failure demo (GIF/asciinema), and links to the one-page report in `docs/report/REPORT.md`.
7. The journal and learning notes are complete for every milestone.

---

## 17. Defaults that may be revisited

These are decided (so work isn't blocked), but are cheap to change:

| Item | Default |
|------|---------|
| Hostname | `noisecut.karthiksurkanti.in` (Grafana under `/grafana`) |
| NodePorts | Api `30530`, Grafana `30531` (Slate uses 30518–30520) |
| `TimeScale` in the deployed showcase | 10× |
| Simulator rate in the showcase | Low (~60 orders/sim-min) so the home server stays quiet; benchmarks raise it temporarily |
| Partitions | 6 (allows 1, 2, 3 or 6 consumers to split evenly) |
