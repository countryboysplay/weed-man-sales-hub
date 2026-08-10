# Wave 0 Implementation Assessment

Prepared before Wave 0 development, per the handoff instructions. Everything
here follows `CLAUDE.md` and `docs/`; where the documents leave an engineering
choice open, the choice made is recorded here.

## 1. Architecture

A **modular monolith** on ASP.NET Core 10 / .NET 10 LTS, one deployment unit,
structured in strict layers:

- **Api** — thin HTTP endpoints, SignalR hubs, middleware. No business logic.
- **Application** — one use-case class per business operation
  (`CreateSale`-style). Each use case authorizes, validates invariants,
  mutates state, writes audit, writes outbox rows, and commits — all in one
  database transaction. Realtime and push happen *after* commit, from the
  outbox, never inline.
- **Domain** — enums, value objects, invariants, record-ID semantics,
  business-date rules. No EF, no I/O.
- **Infrastructure** — EF Core/Npgsql, Identity stores, file blob store,
  Data Protection, outbox dispatcher, job leasing, adapters.
- **Contracts** — request/response/event DTOs shared with the future
  TypeScript client.
- **Workers** — hosted services (outbox publisher, scheduled jobs, stale
  idle-capability detection, retention). In-process with the API for now,
  separable later.

Module boundaries follow the 23 modules in `docs/00-architecture.md`; Wave 0
builds the cross-cutting foundation those modules stand on.

Cross-cutting choices:

- **PKs are UUIDv7** (`Guid.CreateVersion7()`): index-friendly like ULID,
  native to .NET 10 and PostgreSQL `uuid`. Human-readable record IDs
  (`NOTE-2026-00001`) are separate unique columns fed by
  `public_id_sequences` (per `docs/12`), never `COUNT(*)+1`.
- **Money is `decimal`**, mapped `numeric(12,2)`. No floating point anywhere
  near currency.
- **All timestamps UTC** (`timestamptz`); a `BusinessTimeService` owns every
  America/Chicago conversion (business date, year boundaries, business
  midnight) via IANA time-zone data — never a fixed UTC-6 offset. It sits on
  .NET's `TimeProvider` so tests can freeze time and cross DST boundaries.
- **Structured JSON logs** (Serilog) with correlation IDs; the deny-list in
  `docs/04` (passwords, cookies, TOTP, recovery credentials, message bodies)
  is enforced by never handing those values to the logger and by test-time
  log scraping in sensitive flows.

## 2. Solution structure

```text
WeedManSalesHub.sln
src/
  SalesHub.Api/              endpoints, hubs, middleware, composition root
  SalesHub.Application/      use cases, ports (interfaces), authorization handlers
  SalesHub.Domain/           entities, enums, value objects, business rules
  SalesHub.Infrastructure/   EF Core, Identity stores, adapters, dispatchers
  SalesHub.Contracts/        DTOs + event envelopes (API/client contract)
  SalesHub.Workers/          hosted services (referenced by Api host for now)
tests/
  SalesHub.UnitTests/        domain + application rules, no database
  SalesHub.IntegrationTests/ real PostgreSQL: EF, outbox, jobs, sessions
  SalesHub.AuthorizationTests/  endpoint × role × session-state matrix
  SalesHub.EndToEndContractTests/ HTTP+SignalR flows over the running host
```

Dependencies point inward only: Api → Application → Domain;
Infrastructure implements Application ports; Contracts referenced by Api,
Application, and tests. Controllers/endpoints never touch `DbContext`.

## 3. Wave 0 database foundation

Initial migration set (names final, snake_case):

