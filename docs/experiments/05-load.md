# Experiment 5 — Detection latency under load

## Question
How quickly does NoiseCut turn the record that completes an incident into an alert candidate, and does that hold at 2× and 4×
the default load?

## Two different "latencies" (read this first)
- **Detection latency** (this experiment, SPEC §10): `detectedAt − triggerProducedAt`, both wall clock. It runs from the moment the record
  that made a rule's condition true was produced to the moment the engine emitted the candidate. It measures the *system*: Kafka
  delivery plus evaluation. For timers (stuck pickup) the trigger is the tick that moved the watermark past the deadline. For rule B it
  includes the hop through `zone-partials`.
- **Time to detect** (Experiments 1–4): from injecting an incident to its first alert. It is about 20–30 s for a kitchen meltdown, and it is
  dominated by the *incident itself*: five orders must actually run late within 10 minutes. No engine can beat that.

## Setup
- Profile `robust`, 1 engine, sim clock at 12:00 IST (lunch peak), on a fresh stack.
- Three levels, back to back: 300, 600 and 1,200 orders/sim-min (1×, 2×, 4×). Each level settles for 45 s, then injects two kitchen meltdowns and a rider shortage in different zones, and is observed for 150 s.
- Percentiles come from the histogram's `increase()` over each level's own 150 s window, so levels don't mix.
- Command: `NOISECUT_PROFILE=robust dotnet run --project tools/NoiseCut.Experiments -- load`.

## Results

Run `results/load-robust-20261021-085145.json`: Windows 11, Docker Desktop with 14 vCPUs / 7.7 GB, everything on one machine.

| Level | Order events/s consumed | Candidates | p50 | **p95** | p99 | Max consumer lag | Max record age | Checkpoint p95 |
|------:|------------------------:|-----------:|----:|--------:|----:|-----------------:|---------------:|---------------:|
| 1× | 172 | 41 | 8 ms | **10 ms** | 41 ms | 36 records | 0.33 s | 47 ms |
| 2× | 389 | 1,200 | 8 ms | **10 ms** | 27 ms | 52 records | 0.16 s | 95 ms |
| 4× | 782 | 2,419 | 8 ms | **10 ms** | 166 ms | 98 records | 0.20 s | 267 ms |

All alerts were true positives (6 delay bursts, 2,473 stuck-pickup episodes; 0 false). The large candidate counts at 2× and 4× are rider
shortages, where every stuck order is its own episode. The notifier groups them into a few messages per restaurant.

## What it shows
- **p95 detection latency is about 10 ms and flat from 1× to 4×.** The engine is nowhere near saturated at 780 events/s. Consumer lag stays
  under 100 records, and the newest processed record is never more than a third of a second old.
- **The tail grows with load:** p99 is 166 ms at 4×. The likely cause is the checkpoint, which blocks the consume loop while it flushes the producer and writes
  PostgreSQL. Its p95 rises from 47 ms to 267 ms as more candidates are in flight. A natural next step would be checkpointing on a background
  thread from a state snapshot; it isn't needed at this scale.
- **Histogram resolution:** the bucket boundaries are 5, 10 and 25 ms, so "10 ms" means "≤ 10 ms". The quantiles are interpolated
  within buckets.

**Caveats:** one run per level, on a laptop where the simulator, Redpanda, PostgreSQL, Prometheus and the services share the same CPUs.
These numbers describe this machine, not a production cluster.
