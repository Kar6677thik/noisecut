-- NoiseCut schema v1 (SPEC §11). Applied once by NoiseCut.Migrator and recorded in schema_migrations.
-- Rule of the road: never edit a migration that has shipped; add a new numbered file instead.
-- Tables needed only by robust mode (engine_checkpoints, notification_groups) arrive in later migrations, together with
-- the code that uses them, so each migration in the history tells the story of one milestone.

-- Reference data -----------------------------------------------------------------------------------------------------

CREATE TABLE zones (
    id   text PRIMARY KEY,
    name text NOT NULL
);

CREATE TABLE restaurants (
    id      text PRIMARY KEY,
    name    text NOT NULL,
    zone_id text NOT NULL REFERENCES zones (id)
);

-- Rules ---------------------------------------------------------------------------------------------------------------

CREATE TABLE rules (
    id         text PRIMARY KEY,
    version    int         NOT NULL,
    enabled    boolean     NOT NULL DEFAULT true,
    definition jsonb       NOT NULL,          -- the RuleDefinition JSON from SPEC §5
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- Append-only audit: every version a rule ever had.
CREATE TABLE rule_history (
    id         text        NOT NULL,
    version    int         NOT NULL,
    definition jsonb       NOT NULL,
    changed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (id, version)
);

-- Baselines for "same hour last week" (rule B). Seeded by the migrator; live rows replace them from M3.
CREATE TABLE zone_hourly_stats (
    zone_id    text        NOT NULL REFERENCES zones (id),
    hour_start timestamptz NOT NULL,
    placed     int         NOT NULL,
    cancelled  int         NOT NULL,
    source     text        NOT NULL CHECK (source IN ('seed', 'live')),
    PRIMARY KEY (zone_id, hour_start)
);

-- Alerts --------------------------------------------------------------------------------------------------------------

-- One row per episode (alert_key). The notifier upserts it as candidates arrive.
CREATE TABLE alerts (
    alert_key         text        PRIMARY KEY,
    rule_id           text        NOT NULL,
    rule_version      int         NOT NULL,
    severity          text        NOT NULL,
    entity_type       text        NOT NULL,
    entity_key        text        NOT NULL,
    group_key         text        NOT NULL,
    status            text        NOT NULL CHECK (status IN ('firing', 'acknowledged', 'resolved')),
    episode_start     timestamptz NOT NULL,
    title             text        NOT NULL,
    detail            text        NOT NULL,
    observed          jsonb       NOT NULL,
    candidate_count   int         NOT NULL DEFAULT 1,  -- every candidate received for this key, duplicates included
    suppressed_count  int         NOT NULL DEFAULT 0,
    first_detected_at timestamptz NOT NULL,
    last_candidate_at timestamptz NOT NULL,
    resolved_at       timestamptz,
    acked_at          timestamptz
);

CREATE INDEX alerts_recent_idx ON alerts (last_candidate_at DESC);
CREATE INDEX alerts_rule_idx ON alerts (rule_id, first_detected_at DESC);

-- Intake log. The primary key is the idempotency check of SPEC §7.1: one row per (episode, transition), ever.
-- A duplicate bumps repeat_count instead of inserting, so we can count exactly how many repeats arrived.
CREATE TABLE alert_candidates (
    alert_key      text        NOT NULL,
    status         text        NOT NULL CHECK (status IN ('firing', 'resolved')),
    candidate_id   uuid        NOT NULL,
    rule_id        text        NOT NULL,
    payload        jsonb       NOT NULL,
    received_at    timestamptz NOT NULL DEFAULT now(),
    repeat_count   int         NOT NULL DEFAULT 0,
    last_repeat_at timestamptz,
    PRIMARY KEY (alert_key, status)
);

-- Notifications -------------------------------------------------------------------------------------------------------

-- The transactional outbox (SPEC §7.3, ADR-0007). Rows are written in the same transaction as the alert decision and
-- delivered later by the dispatcher.
-- notification_key = alertKey|kind|step|channel identifies the *logical* notification and is sent as Idempotency-Key.
-- It is deliberately NOT unique in v1: the naive profile must be able to send the same notification twice, so the sink
-- can count it (Break #1 and #3). Robust mode prevents repeats earlier, at intake.
CREATE TABLE deliveries (
    id               uuid        PRIMARY KEY,
    notification_key text        NOT NULL,
    alert_key        text        NOT NULL,
    kind             text        NOT NULL CHECK (kind IN ('firing', 'resolved', 'escalation')),
    step             int         NOT NULL DEFAULT 0,
    channel          text        NOT NULL,
    payload          jsonb       NOT NULL,
    status           text        NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'sent', 'dead')),
    attempts         int         NOT NULL DEFAULT 0,
    next_attempt_at  timestamptz NOT NULL DEFAULT now(),
    created_at       timestamptz NOT NULL DEFAULT now(),
    sent_at          timestamptz,
    last_error       text
);

-- A partial index: the dispatcher only ever looks for pending rows, and they are a tiny fraction of the table.
CREATE INDEX deliveries_due_idx ON deliveries (next_attempt_at) WHERE status = 'pending';
CREATE INDEX deliveries_alert_idx ON deliveries (alert_key);
CREATE INDEX deliveries_key_idx ON deliveries (notification_key);

-- What the built-in webhook sink received. receive_count > 1 means the same notification arrived more than once.
CREATE TABLE webhook_receipts (
    idempotency_key   text        PRIMARY KEY,
    first_received_at timestamptz NOT NULL DEFAULT now(),
    last_received_at  timestamptz NOT NULL DEFAULT now(),
    receive_count     int         NOT NULL DEFAULT 1,
    body              jsonb       NOT NULL
);
