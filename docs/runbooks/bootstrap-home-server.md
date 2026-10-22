# Runbook: one-time bootstrap of the home server

Done **once**, by the owner. After this, every merge to `main` deploys automatically (`.github/workflows/deploy.yml`), and
nobody runs `kubectl` against the server by hand again. Values in `<angle brackets>` are yours. Never commit them.

| Placeholder | Meaning |
|-------------|---------|
| `<server-tailnet-ip>` | the server's Tailscale IP (`tailscale ip -4` on the server) |
| `<tunnel-name>` | the name of your existing Cloudflare tunnel (`cloudflared tunnel list`) |

You'll need: a shell on the server with your cluster-admin kubeconfig, access to the Tailscale admin console, and the repo's
GitHub settings.

---

## 1. Namespace and the CI identity (on the server)

```bash
git clone https://github.com/Kar6677thik/noisecut.git && cd noisecut
kubectl apply -f deploy/k3s/bootstrap/ci-access.yaml
```
This creates the `noisecut` namespace, the `noisecut-deployer` ServiceAccount, a RoleBinding to the built-in `admin` role
**inside `noisecut` only**, and a token for it. Check the scope:

```bash
kubectl auth can-i create deployments -n noisecut --as=system:serviceaccount:noisecut:noisecut-deployer   # yes
kubectl auth can-i get secrets -n kube-system  --as=system:serviceaccount:noisecut:noisecut-deployer      # no
kubectl auth can-i create namespaces           --as=system:serviceaccount:noisecut:noisecut-deployer      # no
```

## 2. A kubeconfig for CI (on the server)

The k3s API certificate lists the names and IPs k3s knew at install time. A Tailscale IP is usually not one of them. Check:

```bash
echo | openssl s_client -connect 127.0.0.1:6443 2>/dev/null | openssl x509 -noout -ext subjectAltName
```

If `<server-tailnet-ip>` is not listed (most likely), pass a name that **is** listed (e.g. `kubernetes`) as `tls-server-name`:

```bash
./deploy/k3s/bootstrap/make-kubeconfig.sh https://<server-tailnet-ip>:6443 kubernetes > /tmp/noisecut-deployer.kubeconfig
KUBECONFIG=/tmp/noisecut-deployer.kubeconfig kubectl get pods -n noisecut   # "No resources found" = it works
base64 -w0 /tmp/noisecut-deployer.kubeconfig; echo                          # copy this for step 5
rm /tmp/noisecut-deployer.kubeconfig
```

## 3. Tailscale: let CI reach the API server, and nothing else

The deploy job joins your tailnet as an **ephemeral** node tagged `tag:ci`.
1. If you don't already have an OAuth client for CI: Tailscale admin → Settings → OAuth clients → create one with the
   **Devices: Core (write)** and **Auth Keys (write)** scopes, restricted to tag `tag:ci`. Keep its client id and secret for step 5.
2. In the access policy, make sure `tag:ci` exists and may reach the server's API port. If other projects' CI already uses `tag:ci`
   for SSH, add only port 6443. For example, in the `grants` (or `acls`) section:
   ```jsonc
   { "src": ["tag:ci"], "dst": ["<server-tailnet-ip>"], "ip": ["tcp:6443"] }
   ```

## 4. Cloudflare tunnel: the public hostname (on the server)

Add two rules to `~/.cloudflared/config.yml`, **above** the final catch-all `- service: http_status:404`. The more specific
`/grafana` path rule must come first:

```yaml
  - hostname: noisecut.karthiksurkanti.in
    path: ^/grafana
    service: http://127.0.0.1:30531
  - hostname: noisecut.karthiksurkanti.in
    service: http://127.0.0.1:30530
```

Then create the DNS record and restart the tunnel the way you normally run it:

```bash
cloudflared tunnel route dns <tunnel-name> noisecut.karthiksurkanti.in
cloudflared tunnel ingress validate
# restart cloudflared (e.g. sudo systemctl restart cloudflared)
```

Only the Api and Grafana are routed. Redpanda, PostgreSQL, Prometheus, Mailpit and the metrics ports never are.

## 5. GitHub secrets (repo → Settings → Secrets and variables → Actions)

| Secret | Value |
|--------|-------|
| `KUBE_CONFIG_B64` | the base64 line from step 2 |
| `TS_OAUTH_CLIENT_ID`, `TS_OAUTH_SECRET` | the Tailscale OAuth client from step 3 |
| `GHCR_PULL_TOKEN` | a classic personal access token with only **read:packages**, so the cluster can pull the private images |
| `NC_POSTGRES_PASSWORD` | a long random string (`openssl rand -base64 30`) |
| `NC_WEBHOOK_SECRET` | another long random string |
| `NC_ADMIN_KEY` | another long random string: the `X-Admin-Key` for scripts |
| `NC_ADMIN_PASSWORD_HASH` | `echo '<your admin password>' \| docker run --rm -i ghcr.io/kar6677thik/noisecut-api:<any-sha> hash-password`, or locally: `echo '<pw>' \| dotnet run --project src/NoiseCut.Api -- hash-password` |
| `NC_GRAFANA_ADMIN_PASSWORD` | a long random string (Grafana's own admin; visitors are anonymous viewers) |

Keep `NC_POSTGRES_PASSWORD` stable: PostgreSQL keeps the password it was first initialised with (see rotate-secrets.md to change it).

Optionally create an **environment** named `home` (Settings → Environments) and add required reviewers. Every deploy then waits for your approval.

## 6. First deploy

Push to `main` (or Actions → deploy → Run workflow). Watch the run; then:
- https://noisecut.karthiksurkanti.in shows the dashboard, and https://noisecut.karthiksurkanti.in/grafana/ the metrics;
- `/admin/login` accepts your password, and admin actions are refused without it.

If something fails, see `deploy.md` → Troubleshooting.
