# ADR-0009: Deploy to k3s via GHCR + Tailscale + a scoped ServiceAccount

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0 (implemented in M4)

## Context
- The home server runs a single-node **k3s** cluster (no Docker engine). Other apps already run there.
- Deployment must go through **GitHub Actions only**.
- The server is reachable from CI through Tailscale. Public HTTP goes through an existing Cloudflare Tunnel to NodePorts.

## Decision
1. CI builds one image per service, tagged with the **git SHA** (immutable), and pushes to **GHCR**.
2. The deploy job joins the tailnet with `tailscale/github-action` as `tag:ci`.
3. It talks to the k3s API directly using a kubeconfig for a **ServiceAccount whose RBAC is limited to the `noisecut` namespace**.
4. `kubectl apply -k deploy/k3s/overlays/home` with images pinned to the SHA → the migrator Job → `kubectl rollout status` on every Deployment. If the rollout fails, the job fails loudly, and the previous ReplicaSet keeps serving.

## Alternatives considered
- **SSH + a root-owned deploy script (`sudo -n`)**: works, but it needs sudoers changes and root on the server, and the deploy logic lives outside the repo.
- **Cluster-admin kubeconfig in a GitHub secret**: a leaked secret would compromise every app on the server. Rejected.
- **GitOps (Argo CD / Flux)**: excellent, but it is a whole new system. It fits better in Project 5 (progressive delivery).

## Consequences
- Good: least privilege, everything in the repo, and immutable, traceable releases (image tag = commit).
- One-time manual bootstrap (done once by the owner, documented in the M4 runbook): create the namespace, ServiceAccount and RBAC; allow `tag:ci → server:6443` in the Tailscale ACL; add the Cloudflare Tunnel route; add the GitHub secrets.
- Gotcha to verify in M4: the k3s API certificate may not list the Tailscale IP as a SAN. If so, set `tls-server-name` in the kubeconfig (or add `--tls-san`).