| Table | Purpose |
|---|---|
| `users` + Identity tables | `ApplicationUser : IdentityUser<Guid>` with `display_name`, `is_active`, `created_at_utc`, deactivation fields; roles `SalesAgent`, `SalesSupervisor`, `SalesManager`, `Owner` seeded |
| `user_sessions` | server-side session per signed-in browser: token hash, created/last-seen, revocation (actor + reason), device/browser metadata, **idle capability state + verification/heartbeat timestamps**, fresh-auth expiry |
| `audit_events` | structured audit: category, action, actor, target, reason, before/after JSONB, session/device/correlation, `retention_class` |
| `outbox_messages` | transactional outbox per `db/schema-notes.sql`, plus `correlation_id`; partial index on unprocessed rows |
| `public_id_sequences` | `(prefix, year) → last_value`, allocated inside the caller's transaction with row locking |
| `scheduled_jobs` | persistent job definitions: key, type, cron, timezone, enabled, lease owner/expiry, next run UTC |
| `scheduled_job_runs` | one row per execution: attempt, outcome, error class, correlation |
| `file_blobs` | immutable blob metadata: sha256, content type, original name, byte length, storage key, scan status |
| `idempotency_keys` | replay protection for offline-queued mutations: key, operation, request hash, stored response, expiry |

Later waves add their module tables; nothing in Wave 0 blocks them.

## 4. Authentication design

- **Identity**: ASP.NET Core Identity over EF Core handles password hashing,
  lockout, security stamps, role membership. No custom crypto. No public
  registration endpoint exists at all — users are created by management (in
  Wave 0, by seed/dev-management endpoint per the wave gate).
- **Cookies**: Secure, HttpOnly, `SameSite=Lax`, same-origin app. No tokens
  in localStorage, ever. Security-stamp validation interval kept short
  (≤ 5 min) so password resets/role changes bite quickly.
- **Server-side sessions**: login creates a `user_sessions` row holding only
  a SHA-256 hash of a 256-bit random verifier; the session id + verifier ride
  in the cookie's claims. A **session gate** (middleware after
  authentication) rejects any authenticated request whose session row is
  missing, revoked, or expired — so revocation is immediate regardless of
  cookie lifetime. `last_seen_at_utc` updates are throttled to avoid a write
  per request.
- **Logout/revocation**: logout revokes the caller's session row.
  Force-logout endpoints (management → employees, Owner → anyone) revoke by
  session or by user, recording actor + reason. Revoking a session also
  voids its fresh-auth assertion.
- **Fresh authentication**: `POST /auth/fresh-auth` re-verifies the password
  and stamps `fresh_auth_until` (now + 15 min) on the *session row* —
  server-held, not a client token. The `FreshAuthRequired` policy reads it
  there.
- **Policies**: `Employee` (any active role), `Management`
  (Supervisor|Manager|Owner), `SupervisorOrAbove` (alias of Management),
  `ManagerOrOwner`, `OwnerOnly`, `FreshAuthRequired`, and
  `MonitoredWorkSession` (below). Resource-based authorization handlers come
  with their modules; the handler pattern is established in Wave 0 with
  session-ownership checks (`DELETE /auth/sessions/{id}`).
- **Throttling**: fixed-window rate limiter on `/auth/login` +
  `/auth/fresh-auth` by IP and by username, plus Identity lockout.

## 5. Idle Detection handshake

Mandatory, per `CLAUDE.md §4` and `docs/05`. Wave 0 builds the full
server-side framework; the browser side plugs into it in Wave 1.

1. Login succeeds → session's `idle_capability_state = Unknown`. For
   presence-monitored roles (configuration; default `SalesAgent`), every
   *working* endpoint group is behind the `MonitoredWorkSession` policy and
   returns `403` ProblemDetails `code: "idleCapabilityRequired"` while the
   state is not `Verified` — the login shell and remediation endpoints
   (`/auth/me`, capability verify, logout) remain reachable.
2. Frontend checks `window.IdleDetector`, requests permission from a user
   gesture, starts the detector (threshold ≥ 60 s), then calls
   `POST /auth/idle-capability/verify` with
   `{supported, permission, detectorStarted, thresholdSeconds, clientObservedAt}`.
