-- M3: tables for the robust profile.

-- Engine checkpoints (SPEC §6.3, ADR-0005): per (stage, topic, partition), the state *and* the next offset to read,
-- always written together in one statement, so they can never disagree.
-- epoch is a fencing token: whoever takes a partition bumps it, and every later write must present the epoch it was given.
-- A "zombie" that lost the partition (but hasn't noticed yet) presents an old epoch and its write changes nothing.
CREATE TABLE engine_checkpoints (
    stage       text        NOT NULL,          -- 'order' or 'zone'
    topic       text        NOT NULL,
    partition   int         NOT NULL,
    epoch       bigint      NOT NULL DEFAULT 0,
    owner       text,                          -- engine instance holding the partition (for humans; fencing uses epoch)
    next_offset bigint,                        -- NULL = nothing processed yet: start from the beginning
    watermark   timestamptz,
    state       jsonb,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (stage, topic, partition)
);

-- Notifier policy (SPEC §7.2) -----------------------------------------------------------------------------------------

-- Did a firing notification for this episode go out (or get queued in a group)? A "resolved" is only sent if it did:
-- nobody wants to hear that something they were never told about is over.
ALTER TABLE alerts ADD COLUMN notified boolean NOT NULL DEFAULT false;

-- Cooldown: the last time each (rule, entity) produced a notification, and which alert absorbs suppressed repeats.
CREATE TABLE cooldowns (
    rule_id     text        NOT NULL,
    entity_key  text        NOT NULL,
    alert_key   text        NOT NULL,
    notified_at timestamptz NOT NULL,
    PRIMARY KEY (rule_id, entity_key)
);

-- Grouping: alerts of one rule and group (e.g. zone) that arrive within groupWait become one notification.
CREATE TABLE notification_groups (
    id         uuid        PRIMARY KEY,
    rule_id    text        NOT NULL,
    group_key  text        NOT NULL,
    kind       text        NOT NULL CHECK (kind IN ('firing', 'resolved')),
    status     text        NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'flushed')),
    opened_at  timestamptz NOT NULL DEFAULT now(),
    flush_at   timestamptz NOT NULL,
    flushed_at timestamptz,
    members    jsonb       NOT NULL DEFAULT '[]'      -- [{alertKey, entityKey, title, detail}]
);

-- At most one open group per (rule, group, kind): concurrent notifiers all join the same one.
CREATE UNIQUE INDEX notification_groups_open_idx ON notification_groups (rule_id, group_key, kind) WHERE status = 'open';
CREATE INDEX notification_groups_due_idx ON notification_groups (flush_at) WHERE status = 'open';

-- Escalation: each step fires at most once per alert, enforced by the primary key.
CREATE TABLE escalations (
    alert_key    text        NOT NULL,
    step         int         NOT NULL,
    escalated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (alert_key, step)
);

CREATE INDEX alerts_firing_idx ON alerts (first_detected_at) WHERE status = 'firing';
