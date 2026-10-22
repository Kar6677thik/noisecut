# Runbook: rehearse the deployment on a throwaway k3s

Before touching a real server, run the *same* deploy script against a k3s that lives in a Docker container on your machine.
It catches broken manifests, missing permissions and network-policy mistakes for free. M4 found two bugs this way:
an un-rewritten ConfigMap name, and Mailpit needing a writable `/tmp`.

```bash
# 1. a k3s of the same version as the server, with the two NodePorts published
docker run -d --name nc-k3s --privileged -p 6550:6443 -p 30530:30530 -p 30531:30531 \
  rancher/k3s:v1.36.4-k3s1 server --disable=traefik --write-kubeconfig-mode=644

# 2. build the images and load them into the cluster (no registry involved)
docker compose -f deploy/compose/docker-compose.yml build
for s in simulator engine notifier api migrator; do docker tag noisecut-$s ghcr.io/kar6677thik/noisecut-$s:local; done
docker save $(for s in simulator engine notifier api migrator; do echo ghcr.io/kar6677thik/noisecut-$s:local; done) \
  | docker exec -i nc-k3s ctr -n k8s.io images import -

# 3. the bootstrap, exactly as on the server, then a kubeconfig for the scoped ServiceAccount
docker cp deploy/k3s/bootstrap/ci-access.yaml nc-k3s:/tmp/ && docker exec nc-k3s kubectl apply -f /tmp/ci-access.yaml
docker cp deploy/k3s/bootstrap/make-kubeconfig.sh nc-k3s:/tmp/
docker exec nc-k3s sh /tmp/make-kubeconfig.sh https://127.0.0.1:6443 kubernetes \
  | sed 's#127.0.0.1:6443#127.0.0.1:6550#' > /tmp/nc-deployer.kubeconfig

# 4. deploy as CI would (any values work for the secrets)
export KUBECONFIG=/tmp/nc-deployer.kubeconfig
export NC_POSTGRES_PASSWORD=pg NC_WEBHOOK_SECRET=hook NC_ADMIN_KEY=key NC_GRAFANA_ADMIN_PASSWORD=gf
export NC_ADMIN_PASSWORD_HASH="$(echo admin | docker run --rm -i noisecut-api hash-password)"
bash scripts/deploy/apply.sh deploy/k3s/overlays/local
```

Then try:
- http://localhost:30530 (dashboard) and http://localhost:30531/grafana/;
- `curl -X POST localhost:30530/api/scenarios/kitchen-meltdown` → **401**; with `-H "X-Admin-Key: key"` → 201;
- `kubectl -n noisecut delete pod engine-1`, then watch it come back and take the same partitions (`kubectl -n noisecut logs engine-1`);
- `kubectl auth can-i get nodes` → **no**: the deployer can only touch its namespace.

On Windows/Git Bash, set `MSYS_NO_PATHCONV=1` for the `docker cp`/`docker exec` commands (it stops Git Bash rewriting `/tmp` paths).
Clean up with `docker rm -f nc-k3s` (its data goes with it).