3. Server ignores any client-supplied identity, binds the attestation to the
   authenticated session, records server receive time, and transitions the
   state machine: `Unknown → Verified` only when
   `supported && permission=="granted" && detectorStarted`; otherwise
   `Unsupported` / `PermissionDenied` / `Error` — all of which keep the
   working app blocked and are surfaced to the remediation screen.
4. Response carries the **capability lease**: expiry (default 5 min) and
   heartbeat cadence (default 60 s).
5. `POST /auth/idle-capability/heartbeat` slides the lease and records
   `userState`/`screenState`/visibility. The server derives presence — it
   never accepts client-claimed worked intervals.
6. A scheduled job marks sessions `Stale` when the lease lapses; `Stale`
   blocks working endpoints exactly like `Unknown`. Revoked permission
   reported by the client moves the state to `Revoked` immediately.
7. In-page mouse/keyboard activity may *supplement* later presence
   evaluation but can never substitute for a `Verified` capability — there is
   deliberately no code path that upgrades a session to `Verified` without a
   successful IdleDetector attestation.

States persisted per session: `Unknown, Unsupported, PermissionDenied,
Starting, Verified, Stale, Revoked, Error` (superset of docs/05, adding
`Revoked` distinct from `Error` as the list in docs/05 shows "Revoked/Error").

## 6. Realtime architecture (SignalR + outbox)

- Hubs: `/hubs/app` is stood up in Wave 0 (authenticated, session-gated);
  `/hubs/chat` and `/hubs/presence` are added with their modules. Group
  names follow `docs/03`: `user:{id}`, `role:{role}`, `management`,
  `owners` — groups are routing only; policies still guard every hub method
  and payload.
- **Nothing user-visible is published inline.** A use case writes canonical
  state + an `outbox_messages` row in the same transaction. The outbox
  dispatcher (Workers) claims batches with `FOR UPDATE SKIP LOCKED`, wraps
  the payload in the standard envelope
  `{eventId, eventType, occurredAt, correlationId, payload}`, delivers to
  SignalR groups (and, from Wave 1, notification/Web Push pipelines), then
  marks processed. Failures back off and retry; rows exceeding the attempt
  budget go to a failed state that a health check surfaces — they are never
  silently deleted. This is what makes "sale saved but dashboard never
  updated" impossible.
- Clients that miss events recover over HTTP: SignalR is a delivery channel,
  PostgreSQL is the source of truth.

## 7. Background jobs

- `scheduled_jobs` holds cron/recurrence + **business timezone**; next-run is
  computed in America/Chicago and stored as UTC, so a 12:30 AM CT backup
  stays 12:30 AM across DST transitions.
- The job worker claims due jobs **transactionally with a lease**
  (`lease_owner`, `lease_expires_at`, `SKIP LOCKED`). A crashed host simply
  lets the lease lapse; another (or restarted) worker re-claims. Executions
  are idempotent and every run writes a `scheduled_job_runs` row (attempt,
  outcome, error class, correlation).
- `next_run_at` advances only in the committed completion transaction, so a
  crash mid-run re-runs rather than skips.
- No `Task.Delay`-only scheduling for anything business-critical; the
  in-process timer merely polls the persistent tables.

## 8. Security middleware order

```csharp
app.UseForwardedHeaders();          // IIS/ANCM in front of Kestrel
app.UseExceptionHandler();          // -> ProblemDetails (RFC 7807 + extensions)
app.UseHsts();                      // production
app.UseHttpsRedirection();
app.UseSerilogRequestLogging();     // structured, correlation-aware
// correlation-ID middleware        // accept/mint X-Correlation-Id, push to log scope
app.UseRouting();
app.UseRateLimiter();               // login/global policies
app.UseAuthentication();            // Identity cookie
// session-gate middleware          // active server-side session required
// fresh-auth + idle-capability context // load session facts for policies
app.UseAuthorization();             // role/policy/resource
app.UseAntiforgery();               // state-changing endpoints
app.Map...();                       // endpoints, hubs, health
```

