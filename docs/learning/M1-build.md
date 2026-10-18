# M1 — what we built, and what to learn from it

M1 is the **smallest thing that works end to end**, built naive on purpose. Read this with the code open.
Every claim below points at a file, so you can check it yourself.

---

## 1. Follow one event from birth to browser

A kitchen meltdown at `r-017`, step by step:

| # | Where | What happens |
|---|-------|--------------|
| 1 | `scripts/demo.ps1` → `POST /api/scenarios/kitchen-meltdown` | The Api checks admin access (`AdminGuard.cs`) and forwards to the simulator's internal API (`SimulatorClient.cs`). |
| 2 | `Simulator/Program.cs` → `ScenarioBook.Start` | The scenario is recorded with a sim-time window: `[now, now + 15 min)`. |
| 3 | `OrderSimulation.OnAccepted` | When an order at r-017 is accepted during the window, its kitchen time gets +20–40 min, and an `order.delayed {stage: kitchen}` event is scheduled 2–5 min later. |
| 4 | `OrderSimulation.AdvanceTo` | The sim clock reaches that time and the event is released. |
| 5 | `SimulatorWorker` | It is produced to `order-events` with **key `r-017`**. The key's hash picks the partition, so all of r-017's events land on one partition, in order. |
| 6 | `EngineWorker.Handle` | The engine consumes it, parses it (`EventCodec`), and stamps it with **processing time** (`ProcessingTimeClock`). |
| 7 | `RuleEngine.Process` → `WindowedCountEvaluator.OnEvent` | The event is added to r-017's current 1-minute pane. The 10-pane window now counts 5 distinct orders, so the rule **fires early** and a candidate with key `restaurant-delay-burst:1:r-017:<episodeStart>` is produced. |
| 8 | `EngineWorker` | The candidate goes to `alert-candidates`, keyed `restaurant-delay-burst:r-017`. |
| 9 | `IntakeWorker` → `CandidateIntake.HandleAsync` | **One transaction**: intake log row, alert upsert, and two outbox rows (dashboard, webhook). Commit. Only then is the Kafka offset committed. |
| 10 | `DeliveryDispatcher` | It claims the outbox rows with `FOR UPDATE SKIP LOCKED`. The dashboard row becomes a `pg_notify('alerts_feed', …)`; the webhook row becomes an HMAC-signed POST to `/sinks/webhook`. |
| 11 | `FeedHub` (Api) | It is `LISTEN`ing on `alerts_feed` and pushes the payload to every open browser over SSE (`/api/stream`, `wwwroot/js/feed.js`). |
| 12 | `Endpoints.ReceiveWebhookAsync` | The sink verifies the signature and records the `Idempotency-Key`. A second arrival of the same key counts as a duplicate. |

---

## 2. Kafka (Redpanda) concepts, as they appear in the code

**Producer guarantees** (`KafkaClients.ProducerConfig`)
- `acks=all`: the broker confirms a write only after it is replicated.
- `enable.idempotence=true`: producer retries can't create duplicates or reorder records within a partition.
- Both are cheap insurance, and they say nothing about *consumer-side* duplicates. Those are the hard part.

**Keys → partitions**
Orders are keyed by restaurant, so the order of one order's events is preserved. Ticks have no key: the simulator picks each
partition explicitly (`new TopicPartition(topic, p)`), because the point of a tick is to reach *every* partition.

**Consumer groups**
The engine logs `Assigned partitions: 0,1,2,3,4,5` at start. One engine owns all six. Start a second engine with the same
`GroupId` and you'll see a rebalance split them 3/3. That is exactly what breaks rule B in M2 (Break #4), because the zone counts
then live in two processes.

**Offsets and commits, the naive way** (`EngineWorker`, `EnableAutoCommit = true`)
librdkafka stores each offset as soon as `Consume()` returns, and commits on a 5-second timer. That timer knows nothing about
our in-memory windows. Kill the engine and:
- the windows are gone, so incidents in progress are missed; and
- everything since the last auto-commit is read again, so alerts are repeated.

That is Break #1, measured in M2.

**Offsets and commits, the careful way** (`IntakeWorker`, `EnableAutoCommit = false`)
Process → database commit → `consumer.Commit(result)`. A crash between the two replays the record, and the intake log
turns the replay into a recorded repeat. This is at-least-once plus idempotency.

---

## 3. Time: one function decides naive vs robust

Every evaluator is written against two things: *the time assigned to a record* and *the watermark*. `RuleEngine.Process` gets the time
from its caller. In M1 that is `ProcessingTimeClock.Assign`, which ignores `occurredAt`. In M3, event-time mode passes `occurredAt` and
keeps a per-partition watermark. Nothing else changes, so the before/after comparison is honest.

