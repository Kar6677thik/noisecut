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
*To be recorded.*