`/health/live` is anonymous and minimal; `/health/ready` (DB, outbox lag,
workers, disk) is management-protected per `docs/08`.

## 9. Contradictions found

All 23 written documents and all 28 reference GUI mockups were reviewed.
No contradiction blocks Wave 0, but the following need owner awareness. Per
your instruction, none of these were "resolved" by changing a product rule.

**Genuine conflicts needing a product decision (before the affected wave):**

1. **iPhone/Safari vs mandatory Idle Detection.** The onboarding mockup
   documents an iOS "Add to Home Screen" path, and the Profile → Devices
   mockup shows an active "iPhone · Safari PWA" session — but Safari does not
   implement the Idle Detection API, so under the mandatory rule that device
   can never enter the monitored working app. Either monitored roles are
   desktop/Chromium-only (and the iPhone session in the mockup belongs to a
   non-monitored role), or an exception path is needed. Decision needed
   before Wave 1's browser gate; Wave 0 builds the strict rule as written.
2. **"Add User" dialog offers Owner in the plain role dropdown** (user-admin
   mockup) with no reason/master-credential/TOTP fields, while the same
   screen's Owner Security tab — and `CLAUDE.md §19` — require the protected
   flow for Owner creation. The backend will *reject* Owner creation through
   the ordinary create-user path; the GUI should route it to the protected
   flow. Flagging because the mockup is permissive where the rule is not.
3. **Password-reset request recipients.** Login mockup copy says
   "Supervisors and Sales Managers will receive a password-reset request" —
   Owners are omitted. `CLAUDE.md` says management handles the flow.
   Assuming Owners should also see these requests; confirm.

**Scoping/naming differences (no product impact; choice recorded):**

4. **Idle Detection wave placement.** Your kickoff prompt puts the
   "mandatory Idle Detection capability verification framework" in Wave 0;
   `docs/10-implementation-waves.md` lists the capability handshake under
   **Wave 1**. I follow the prompt: the server-side framework, session state
   machine, verify/heartbeat endpoints and gating policy are built and tested
   in Wave 0; Wave 1 wires the real browser flow onto it. (The GUI master
   index's own wave list also places the "mandatory Idle Detection gate" in
   its Wave 0.)
5. **Job table naming.** `docs/01` says `scheduled_jobs`; `docs/06` says
   `schedule_jobs`. Standardized on `scheduled_jobs`.
6. **Wave 0 gate wording.** `docs/10` Wave 0 gate says "user can be created
   by seed/management dev endpoint" while full user management is Wave 1 —
   implemented as: seeding plus a management-only user-create endpoint
   sufficient to exercise auth, not the full lifecycle.

**Mockup gaps worth knowing (written rules stand; nothing to change):**

7. The **$5,000 second-confirmation** rule and the **3-pinned/7-day
   announcement** rules appear in no mockup (screens 1–3 were approved
   in-chat only). The backend implements the written rules; the future
   frontend must add the missing confirmation UI.
8. Mockups confirm details the docs imply: the **fresh-auth window is an
   Owner-configurable setting defaulting to 15 minutes**; forced logout must
   carry a **reason code** the client can render ("password reset, account
   security action, or administrative logout"); API errors need a
   **management-visible diagnostic/correlation ID** (`WM-8F31A7` style);
   sale deletion is a tombstone visible until business midnight, excluded
   from totals and duplicate checks immediately. All are reflected in the
   Wave 0 design above.

Environment notes (deviations of the *development container*, not of the
product):

- This cloud dev environment cannot reach the PostgreSQL 17 apt repository,
  so integration tests here run against **PostgreSQL 16.14**. Production
  remains PostgreSQL 17; Wave 0 uses no 17-only features. Re-run the suite
  against 17 before first production deploy.
- Development happens on Linux; the production target stays Windows Server
  2019 (IIS → Kestrel). .NET and Npgsql are identical across both;
  IIS/Hosting-Bundle specifics stay in `docs/08` until the deployment wave.
