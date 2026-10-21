# Experiment 3 — Alert storm

## Hypothesis
With no noise controls (fully naive profile: `EmitMode=EveryMatch`, no dedup, no cooldown, no grouping), one real incident that
hits many restaurants becomes hundreds of notifications. The person on call learns to ignore the channel, and that is how the
next real incident gets missed.

## Setup
- Fresh stack, profile `naive` (unmodified), sim clock at 12:00 IST, rate 300 orders/sim-min.
- `alert-storm` in zone z-01: 8 of its 10 restaurants melt down together for 15 sim minutes.
- 210 s of observation, then 20 s for the outbox to drain.
- Command: `dotnet run --project tools/NoiseCut.Experiments -- storm`.

## Results (Break, M2)

Run `results/storm-20261019-131145.json`, on the same code and machine as Experiment 1. It took 4.5 minutes.

| | Count |
|---|---:|
| Real incidents | **1** (8 restaurants in one zone) |
| Alerts (distinct episodes) | 8 (one per restaurant) |
| Candidates received by the notifier | **582** |
| Notifications delivered (dashboard + webhook) | **1,164** (582 + 582) |
| Duplicate webhooks (same Idempotency-Key received again) | **566** |
| Peak candidates per minute | 403 |
| Peak notifications per minute | **801** |
| Time to the first alert | 22.3 s |

## What it shows

- **Detection was fast and correct**: the first alert came after 22 s, and all 8 alerts were true positives. The problem is not finding the incident.
- **The noise is the problem.** One incident produced 1,164 notifications, about 800 a minute at peak. 566 of the webhook
  deliveries repeated a notification the receiver had already seen. A person would mute the channel within a minute, and the
  next real incident, in another zone, would land in a muted channel.
- **Where the noise comes from**:
  - Each restaurant's alert was re-emitted on every matching event (`EveryMatch`), about 70 candidates per alert.
  - The notifier passed each one through (no dedup).
  - Nothing merged 8 alerts from one zone into one message (no grouping).
- **What the M3 fixes should do**: `OnTransition` should cut candidates to about one firing and one resolved per episode. Dedup should absorb
  replays. Grouping on `zoneId` should collapse the 8 restaurants into roughly one message. The target is to turn the 1,164 into a
  handful, with the suppressed counts reported.

*Run-to-run variation: an earlier run of the same experiment, discarded because of the baseline bug described in the journal, gave
545 rule-A candidates and 542 duplicate webhooks. The order of magnitude is stable; the exact count depends on the random lifecycle.*

## After (M3)

Run `results/storm-robust-20261021-083847.json`: profile `robust`, same scenario and machine.

| | Before (naive) | **After (robust)** |
|---|---:|---:|
| Real incidents | 1 (8 restaurants) | 1 (8 restaurants) |
| Alerts (episodes) | 8 | 8 |
| Candidates received | 582 | **16** (8 firing + 8 resolved) |
| Notifications delivered | 1,164 | **4** |
| Duplicate webhooks | 566 | **0** |
| Time to the first alert | 22.3 s | 22.2 s |

The 4 notifications are, on each of the two channels (dashboard and webhook):
- **one firing message**, "[HIGH] Restaurant delay burst · 8 alerts in Koramangala", listing every restaurant; and
- **one resolved message** when the storm was over.

Detection did not slow down, and none of the 8 alerts was lost; they are listed inside the group message and on the dashboard.

Where each stage cut (from the notifier's `noisecut_suppressed_total`):
1. **`OnTransition`** in the engine took candidates from 582 to 16. A firing alert is announced once, not on every matching event.
2. **Grouping** (`groupBy: zoneId`, `groupWait: 30s`) turned 8 alerts into 1 message per channel. The resolutions were grouped the same way.
3. **Dedup and cooldown** had nothing left to do in this run. They are what absorbs crash replays (Experiment 1) and flapping.

The peak-per-minute PromQL figure is omitted for the robust run: with 4 increments in total, `rate()` over 1-minute windows mostly
misses the first increment of a newly created series, and reports 0. The totals above come from the database.
