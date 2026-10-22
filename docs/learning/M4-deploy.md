# M4: from `docker compose up` to a deploy nobody types

M1–M3 ran on a laptop. M4 makes the same system run on a small shared server. Every change arrives through GitHub Actions,
and nothing is reachable that shouldn't be. This note explains the ideas; the commands are in [`docs/runbooks/`](../runbooks/).

---

## 1. Images: ship only what runs

A multi-stage `Dockerfile` builds in the SDK image and copies only the published output into the **chiseled** runtime image:
- Chiseled images are Ubuntu with nearly everything removed: no shell, no package manager, and a non-root user by default.
- An attacker who gets code execution finds no `sh` to continue with, and a scanner finds far fewer packages to flag.
- The cost: `docker exec … sh` doesn't work. You debug with logs, metrics and `kubectl debug`, which is what production expects anyway.

Each image is tagged with the **commit SHA**, never only `latest`. A tag that means "exactly this code" makes rollbacks a lookup
(deploy an older SHA), and the pod list shows what really runs.

## 2. The Kubernetes objects, and why each is the shape it is

| Object | Used for | Why this kind |
|--------|----------|---------------|
| **StatefulSet** + PVC | Redpanda, PostgreSQL | stable identity *and* a disk that follows the pod |
| **StatefulSet** without a PVC | engine ×3 | only the stable *names* (`engine-0..2`) are needed: static Kafka membership keys on them. The state lives in PostgreSQL checkpoints |
| Deployment | simulator, notifier, api, Prometheus, Grafana, Mailpit | interchangeable pods |
| Job | topic creation, migrations | run to completion once per deploy |
| PodDisruptionBudget | engine (`minAvailable: 2`) | a node drain never takes more than one engine at once |
| NetworkPolicy | everything | default-deny: other namespaces can't reach the database, even on the same node |
| ConfigMap / Secret | config / passwords | config is in git; secrets are created by CI from GitHub secrets |

**Probes.** *Readiness* ("send me traffic") and *liveness* ("restart me") are different questions. A slow database should make the Api
unready, not restart it in a loop.

**Requests vs. limits.** The *request* is what the scheduler reserves, and our total (≈ 2.1 GiB) fits the budget. The *limit* is the
ceiling before the kernel kills the container. Setting both on every container stops one service from starving the other apps on the server.

## 3. Ordering a deploy

`kubectl apply` of everything at once would race: new engine code could start before its migration ran. `scripts/deploy/apply.sh` applies in two passes:
1. infrastructure, config and the Jobs, then wait for the migrations;
2. the workloads (label `noisecut/workload=true`), then wait for every rollout.

CI and the local rehearsal run the *same script*. "It worked in the rehearsal" therefore says something about the real deploy.

## 4. Least-privilege CI

The question: how does a GitHub runner on the internet change a cluster on a private home network, without anyone being able to take the cluster over?
- **Network:** the runner joins the tailnet as an *ephemeral* node tagged `tag:ci`. The tailnet policy lets that tag reach port 6443, and nothing else.
  The cluster API is never on the public internet.
- **Identity:** the runner authenticates as a ServiceAccount whose RoleBinding grants `admin` **in the `noisecut` namespace only**.
  It can't see nodes, other namespaces or `kube-system` secrets. The rehearsal checked each of these with `kubectl auth can-i`.
- **Certificates:** the API server's certificate doesn't list the Tailscale IP. Instead of turning off verification, the kubeconfig names a SAN the certificate *does*
  have (`tls-server-name`). The connection stays verified.
- **Secrets:** they flow from GitHub secrets to a Kubernetes Secret on each deploy, and are never in git, in an image or in a log.

## 5. A public showcase that is safe to leave open

- Anonymous visitors get read-only pages. Everything that changes state needs the admin cookie or the `X-Admin-Key` header.
- **Passwords:** only a PBKDF2-SHA256 hash (600k iterations, random salt) is stored, and it is compared in constant time.
- **Guessing:** 10 login attempts/min per client, and a 15-minute lockout after 5 failures.
- **Cookies:** `HttpOnly` (scripts can't read it), `Secure` (HTTPS only), and `SameSite=Strict`; antiforgery tokens on top of that.
- **Abuse:** a global 240 requests/min per client, and at most 3 concurrent SSE streams. Behind Cloudflare, the client is `CF-Connecting-IP`.

## 6. Rehearse on something you can throw away

A k3s of the server's version, running in a Docker container, took about a minute to create. It caught two bugs (a ConfigMap name, a read-only `/tmp`).
On the shared server they would have been outages, debugged with no shell in the images. Rehearsal is cheap; production debugging isn't.
