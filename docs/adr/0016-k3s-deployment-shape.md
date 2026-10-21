# ADR-0016: How NoiseCut is laid out on k3s, and how CI applies it

- **Status:** Accepted (details ADR-0009)
- **Date:** 2026-10-21
- **Milestone:** M4

## Context
ADR-0009 decided *where* and *who*: k3s on the home server, deployed by GitHub Actions over Tailscale as a namespace-scoped
ServiceAccount. Building the manifests raised questions ADR-0009 didn't answer. All of these were tested on a throwaway k3s v1.36
in Docker, with the same version as the server (`docs/runbooks/k3s-rehearsal.md`), before anything touched the server.

## Decisions
1. **Engine = StatefulSet without volumes.**
   - The engine keeps its state in PostgreSQL checkpoints, not on disk. It needs a StatefulSet for its *stable pod names*: static Kafka membership (ADR-0014) keys on the hostname.
   - The rehearsal force-deleted `engine-1`. The replacement was ready in 3 s, took back exactly partitions 1 and 4 (epoch 2, restored state), and the other two engines never rebalanced.
2. **The bootstrap owns cluster-scoped objects; CI owns everything else.**
   - The namespace, the deployer ServiceAccount and its RoleBinding to the built-in `admin` role are applied once by the owner (`deploy/k3s/bootstrap/ci-access.yaml`).
   - CI cannot create namespaces, read other namespaces, or see nodes. The rehearsal checked all of this with `kubectl auth can-i`.
3. **Two-pass apply, never prune** (`scripts/deploy/apply.sh`, shared by CI and the rehearsal):
   - First pass: infrastructure, config and Jobs (topics, migrations). Wait for the migrations.
   - Second pass: the application workloads (label `noisecut/workload=true`).
   - New code therefore never meets an old schema. No `--prune`: an object missing from git is not silently deleted.
4. **Secrets never enter git or the image.** CI creates `noisecut-secrets` and the GHCR pull secret from GitHub secrets on every deploy (`--dry-run=client -o yaml | kubectl apply`). The admin password exists only as a PBKDF2 hash.
5. **Public surface = two NodePorts.**
   - 30530 → Api, and 30531 → Grafana under `/grafana`, both routed by the host's Cloudflare tunnel.
   - A default-deny NetworkPolicy plus "same namespace only" keeps Redpanda, PostgreSQL, Mailpit, Prometheus and every metrics port unreachable from other namespaces. Verified: a pod in `default` could reach the Api but not PostgreSQL or Redpanda.
6. **Prometheus uses Kubernetes service discovery**, limited to the namespace by a Role (not a ClusterRole), so `instance` = pod name and `service` = app label.
7. **Hardened containers.**
   - Chiseled (shell-less) .NET runtime images; every container runs non-root.
   - `readOnlyRootFilesystem` (with an `emptyDir` `/tmp`), all capabilities dropped, `RuntimeDefault` seccomp.
   - Every container has requests and limits. In total, about 2.1 GiB of memory is requested; this is what the scheduler reserves, and it is inside the SPEC §15 budget of about 3 GiB. The limits add up to about 3.6 GiB, a ceiling the pods never all reach at the same time.
8. **The API certificate doesn't list the Tailscale IP.** The CI kubeconfig sets `tls-server-name` to a name the certificate does list, instead of changing the k3s server's certificate.

## Alternatives considered
- **Helm**: templating power we don't need. Kustomize overlays are plain YAML and ship inside `kubectl`.
- **`kubectl apply --prune`**: a renamed object would silently delete the old one. Too sharp for a learning deployment.
- **An Ingress (Traefik) instead of NodePorts**: the host's existing tunnel already maps hostnames to localhost ports, which is how the other apps are exposed. NodePorts keep NoiseCut consistent with them.
- **Public GHCR packages**: no pull secret needed, but visibility must be set by hand per package. The pull secret matches the existing apps.

## Consequences
- Good: the exact deploy script was rehearsed end to end on the same k3s version, including least-privilege checks, network-policy checks and a pod kill.
- Cost: kustomize's load restrictor must be relaxed (`--load-restrictor=LoadRestrictionsNone`) so the base can reuse the Grafana JSON from `deploy/observability`.
- Cost: an overlay-generated ConfigMap referenced from the base can't carry kustomize's hash suffix (found in the rehearsal), so `noisecut-public` is unsuffixed.
