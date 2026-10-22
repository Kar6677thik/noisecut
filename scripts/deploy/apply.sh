#!/usr/bin/env bash
# Deploy NoiseCut to the cluster in $KUBECONFIG. Used by .github/workflows/deploy.yml and by the local rehearsal
# (docs/runbooks/k3s-rehearsal.md), so the thing you rehearse is exactly the thing CI runs.
#
#   scripts/deploy/apply.sh <overlay-dir>
#
# Secrets come from the environment (CI maps GitHub secrets to these names); nothing secret is ever written to the repo:
#   NC_POSTGRES_PASSWORD  NC_WEBHOOK_SECRET  NC_ADMIN_KEY  NC_ADMIN_PASSWORD_HASH  NC_GRAFANA_ADMIN_PASSWORD
#   GHCR_USER + GHCR_PULL_TOKEN   (optional: lets the cluster pull private images from ghcr.io)
#
# Order matters:
#   1. secrets;
#   2. infrastructure, config and the one-shot Jobs (topics, migrations), then wait until the migrations have run;
#   3. the application workloads, then wait for every rollout.
# So new code never runs against an old schema. Nothing is ever deleted except the two finished Jobs, which Kubernetes
# can't update in place. No --prune: an object missing from git is left alone, not silently removed.
set -euo pipefail

overlay="${1:?usage: apply.sh <overlay-dir>}"
ns=noisecut
k() { kubectl -n "$ns" "$@"; }
step() { printf '\n==> %s\n' "$1"; }

: "${NC_POSTGRES_PASSWORD:?}" "${NC_WEBHOOK_SECRET:?}" "${NC_ADMIN_KEY:?}" "${NC_ADMIN_PASSWORD_HASH:?}" "${NC_GRAFANA_ADMIN_PASSWORD:?}"

step "Secrets"
k create secret generic noisecut-secrets \
  --from-literal=POSTGRES_PASSWORD="$NC_POSTGRES_PASSWORD" \
  --from-literal=WEBHOOK_SECRET="$NC_WEBHOOK_SECRET" \
  --from-literal=ADMIN_KEY="$NC_ADMIN_KEY" \
  --from-literal=ADMIN_PASSWORD_HASH="$NC_ADMIN_PASSWORD_HASH" \
  --from-literal=GRAFANA_ADMIN_PASSWORD="$NC_GRAFANA_ADMIN_PASSWORD" \
  --dry-run=client -o yaml | k apply -f -
if [[ -n "${GHCR_PULL_TOKEN:-}" ]]; then
  k create secret docker-registry noisecut-registry \
    --docker-server=ghcr.io --docker-username="${GHCR_USER:?}" --docker-password="$GHCR_PULL_TOKEN" \
    --dry-run=client -o yaml | k apply -f -
fi

step "Render $overlay"
rendered=$(mktemp)
trap 'rm -f "$rendered"' EXIT
kubectl kustomize --load-restrictor=LoadRestrictionsNone "$overlay" > "$rendered"

step "Infrastructure, config and one-shot jobs"
# A Job's pod template is immutable, so last release's finished runs are removed before the new ones are applied.
k delete job migrator topics-init --ignore-not-found --wait=true
kubectl apply -f "$rendered" --selector 'noisecut/workload!=true'
k rollout status statefulset/postgres --timeout=5m
k rollout status statefulset/redpanda --timeout=5m
k wait --for=condition=complete job/topics-init --timeout=5m
k wait --for=condition=complete job/migrator --timeout=5m

step "Application workloads"
kubectl apply -f "$rendered"
for w in deployment/simulator statefulset/engine deployment/notifier deployment/api deployment/prometheus deployment/grafana deployment/mailpit; do
  k rollout status "$w" --timeout=5m
done

step "Deployed"
k get pods -o wide
