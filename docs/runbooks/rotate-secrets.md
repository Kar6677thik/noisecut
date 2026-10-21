# Runbook: rotate secrets

All runtime secrets live in GitHub secrets and are copied into the `noisecut-secrets` Secret on every deploy. To rotate most of them,
**change the GitHub secret and re-run the deploy workflow**. Then restart what reads it, because environment variables are read at start:

| Secret | After changing it |
|--------|-------------------|
| `NC_ADMIN_PASSWORD_HASH`, `NC_ADMIN_KEY` | `kubectl -n noisecut rollout restart deployment/api` (the deploy also restarts it if the image changed) |
| `NC_WEBHOOK_SECRET` | restart both `api` and `notifier` (both sides of the HMAC must agree) |
| `NC_GRAFANA_ADMIN_PASSWORD` | Grafana keeps its first admin password in its database; ours is an emptyDir, so a restart applies it |
| `GHCR_PULL_TOKEN` | nothing else; used at the next image pull |
| `NC_POSTGRES_PASSWORD` | **two steps**: PostgreSQL keeps the old one. First `kubectl -n noisecut exec postgres-0 -- psql -U noisecut -d noisecut -c "ALTER USER noisecut PASSWORD '<new>'"`, then update the GitHub secret and re-deploy |

## The CI ServiceAccount token
```bash
kubectl -n noisecut delete secret noisecut-deployer-token
kubectl apply -f deploy/k3s/bootstrap/ci-access.yaml     # a new token is issued
# then repeat bootstrap step 2 and update KUBE_CONFIG_B64
```
The old token stops working the moment its Secret is deleted.

## The Tailscale OAuth client
Create a new client in the Tailscale console, update `TS_OAUTH_CLIENT_ID` / `TS_OAUTH_SECRET`, then delete the old client.
CI nodes are ephemeral, so nothing else needs cleaning up.
