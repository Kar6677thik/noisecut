#!/usr/bin/env bash
# M1 demo: start the local stack, inject the three reference incidents, and check that each one raises its alert.
# Same steps as scripts/demo.ps1. Needs: docker, curl, grep.
#
#   ./scripts/demo.sh             # start the stack and run the demo
#   NO_START=1 ./scripts/demo.sh  # stack already running
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
ADMIN_KEY="${NOISECUT_ADMIN_KEY:-local-dev-admin-key}"
DEMO_RATE="${DEMO_RATE:-600}"
NORMAL_RATE="${NORMAL_RATE:-300}"
TIMEOUT_MINUTES="${TIMEOUT_MINUTES:-12}"
COMPOSE="$(dirname "$0")/../deploy/compose/docker-compose.yml"

step() { printf '\n==> %s\n' "$1"; }
post() { curl -fsS -X POST "$BASE_URL$1" -H "X-Admin-Key: $ADMIN_KEY" -H 'Content-Type: application/json' -d "$2"; }

if [[ -z "${NO_START:-}" ]]; then
  step "Starting the stack (docker compose up -d --build)"
  docker compose -f "$COMPOSE" up -d --build
fi

step "Waiting for the Api and the simulator"
deadline=$(( $(date +%s) + 180 ))
until curl -fsS "$BASE_URL/readyz" >/dev/null 2>&1 \
   && curl -fsS -H "X-Admin-Key: $ADMIN_KEY" "$BASE_URL/api/simulator/status" 2>/dev/null | grep -q '"running":true'; do
  (( $(date +%s) > deadline )) && { echo "stack did not become ready within 3 minutes"; exit 1; }
  sleep 2
done

step "Raising the order rate to $DEMO_RATE orders/sim-minute for the demo"
post /api/simulator/config "{\"ordersPerSimMinute\":$DEMO_RATE}" >/dev/null

step "Injecting the three reference incidents"
post /api/scenarios/kitchen-meltdown '{"target":"r-017"}' >/dev/null && echo "  started kitchen-meltdown  r-017"
post /api/scenarios/zone-cancel-spike '{"target":"z-02"}' >/dev/null && echo "  started zone-cancel-spike z-02"
post /api/scenarios/rider-shortage '{"target":"z-04"}' >/dev/null && echo "  started rider-shortage    z-04"
started=$(date +%s)

# name ; rule ; grep -E pattern that identifies the expected alert in /api/alerts?rule=...
checks=(
  'A kitchen-meltdown -> restaurant-delay-burst;restaurant-delay-burst;"entityKey":"r-017"'
  'B zone-cancel-spike -> zone-cancellation-spike;zone-cancellation-spike;"entityKey":"z-02"'
  'C rider-shortage -> stuck-pickup;stuck-pickup;"groupKey":"r-0(3[1-9]|40)"'
)
declare -A found=()

step "Waiting for alerts (up to $TIMEOUT_MINUTES minutes)"
deadline=$(( started + TIMEOUT_MINUTES * 60 ))
while :; do
  for check in "${checks[@]}"; do
    IFS=';' read -r name rule pattern <<<"$check"
    [[ -n "${found[$name]:-}" ]] && continue
    if curl -fsS "$BASE_URL/api/alerts?rule=$rule&limit=500" | grep -Eq "$pattern"; then
      found[$name]=$(( $(date +%s) - started ))
      echo "  DETECTED $name after ${found[$name]}s"
    fi
  done
  (( ${#found[@]} == ${#checks[@]} )) && break
  (( $(date +%s) > deadline )) && break
  sleep 5
done

post /api/simulator/config "{\"ordersPerSimMinute\":$NORMAL_RATE}" >/dev/null

step "Result"
status=0
for check in "${checks[@]}"; do
  IFS=';' read -r name _ <<<"$check"
  if [[ -n "${found[$name]:-}" ]]; then echo "  PASS  $name (${found[$name]}s)"; else echo "  FAIL  $name"; status=1; fi
done
echo
curl -fsS "$BASE_URL/api/stats"; echo
echo "  Dashboard: $BASE_URL"
exit $status
