# Experiments

Each experiment is a reproducible run with recorded numbers. M2 recorded the **Break** ("before") runs, and M3 added an
**After** section to each page with the fixes switched on (`NOISECUT_PROFILE=robust`). Result files are named `<experiment>-<profile>-<timestamp>.json`;
the M2 files predate the profile suffix.

| # | Page | Question |
|---|------|----------|
| 1 | [01-restart.md](01-restart.md) | What does a crash cost when state is in memory and offsets are auto-committed? |
| 2 | [02-out-of-order.md](02-out-of-order.md) | What do out-of-order delivery and consumer lag do to processing-time rules? |
| 3 | [03-storm.md](03-storm.md) | How many notifications does one big incident produce with no noise controls? |
| 4 | [04-scale.md](04-scale.md) | Does a zone rule still work when 3 engines each see part of the zone? |
| 5 | [05-load.md](05-load.md) | Detection latency at 1×, 2× and 4× load |

## How to run

```bash
dotnet run --project tools/NoiseCut.Experiments -- restart        # or out-of-order | storm | scale | all
./scripts/break/01-restart.sh --rounds 10                         # same thing, via the wrapper
```

Every run:
1. builds the images, then starts the Compose stack **from empty volumes** (`down -v`, then `up`);
2. applies per-experiment settings through a generated Compose override (the committed compose file never changes);
3. pins the sim clock to a fixed local hour (`Simulator:StartAtLocalHour`), so the demand curve doesn't depend on when you run it;
4. drives the stack only through public interfaces (`docker compose`, the Api's admin endpoints, Prometheus);
5. writes `results/<experiment>-<timestamp>.json` with the commit, machine, configuration, a timestamped step log and the numbers.

## How alerts are graded (the truth log)

The simulator records every injected scenario in `sim_scenarios` (migration `0002_truth_log.sql`). The view
`alert_attribution` links an alert to a scenario when **both** hold:

- the alert is about the scenario's target: the zone, one of its restaurants, or an order grouped under one of its restaurants; and
- its episode starts within `[starts_at − 15 min, ends_at + 60 min]` of sim time. Effects lag causes: delays are noticed minutes after
  accept, and stuck pickups fire 20 minutes after the food is ready.

An alert is a **true positive** if it is attributed to a scenario whose `expected_rule` is that alert's rule. Everything else is a
**false positive**: alerts with no scenario nearby, alerts from the wrong rule, and anything caused by a *negative control*
(`slow-kitchen`, whose expected rule is NULL). `/api/truth` returns the grading. The attribution window is a heuristic, written down
so that every experiment uses the same one.

## Reading the numbers

- These are laptop numbers from Docker Desktop, not benchmarks. What matters is the **before/after difference** on the same machine and commit.
- Wall-clock seconds are real time. Sim times run 10× faster: 6 wall seconds = 1 sim minute.
- One run is one sample. Where a number varies run to run, the page says so instead of reporting false precision.
