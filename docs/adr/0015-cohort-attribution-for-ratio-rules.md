# ADR-0015: Ratio rules count cancellations against the hour the order was placed

- **Status:** Accepted (changes the semantics of SPEC §5.3)
- **Date:** 2026-10-21
- **Milestone:** M3

## Context
Rule B was defined as `cancelled ÷ placed for the zone in the current event-time hour`. The first robust demo raised a false
cancellation spike in a zone with no incident: 6.1% against a 3.0% baseline over 1,083 orders, about 4.6 standard deviations, so not chance.

The cause is that numerator and denominator describe different populations. A cancellation happens 1–35 minutes after its order
was placed.
- At a **constant** order rate, the two flows balance and the ratio is unbiased.
- When **demand drops**, they don't. New placements fall immediately, while the busier period's cancellations keep arriving. The ratio can inflate up to 2× with no change in customer behaviour.

The demo raised the rate and then lowered it. The simulator's own daily curve halves demand at 15:00 and 23:00 every day, so the
public showcase would have raised false spikes on schedule.

## Decision
- Ratio rules may declare `"cohortBy": "orderId"`. The reference rule B does.
- Each **numerator** event is then attributed to the hour of the **denominator** event with the same `orderId`. The rule asks:
  *"Of the orders placed this hour, what fraction were cancelled?"* (`CohortTracker`).
- A cohort keeps receiving cancellations after its hour ends, so cohort hours become final 45 minutes later. That delays the
  resolution notification and the `zone_hourly_stats` live write, but not detection, which still fires early.
- If the placement was never seen (e.g. it predates the engine's state), the cancellation falls back to its own hour.

## Alternatives considered
- **A sliding 60-minute window**: smaller edge effects, but it no longer matches the hourly baselines ("same hour last week").
- **Only evaluate after N minutes into the hour**: hides the start-of-hour effect but not the demand-drop effect.

## Consequences
- Good: the rule measures what its name says. The seeded baselines are cohort-defined too, so live and history now agree.
- Good: unit test `A_drop_in_demand_does_not_look_like_a_cancellation_spike` shows the old definition firing falsely and the new one not.
- Cost: per-partition memory for in-flight orders (about two hours of placements), included in checkpoints.
- Cost: the naive (M2) runs used the old definition. The Experiment 4 "before" was therefore re-run with the new rule as a control.
