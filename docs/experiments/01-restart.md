# Experiment 1 — Kill the engine mid-incident

## Hypothesis
With window state only in memory and offsets auto-committed every 5 s on a timer (`StateMode=InMemory`), a crash:
1. **loses the partial window**, so an incident in progress is detected late or not at all; and
2. **replays** the records read after the last auto-commit, and rebuilds the window from them under *new* processing times,
   so the same incident can open a *second* alert with a different key.

## Setup
- Profile `naive` (in-memory state, processing time, auto-commit), but with `Engine:EmitMode=OnTransition`. EveryMatch noise is
  Experiment 3's subject; turning it off here means any extra alert comes from the crash itself.
- Sim clock pinned to 12:00 IST (lunch peak: about 5 orders per minute per restaurant). Default rate 300 orders/sim-min.
- Round 0 is a control: a kitchen meltdown with no kill. Rounds 1–5 inject a kitchen meltdown at a different restaurant each time,
  `SIGKILL` the engine 15, 30, 45, 60 or 90 s later, keep it down for 5 s, start it again, and give the round 150 s in total.
- Command: `dotnet run --project tools/NoiseCut.Experiments -- restart --rounds 5` (or `./scripts/break/01-restart.sh`).

## Results (Break, M2)

Run `results/restart-20261019-123846.json`.
- Code: base commit `04db4c5` plus the uncommitted M2 changes, which are the code in the M2 PR.
- Machine: Windows 11, Docker Desktop 28.4 with 14 vCPUs and 7.7 GB.
- Duration: 16.5 minutes.

| Round | Restaurant | Kill after | Detected | Time to first alert | Alerts for this one incident |
|------:|-----------|-----------:|:--------:|--------------------:|-----------------------------:|
| 0 (control) | r-003 | — | yes | 24.0 s | 1 |
| 1 | r-013 | 15 s | yes | **58.4 s** | 1 |
| 2 | r-023 | 30 s | yes | 21.9 s | **2** |
| 3 | r-033 | 45 s | yes | 29.4 s | **2** |
| 4 | r-043 | 60 s | yes | 30.5 s | **2** |
| 5 | r-053 | 90 s | yes | 27.0 s | **2** |

Totals: 5 kills · **0 missed incidents** · **4 extra alerts** for incidents that were already alerted · 15 webhook receipts ·
**0 duplicate webhooks** · 0 false alerts from other causes.

## What it shows

1. **A kill before detection delays it.** In round 1 the engine died 15 s into the meltdown with a partly filled window. The new
   process started from an empty window, so it needed five *more* delayed orders. Time to first alert went from 24 s (control) to 58 s.
   Nothing was missed, but only because a lunch-peak meltdown keeps producing delays for 90 s (15 sim minutes). A shorter or
   quieter incident would have been missed.
2. **A kill after detection alerts again.** In 4 of the 4 rounds where the kill came after the first alert, the restarted engine
   rebuilt the window from the records read after the last auto-commit, plus new ones. It crossed the threshold again and opened a
   **new episode**. The episode start comes from processing-time panes, so it was different, and so was the alert key.
   That is the "alerts repeat after restart" symptom from the project brief.
3. **Idempotency keys did not catch it, and that is the important part.** The sink counted 0 duplicates, because the second alert
   had a *different* key. Deduplication only works when the same event always produces the same identity. Losing state changed
   the identity, so "add dedup at the notifier" alone would not fix this. The fix has to make the engine's output deterministic
   across a crash: checkpoint the state and the offsets together (M3, ADR-0005), and use event time so episode starts come from
   the data, not the clock (ADR-0012).

**Caveat:** this is one run of 5 kills. M3 will repeat it with 20 or more kills under load (acceptance criterion 3).

## After (M3)
*To be recorded.*
