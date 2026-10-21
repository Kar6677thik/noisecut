# M2 — measuring failures

M2 changes no behaviour. It adds the instruments needed to *see* the system (metrics, a truth log, chaos knobs), then breaks
it on purpose and writes the numbers down. The numbers themselves are in [`docs/experiments/`](../experiments/README.md).

---

## 1. Metrics, the Prometheus way

**Pull, not push.** Each service serves its current metric values as text on an internal port (`:9464/metrics`).
Prometheus fetches ("scrapes") every target every 5 s and stores the time series. Run
`docker compose exec prometheus wget -qO- http://engine:9464/metrics` to see exactly what Prometheus sees.

**Three instrument types** (`Infrastructure/Telemetry/NoiseCutMetrics.cs`):

| Type | Example | Read it with |
|------|---------|--------------|
| Counter: only goes up | `noisecut_candidates_total` | `rate(x[1m])` = per second; `increase(x[5m])` = how many in 5 minutes |
| Gauge: any value | `noisecut_consumer_lag_records` | the value itself, `max by (...)` |
| Histogram: counts per bucket | `noisecut_detection_latency_seconds_bucket` | `histogram_quantile(0.95, sum by (le) (rate(x_bucket[5m])))` |

Why `rate()` and not the raw counter? A counter restarts at 0 when its process restarts (Break #1 restarts the engine on purpose).
`rate()` understands resets; subtracting raw values does not.

**Why percentiles, not averages.** If 99 alerts take 1 s and one takes 60 s, the average (1.6 s) describes nobody.
p95/p99 describe the slow tail that people actually notice. Histograms can't store every value, so bucket boundaries matter: the
OpenTelemetry defaults assume milliseconds, so `NoiseCutTelemetry` sets seconds-scale buckets.

**Cardinality.** Every distinct label combination is its own time series. Labels here are rules, zones, partitions and channels
(tens of values). An `orderId` label would create millions of series and take Prometheus down. This is a classic production outage.

**Scraping replicas.** `prometheus.yml` uses DNS service discovery: the name `engine` resolves to *every* engine container, so
`--scale engine=3` produces three targets, each with its own `instance` label. Break #4 relies on this to show each engine's partial view.

---

## 2. Grading alerts: the truth log

"Did it work?" needs an answer key. The simulator writes every injected scenario to `sim_scenarios`, and SQL views decide which
alerts were deserved (`0002_truth_log.sql`):

- **Recall** (did we catch it?): `scenario_outcomes.matching_alerts > 0`.
- **Precision** (was it real?): `alert_classification.true_positive`.
- **Time to detect**: the first matching alert's `first_detected_at` minus the scenario's `injected_at`, both wall clock.

**Negative controls.** `slow-kitchen` looks bad (orders 16–25 min late) but is deliberately *below* rule A's threshold. A system
that alerts on it is wrong, however impressive its recall. Testing only "does it fire when it should" misses half of alert quality;
the M1 lesson "Ordinary traffic raises no alerts" is the same idea.

The attribution window (`starts_at − 15 min … ends_at + 60 min`) is a judgement call. What matters is that it is written down and
applied the same way to every run, before and after the fixes.

---

## 3. Chaos, deliberately

| Knob | Real-world cause | Mechanism |
|------|------------------|-----------|
| Hold-back (`outOfOrderProbability`) | Retries, a slow producer, network partitions | The event is sent up to N s late; `occurredAt` is unchanged |
| Duplicates | A producer retry whose first attempt actually succeeded | Same `eventId` sent twice |
| Late burst | A producer stalled, then flushed | Every order event is held, ticks keep flowing, then everything is released |
| `docker compose pause` | GC pause, CPU starvation, a stuck host | The process is frozen (cgroup freezer). It holds its Kafka group membership until the session times out |
| `docker compose kill` | OOM kill, node loss | SIGKILL: no shutdown hooks, no final offset commit |

Kill and pause are different failures. A kill loses in-memory state immediately. A pause loses nothing, but creates a backlog that
is then read at full speed. That burst is exactly where processing-time rules go wrong.

---

## 4. Lessons from the runs

1. **Predictions are cheap; measurements are humbling.** Break #2 was designed to show false delay bursts, and it did (6). Its biggest
   effect was one nobody predicted: 1,428 false stuck-pickups, because processing-time timers fire on the wall clock while the
   data they wait for is still in the backlog. Without a truth log, that would have looked like "a busy afternoon".
2. **Dedup needs determinism.** After a crash the engine re-alerted with a *new* key (Exp 1). The sink's duplicate counter said 0, while
   the human got two alerts. Idempotency keys only help if the same input always produces the same key. That is why checkpointing
   comes before dedup in M3.
3. **Noise is the failure mode of alerting.** One incident became 1,164 notifications at about 800 a minute (Exp 3), and detection itself was
   correct and fast. Alert quality is mostly about what you *don't* send.
4. **Partition skew is real.** Ten keys over six partitions split 7/0/3 for one zone and 3/3/4 for another. The same bug was invisible in
   one zone and caused a complete miss in the other (Exp 4). Test distributed aggregates on more than one key.
5. **Design the experiment, then debug the experiment.** Two runs were thrown away: one hit a seeding gap that made baselines fall to
   their floor, and one snapshotted counts right after an hour boundary. Both are written down in the journal, because a result you can't
   explain is a bug in the system *or* in the measurement.

## 5. Try it yourself

- Open Grafana (http://localhost:3000), run `./scripts/break/03-storm.sh`, and watch "Candidates / min by rule" and "Notifications / min".
- During `02-out-of-order`, watch "Consumer lag by partition" climb while the engine is paused, then collapse once it resumes.
- In Prometheus (http://localhost:9090) try:
  - `histogram_quantile(0.95, sum by (le) (rate(noisecut_detection_latency_seconds_bucket[5m])))`
  - `sum by (instance) (noisecut_engine_zone_hour_orders{kind="placed"})` with `--scale engine=3`.
