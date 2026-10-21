# Experiment 4 — Three engines, no zone stage

## Hypothesis
Order events are keyed by restaurant (ADR-0003), so one zone's orders are spread over several partitions. With 3 engines in the
consumer group, each engine owns 2 of the 6 partitions and holds rule B's counts for **only its share of each zone**.
- Each engine reaches the 300-order guard later, or never, so the cancellation spike is detected late or missed.
- Each engine's count is a fraction of the truth, so any alert it does raise reports wrong numbers.
- Rules A and C are per restaurant and per order. They live entirely inside one partition, so they are unaffected.

## Setup
- Profile `naive` with `EmitMode=OnTransition`.
- The sim clock starts at **16:30 IST = 11:00 UTC**, exactly on an hour, so one hour cell covers the whole spike. Afternoon demand is half of peak.
- `zone-cancel-spike` (cancellation probability ×3), injected 15 s after the stack is ready.
- Each configuration runs on a fresh stack: first 1 engine, then 3 engines (`--scale engine=3`).
- After 200 s (about 35 minutes into the hour) the run snapshots:
  - each engine's count for the zone (`noisecut_engine_zone_hour_orders`), reported as a **share** of the engines' total; and
  - the simulator's ground truth (`noisecut_sim_zone_hour_orders`).
- It then waits until 360 s (the end of the sim hour) for the first matching alert.
- Commands: `dotnet run --project tools/NoiseCut.Experiments -- scale` (z-02 at 300 and 80 orders/sim-min), and
  `... -- scale --zone z-03 --rate 80`.

## Why the zone matters: partition skew
The real mapping, read from the topic with `rpk topic consume order-events -f '%k %p'`, shows how each zone's 10 restaurants land on
the 6 partitions. With 3 engines, the range assignor gives each engine a pair of partitions (0–1, 2–3, 4–5):

| Zone | Restaurants per engine (p0–1 / p2–3 / p4–5) | Largest share |
|------|---------------------------------------------|--------------:|
| z-01 | 4 / 2 / 4 | 40% |
| **z-02** | **7 / 0 / 3** | **70%** |
| **z-03** | **3 / 3 / 4** | **40%** |
| z-04 | 3 / 1 / 6 | 60% |
| z-05 | 4 / 4 / 2 | 40% |
| z-06 | 3 / 5 / 2 | 50% |

Hashing ten keys into six buckets is lumpy. Whether a split zone is "mostly in one engine" or "spread thin" is luck.

## Results (Break, M2)

Runs `results/scale-20261019-134410.json` (z-02) and `results/scale-20261019-171104.json` (z-03), on the same machine and code as the
other experiments.

| Zone | Orders/sim-min | Engines | Detected | Time to detect | Engines' shares of the zone at +200 s | What the alert reported |
|------|---------------:|--------:|:--------:|---------------:|---------------------------------------|------------------------|
| z-02 | 300 | 1 | yes | 176 s | 100% (895 of 897 true) | 45/818 orders |
| z-02 | 300 | 3 | yes | 165 s | 69% / 30% / 1%¹ | **31/525 orders** (one engine's slice) |
| z-02 | 80 | 1 | yes | 326 s | 100% | 26/475 orders |
| z-02 | 80 | 3 | yes | 297 s | 70% / 30% | **17/305 orders** |
| z-03 | 80 | 1 | yes | **230 s** | 100% (268 of 262 true²) | 22/308 orders |
| z-03 | 80 | 3 | **no** (whole hour) | — | **35% / 34% / 31%** | *(nothing)* |

¹ The 1% is a stale gauge from a partition the engine owned briefly during the start-up rebalance. Gauges keep their last value.
² Processing-time and event-time hours are not exactly aligned, so engine and truth counts differ by a few orders even with one engine.

## What it shows

1. **A per-partition aggregate is a partial aggregate.** Every 3-engine alert reported the counts of whichever engine fired:
   "31 of 525 orders" was the slice of the engine holding about 69% of the zone, so roughly 760 orders had really been placed by
   then. An on-call engineer reading that alert would be misinformed.
2. **Detection depends on luck.** When one engine happened to hold 70% of the zone (z-02), it still passed the 300-order guard and
   the spike was caught. The timing differences (165 vs 176 s, 297 vs 326 s) are within run-to-run noise. When the zone was spread
   evenly (z-03, about a third each), no engine ever had 300 orders in the hour, and **a real, sustained cancellation spike went
   completely undetected**. One engine caught the same spike in under 4 minutes.
3. **Rules A and C were not affected.** They are per restaurant and per order, and all of one restaurant's events live in one partition (ADR-0003).
   The problem is specific to aggregates whose key (zone) differs from the partition key (restaurant).
4. **The fix is not "add more partitions" or "co-locate by zone".** Re-keying the source by zone would break per-order ordering for the
   other rules. The fix is a second stage: each engine publishes its partial counts to `zone-partials`, keyed by zone, and the zone
   stage sums them (M3, ADR-0004). That is the "shuffle" of MapReduce and the `repartition()` of Kafka Streams.

## After (M3)

Zone z-03 at 80 orders/sim-min, the case that missed completely. Between M2 and M3, rule B changed to cohort attribution (ADR-0015),
so the naive run was **repeated with the new rule** as a control. Before and after differ only in the profile.

| Profile | Engines | Detected | Time to detect | Engines' own shares of z-03 | What the alert reported |
|---------|--------:|:--------:|---------------:|-----------------------------|------------------------|
| naive (control, new rule) | 1 | yes | 230 s | 100% | 22/307 orders |
| naive (control, new rule) | 3 | **no** | — | 38% / 34% / 28% | *(nothing)* |
| **robust** | 1 | yes | 214 s | 100% | 17/300 orders |
| **robust** | **3** | **yes** | **222 s** | 37% / 33% / 30% | **19/300 orders**: the merged zone total |

Runs: `results/scale-naive-20261021-090217.json` and `results/scale-robust-20261021-084321.json`.

### What changed
- Each order-stage engine still sees only about a third of z-03. Its own counts (`noisecut_engine_zone_hour_orders`, "placed") remain partial by design.
- Every pane close, each partition publishes its cumulative per-zone counts to `zone-partials`, keyed by zone. Every partial for z-03
  therefore reaches one zone-stage owner, which keeps the latest partial per source partition and sums them.
- The zone stage's watermark is the minimum of the six source watermarks, so it never judges an hour before all partitions have reported.
- **With 3 engines, the merged count reached the 300-order guard as fast as with 1 engine.** Detection was 222 s against 214 s,
  within run-to-run noise, and the alert reported the true zone total.
- Restaurant and order rules were unaffected throughout. They never needed the shuffle, because their key matches the partition key.
