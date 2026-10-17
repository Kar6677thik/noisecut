# ADR-0010: Razor Pages dashboard, public read-only, admin login

- **Status:** Accepted
- **Date:** 2026-10-17
- **Milestone:** M0 (implemented M1 read-only, M4 auth)

## Context
The deployment is a public showcase (portfolio/résumé). Anonymous visitors must be able to *see* the system work,
but must not be able to change rules, inject scenarios, read emails or flood the system.

## Decision
- **UI:** server-rendered ASP.NET Core Razor Pages, plus about 100 lines of JS for the SSE live feed. No SPA toolchain.
- **Anonymous:** GET-only pages and APIs, rate-limited per IP.
- **Admin:** a single admin with a cookie login (password hash from a Secret), lockout, antiforgery, and an `X-Admin-Key` header for scripts.
- **Never public:** `/metrics` ports, Mailpit UI, Redpanda, PostgreSQL. The webhook sink requires an HMAC signature.
- **Grafana:** anonymous `Viewer` role, served under `/grafana`.

## Alternatives considered
- **Cloudflare Access in front of everything**: strongest, but it defeats the showcase goal.
- **React/Next SPA**: nicer UI, but a second toolchain that teaches nothing about streaming.

## Consequences
- Accepted risk: anonymous Grafana viewers can run arbitrary PromQL against our Prometheus. Prometheus holds no secrets or PII, and query timeouts and limits are configured.
- Accepted risk: NodePorts are also reachable on the LAN, bypassing Cloudflare. App-level auth still applies.
