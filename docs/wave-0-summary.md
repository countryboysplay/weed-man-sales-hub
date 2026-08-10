# Wave 0 — Completion Summary

Status: **complete**. All 82 automated tests pass (3 consecutive full-suite
runs). Wave 1 has not been started, per the handoff instructions.

## 1. What was built

The technical foundation every later module stands on:

- .NET 10 solution in the docs/00 modular-monolith layout
- PostgreSQL via EF Core with the `InitialWaveZero` migration (snake_case
  schema; Identity tables renamed to `users`, `roles`, … per docs/01)
- ASP.NET Core Identity (hashing, lockout, security stamps, roles seeded)
- Cookie authentication with a **server-side session gate**: every
  authenticated request must match an active `user_sessions` row (verifier
  hash check); revocation is immediate regardless of cookie lifetime
- Fresh authentication (server-held assertion, 15-minute default window)
- **Mandatory Idle Detection capability framework**: per-session state
  machine (`Unknown … Verified … Stale/Revoked`), verify + heartbeat
  endpoints, capability lease, stale-scan job, and the
  `MonitoredWorkSession` policy returning `403 idleCapabilityRequired`
- Role policies (`Employee`, `Management`, `ManagerOrOwner`, `OwnerOnly`),
  with Owner-session revocation restricted to Owners and Owner *creation*
  refused outside the (Wave 6) protected workflow
- RFC 7807 ProblemDetails everywhere, with `code` + `correlationId`
  extensions; correlation-id middleware end to end (request → logs → audit)
- Antiforgery (X-CSRF-TOKEN header pattern) on all state-changing API calls
- Rate limiting on credential endpoints + Identity lockout
- America/Chicago `BusinessTime` service on `TimeProvider` (DST-proof)
- `public_id_sequences` + generator (`NOTE-2026-00001`, row-locked upsert)
- Structured audit writer with retention classes
- **Transactional outbox** + dispatcher (SKIP LOCKED claims, exponential
  backoff, poison rows parked as failed and surfaced in health)
- **Persistent scheduled jobs** (cron in business timezone, lease-based
  claiming, per-run history) with two live jobs: idle-capability stale scan
  and idempotency-key cleanup
- Immutable file-blob store (SHA-256, server-generated keys, config-driven
  root) and `idempotency_keys` infrastructure
- SignalR `/hubs/app` (authenticated; `user:`, `role:`, `management`,
  `owners` groups) fed exclusively by the outbox
- Health endpoints: anonymous `/health/live`; management-only
  `/health/ready` (DB + outbox lag/poison)
- Serilog structured JSON logging; secrets never logged
- Startup seeding: roles, jobs, and a config-driven initial Owner

## 2. Solution structure

```
src/  Api · Application · Domain · Infrastructure · Contracts · Workers
tests/ UnitTests · IntegrationTests · AuthorizationTests ·
       EndToEndContractTests · TestSupport
```

## 3. Migrations

- `20260810…_InitialWaveZero` — the entire Wave 0 schema.

## 4. Tables

`users`, `roles`, `user_roles`, `user_claims`, `role_claims`, `user_logins`,
`user_tokens`, `user_sessions`, `audit_events`, `outbox_messages`,
`public_id_sequences`, `scheduled_jobs`, `scheduled_job_runs`, `file_blobs`,
`idempotency_keys`.

## 5–8. Architecture details

See `docs/wave-0-assessment.md` §4–§8 — implemented as designed, with these
deltas discovered during implementation:

- Connection string resolves lazily at DbContext construction (registration-
  time reads capture stale config under minimal hosting).
- Cron next-run instants are normalized to UTC (Cronos returns zone-local
  offsets, and Npgsql — correctly — refuses non-UTC `timestamptz` writes).
- Two gate-probe endpoints exist for the client and the test matrix:
  `GET /api/v1/diagnostics/monitored-ping` (idle gate) and
  `POST /api/v1/diagnostics/fresh-auth-ping` (fresh-auth gate).

## 9–10. Tests and results

82 tests, all passing (three consecutive full-suite runs):

| Suite | Count | Covers |
|---|---|---|
| UnitTests | 38 | business time across both 2026 DST transitions, record IDs, session tokens, roles, idle states, backup cron |
| IntegrationTests (real PostgreSQL) | 16 | migrations, login/lockout/logout/revocation + audit + outbox rows, fresh auth, antiforgery, outbox exactly-once/retry/poison, concurrent public-id allocation, job lease claim/respect/crash-recovery, idempotency uniqueness |
| AuthorizationTests | 24 | anonymous + all four roles × endpoints, Owner-session protection, Owner-creation refusal, fresh-auth gate, idle gate incl. all failed attestations |
| EndToEndContractTests | 4 | agent handshake → Verified → stale → blocked; revocation delivered over SignalR via outbox; ProblemDetails contract; anonymous hub refused |

Each integration/authorization/e2e class boots the real API against its own
freshly-migrated PostgreSQL database (no in-memory substitute, per docs/09).

## 11. Warnings / technical debt

- One full-suite run showed a single non-reproducible integration failure
  under maximum parallel load (all four suites at once); three subsequent
  full runs were clean. Watch in CI; likely local PG contention.
- The outbox event router covers `user:{id}` and `management` targets; the
  `conversation:`/`branch:` targets arrive with their modules.
- `SessionGateMiddleware` reads the session row per request (one indexed
  PK lookup). If profiling ever shows pressure, add a short-TTL cache keyed
  on session id + revocation stamp.
- Serilog request logs go to console JSON; rolling file sinks and the
  Windows log directory layout belong to the deployment wave.
- Password policy is length-based (10+). The management-assigned password
  workflow may want a generator/strength meter in the GUI wave.

## 12. Environment / server prerequisites identified

- **Dev container runs PostgreSQL 16.14** (the network policy here blocks
  the PostgreSQL 17 repo). No 17-only features are used; re-run the suite
  against 17 before first production deploy. Production remains
  PostgreSQL 17 on Windows Server 2019 per docs/08.
- Windows Server will need the .NET 10 Hosting Bundle, IIS WebSocket
  feature, an ICU-enabled .NET (default) for IANA time zones, a
  Data Protection key directory and file-storage root outside the deploy
  path — all already listed in docs/08; nothing new discovered.

## 13. Decisions wanted before Wave 1

1. ~~**iPhone/Safari vs mandatory Idle Detection**~~ — **RESOLVED
   2026-08-10**: the application is desktop-only for all roles; mobile
   support is cut. See `ADR-002-desktop-only.md`.
2. ~~**Password-reset request visibility**~~ — **RESOLVED 2026-08-10**:
   all management roles including Owners see password-reset requests.
3. ~~**Monitored roles configuration**~~ — **RESOLVED 2026-08-10**:
   Sales Agents only, exactly the shipped default.

## 14. Git history (Wave 0)

```
a409b1d Wave 0 test suites: 82 tests across unit, integration, authz, e2e
0b16433 Wave 0 foundation: auth, sessions, idle gate, outbox, jobs, audit
82a9bb0 Scaffold the .NET 10 solution: six src projects, four test projects
5763799 Wave 0 implementation assessment
fe111f7 Import the approved backend handoff package
```
