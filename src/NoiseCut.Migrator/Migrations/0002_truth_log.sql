-- M2: the simulator's truth log. Every injected scenario is recorded here, so "missed" and "false" alerts can be computed
-- with SQL instead of eyeballed.

CREATE TABLE sim_scenarios (
    id            uuid             PRIMARY KEY,
    name          text             NOT NULL,
    target        text             NOT NULL,
    restaurants   text[]           NOT NULL,       -- restaurants affected (all restaurants of the zone for zone scenarios)
    starts_at     timestamptz      NOT NULL,       -- sim time
    ends_at       timestamptz      NOT NULL,       -- sim time
    intensity     double precision NOT NULL,
    expected_rule text,                            -- NULL = negative control: any alert it causes is a false alarm
    injected_at   timestamptz      NOT NULL DEFAULT now()   -- wall time, the start of the "time to detect" stopwatch
);

-- Which alert belongs to which scenario.
-- An alert is attributed to a scenario when it is about the scenario's target (the zone, one of its restaurants, or an order
-- grouped under one of its restaurants) and its episode starts in [starts_at − 15 min, ends_at + 60 min] of sim time.
-- The slack exists because effects lag causes: a delay is noticed minutes after the order is accepted, and a stuck-pickup alert
-- fires 20 minutes after the food is ready. It is a heuristic, written down so every experiment uses the same one.
CREATE VIEW alert_attribution AS
SELECT a.alert_key,
       a.rule_id,
       s.id AS scenario_id,
       -- NULL = 'x' is NULL, not false, in SQL; coalesce so negative controls count as "not expected".
       coalesce(a.rule_id = s.expected_rule, false) AS expected
FROM alerts a
JOIN sim_scenarios s
  ON (a.entity_key = s.target OR a.entity_key = ANY (s.restaurants) OR a.group_key = ANY (s.restaurants))
 AND a.episode_start BETWEEN s.starts_at - interval '15 minutes' AND s.ends_at + interval '60 minutes';

-- One row per alert: a true positive if some scenario expected exactly this rule for this target, otherwise a false positive.
CREATE VIEW alert_classification AS
SELECT a.alert_key,
       a.rule_id,
       a.entity_key,
       a.first_detected_at,
       coalesce(bool_or(t.expected), false) AS true_positive
FROM alerts a
LEFT JOIN alert_attribution t ON t.alert_key = a.alert_key
GROUP BY a.alert_key, a.rule_id, a.entity_key, a.first_detected_at;

-- One row per injected scenario: was it detected, how fast, and how noisy was the response?
CREATE VIEW scenario_outcomes AS
SELECT s.id,
       s.name,
       s.target,
       s.expected_rule,
       s.starts_at,
       s.ends_at,
       s.injected_at,
       count(a.alert_key) FILTER (WHERE t.expected)                                     AS matching_alerts,
       count(a.alert_key) FILTER (WHERE NOT t.expected)                                 AS other_alerts,
       extract(epoch FROM min(a.first_detected_at) FILTER (WHERE t.expected) - s.injected_at) AS seconds_to_detect,
       coalesce(sum(a.candidate_count) FILTER (WHERE t.expected), 0)                    AS candidates
FROM sim_scenarios s
LEFT JOIN alert_attribution t ON t.scenario_id = s.id
LEFT JOIN alerts a ON a.alert_key = t.alert_key
GROUP BY s.id;