`WindowedCountEvaluator` shows the asymmetry from SPEC §5.2 in code:
- `OnEvent` fires as soon as the count reaches the threshold (**early firing**: a count can only grow).
- `OnWatermark` → `CheckResolution` resolves only windows that are **closed** (`Panes.LastClosed`).

The test `Resolves_only_after_the_watermark_closes_a_window_below_threshold` walks through the exact minutes.

---

## 4. PostgreSQL techniques worth stealing

| Technique | Where | Why |
|-----------|-------|-----|
| `INSERT … ON CONFLICT … DO UPDATE … RETURNING (xmax = 0)` | `CandidateIntake` | One round trip both records the candidate and tells you if it is new. `xmax` is 0 only for a freshly inserted row version. |
| `FOR UPDATE SKIP LOCKED` | `DeliveryDispatcher` | A work queue in a table: competing workers skip rows another worker holds instead of waiting for them. Proven by `Two_dispatchers_never_send_the_same_delivery_twice`. |
| Transactional `NOTIFY` | `DashboardChannel` | The notification is delivered only if the transaction that marks the row "sent" commits, so the dashboard and the database can't disagree. |
| `pg_advisory_lock` | `MigrationRunner` | A named mutex inside the database, so two migrators starting together can't both run a script. |
| Partial index `WHERE status = 'pending'` | `0001_initial_schema.sql` | It indexes only the small "to do" slice of a table that keeps growing. |
| Script + journal row in one transaction | `MigrationRunner` | A crash can't leave a migration half-applied but unrecorded. |

---

## 5. .NET patterns used

- **Generic Host + `BackgroundService`**: each long-running loop is a hosted service. `Consume()` blocks a thread, so consume loops run on a dedicated thread (`TaskCreationOptions.LongRunning`) instead of borrowing one from the thread pool.
- **Options pattern** (`IOptions<T>`): settings bind from appsettings → env vars (`Engine__EmitMode=OnTransition`).
- **Profiles** (`Profiles.ApplyProfile`): the preset is inserted as the *lowest-priority* configuration source, so any single key still overrides it (ADR-0008).
- **Fail fast on unsupported modes**: `EngineOptions.EnsureSupported` and `NotifierOptions.EnsureSupported` refuse robust settings that M1 doesn't implement yet. Silently ignoring a setting is worse than crashing.
- **`TimeProvider`**: no `DateTime.UtcNow` in Core. Tests use `FakeTimeProvider` to move time by hand (`TimeTests`).
- **Central Package Management** (`Directory.Packages.props`) and shared build settings (`Directory.Build.props`, warnings are errors).
- **Microsoft Testing Platform**: .NET 10's `dotnet test` runs xUnit v3 test projects as executables. `global.json` opts in, and the syntax is `dotnet test --project …`.
- **Testcontainers**: integration tests start a real PostgreSQL in Docker, and each test class gets its own database (`PostgresFixture`).

---

## 6. Surprises and lessons (keep these)

1. **A guard is a statistics problem.** With `minDenominator = 20` (later 200), normal traffic produced a false "cancellation spike"
   in the no-incident test. At a true rate of 3.5% and n = 200, "twice the baseline" is only about 2 standard deviations away, and the rule
   checks every minute, so it gets many chances. We raised it to 300. The test that caught this, `Ordinary_traffic_raises_no_alerts`, is the
   most valuable test in the repo: it measures *precision*, not just detection.
2. **Rules leak into each other.** A rider shortage also makes pickups late. Without `stage = kitchen` on rule A, one incident
   would raise two kinds of alert. Write rules about causes, not symptoms.
3. **Detection speed is a simulator design decision too.** Emitting `order.delayed` when the ETA slips (2–5 min after accept) instead of
   when food is finally ready (10–65 min) is the difference between detecting a meltdown in about 40 s and about 5 min of wall time.
4. **The spec had a bug.** It said to drop stuck-pickup state at `deadline + 2 min`, which would have made resolution impossible. Writing
   the test for "a late pickup resolves the alert" exposed it before any code shipped.
5. **The sim clock must survive restarts.** A restarted simulator starting again at wall-clock "now" would send event time *backwards*
   by hours. It now resumes after the newest event in the log.

---

## 7. Try it yourself

1. `docker compose -f deploy/compose/docker-compose.yml up -d --build`, then `./scripts/demo.ps1 -NoStart`.
2. Open http://localhost:8090 (Redpanda Console). Look at `order-events`: partitions, keys, and ticks on every partition.
3. Open any alert on the dashboard. Its **Repeats** column is EveryMatch noise. `/admin/sinks/webhook` shows the duplicates the receiver saw.
4. `docker compose -f deploy/compose/docker-compose.yml restart engine` in the middle of a meltdown, and watch what happens to its alert.
   That is a preview of Break #1.
5. Set `NoiseCut__Engine__EmitMode=OnTransition` on the engine and re-run: candidates per incident drop to 1–2.
