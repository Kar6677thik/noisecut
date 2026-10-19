# Experiment 2 — Out-of-order delivery and consumer lag

No real incidents are injected in either part, so **every alert is a false alarm**.

## Part A — out-of-order delivery

**Hypothesis.** When a `food_ready` is delayed in transit and arrives *after* its `picked_up`, the naive absence rule (rule C)
forgets the early pickup. The late `food_ready` then arms a timer nobody will ever disarm, and 20 sim minutes later a false
"stuck pickup" fires. The unit test `Naive_mode_forgets_a_pickup_that_arrives_before_food_ready` pins this behaviour down.

**Setup.**
- Fresh stack, naive profile with `EmitMode=OnTransition`, and the sim clock at 12:00 IST.
- Chaos on for 6 wall minutes (1 sim hour): 1% of order events held back for a uniform 0–60 wall seconds (0–10 sim min).
- Chaos then goes off, and the run waits 150 s so that timers armed during chaos come due.

## Part B — consumer lag with near-miss restaurants

**Hypothesis.** `slow-kitchen` restaurants have badly late orders, but never 5 within 10 minutes, so in event time they must never
trip rule A. If the engine stalls, the backlog is read back in seconds. In processing time every backlog record gets almost the
same "now", the spaced-out delays land in one 1-minute pane, and rule A fires falsely. The unit test
`Slow_kitchen_fires_falsely_when_a_backlog_is_squeezed_into_processing_time` shows the mechanism without Kafka.

**Setup.**
- Fresh stack, same settings as Part A, and `slow-kitchen` at 6 restaurants (one per zone) for 120 sim minutes.
- 180 s control period with the engine running normally.
- `docker compose pause engine` for 120 s (20 sim minutes of backlog), then `unpause`, and 90 s to catch up.

Command: `dotnet run --project tools/NoiseCut.Experiments -- out-of-order`.

## Results (Break, M2)

Run `results/out-of-order-20261019-132720.json`: same code and machine as Experiment 1, 19 minutes. An earlier run with
identical settings gave 67 and 6 + 1,438 false alerts. It was discarded only because it didn't record *where* the false
alerts landed. Numbers in that range are reproducible.

### Part A — out-of-order delivery (1.42 sim hours, 26,877 orders, 652 events held back)

| Rule | False alerts | Per sim hour |
|------|-------------:|-------------:|
| stuck-pickup | **61** | **43.1** |
| restaurant-delay-burst | 0 | 0 |
| zone-cancellation-spike | 0 | 0 |

The false stuck-pickups were spread over 41 restaurants (1–4 each), which is what random hold-backs should produce. Rules A and B were
unaffected: one late `delayed` or `placed` event barely moves a count.

### Part B — consumer lag (engine paused 120 s, 27,207 records of backlog on resume)

| Period | Rule A (delay burst) | Rule C (stuck pickup) |
|--------|--------------------:|---------------------:|
| 180 s control, engine keeping up | 0 | 0 |
| After the pause | **6**: exactly the six slow-kitchen restaurants, one each | **1,428**, spread across every restaurant (up to 37 each) |

## What it shows

1. **Out-of-order input makes an order-sensitive rule lie** (Part A, as predicted). 652 held-back events (1% of events during the
   6-minute chaos window) produced 61 false alerts. The cause is that the naive absence rule forgets a pickup that arrives before `food_ready`. The fix is
   order-insensitive state per order (M3, SPEC §5.4).
2. **A processing-time window counts arrivals, not events** (Part B, rule A, as predicted). In event time the slow kitchens never
   have 5 late orders within 10 minutes, and the control period produced 0 alerts. After the pause, 20 minutes of their delays
   were read within seconds and landed in one processing-time pane. All six fired. The fix is to assign windows by `occurredAt`.
3. **A processing-time timer fires on the wall clock, not when the data says so** (Part B, rule C). This was *not* predicted, and
   it was 240× bigger than the rule-A effect. When the engine woke up, its processing clock had jumped 20 sim minutes. The very
   first record it read pushed the "watermark" past the deadline of every order waiting for a rider, and every one of those timers
   fired **before** the engine had read the pickups sitting in the backlog. 1,428 false "stuck pickup" alerts came from one
   2-minute stall. The fix is to drive timers by an **event-time watermark** (M3, ADR-0012). Time only moves as fast as the
   data proves it has.

The general lesson: in processing time, a pause in *processing* looks like a pause in the *world*. Any consumer stall (a GC pause,
a deploy, a rebalance, a slow database) turns into fake incidents.

## After (M3)
*To be recorded.*
