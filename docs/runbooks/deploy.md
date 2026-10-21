# Runbook: deploy, roll back, inspect

## How a deploy happens
1. A PR merges to `main`, and the **ci** workflow runs (build, tests, manifests).
2. When ci succeeds, **deploy** starts (`.github/workflows/deploy.yml`):
   1. builds 5 images and pushes them as `ghcr.io/kar6677thik/noisecut-<service>:<commit-sha>`;
   2. joins the tailnet as an ephemeral `tag:ci` node;
   3. runs `scripts/deploy/apply.sh deploy/k3s/overlays/home` as the namespace-scoped ServiceAccount: secrets, then infrastructure and Jobs, then wait for migrations, then the workloads, then wait for rollouts;
   4. smoke-tests the public URL, including "an anonymous admin call is refused".
3. The run summary lists every pod and the image it runs.

Nothing deploys if ci fails. Two deploys never run at once (`concurrency: deploy-home`).

## Roll back
Every image is tagged with its commit, so a rollback is "deploy an older SHA":
1. Find a known-good commit on `main` (`git log --first-parent main`).
2. Actions → **deploy** → Run workflow → paste the full SHA.
3. The workflow skips the image build (the images exist) and applies that commit's manifests with its images.

Schema migrations only go forward. A rollback runs the *older* migrator, which sees every script already applied and does nothing.
Every migration so far is additive (new tables and columns), so older code keeps working against the newer schema.

## Inspect (owner, from the server)
```bash
kubectl -n noisecut get pods                                     # what runs
kubectl -n noisecut logs statefulset/engine --prefix --tail=50   # engine logs (all replicas: add -l app=engine)
kubectl -n noisecut exec redpanda-0 -- rpk topic list            # topics and partitions
kubectl -n noisecut exec redpanda-0 -- rpk group describe noisecut-engine   # partition owners and lag
kubectl -n noisecut exec postgres-0 -- psql -U noisecut -d noisecut \
  -c "select stage, partition, epoch, next_offset, updated_at from engine_checkpoints order by 1,2"
kubectl -n noisecut port-forward svc/mailpit 8025                # escalation emails at http://localhost:8025
```

## Reset the showcase's data
Only when you really want to start over (it deletes alerts, checkpoints and topics):
```bash
kubectl -n noisecut scale statefulset/engine deployment/simulator deployment/notifier --replicas=0
kubectl -n noisecut delete pvc data-postgres-0 data-redpanda-0   # after: kubectl -n noisecut delete pod postgres-0 redpanda-0
# then re-run the deploy workflow: topics, schema and seeds are recreated.
```

## Troubleshooting
| Symptom | Likely cause |
|---------|--------------|
| Deploy skipped with a notice | `KUBE_CONFIG_B64` not set: the bootstrap isn't done |
| Tailscale step fails | OAuth client scopes/tag, or `tag:ci` missing from the policy |
| `x509: certificate is valid for ..., not <ip>` | kubeconfig lacks `tls-server-name` (bootstrap step 2) |
| `Forbidden` from kubectl | RoleBinding missing, or the deploy touched another namespace (it shouldn't) |
| Pods `ImagePullBackOff` | `GHCR_PULL_TOKEN` missing or lacks `read:packages` |
| Migrator Job fails with auth errors | `NC_POSTGRES_PASSWORD` changed after the first deploy (see rotate-secrets.md) |
| Smoke test fails but pods are healthy | Cloudflare tunnel rule or DNS route (bootstrap step 4) |
