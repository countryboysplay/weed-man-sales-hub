# Wave 6 summary — Owner security and production governance

Status: complete. 221 tests green (78 unit, 104 integration, 34
authorization, 5 end-to-end contract).

## What shipped

### Protected Owner verification core

- Master recovery credential: separate from the Owner password, at least 16
  characters, stored only as a PBKDF2 verifier (Identity's PasswordHasher —
  no custom password cryptography). Write-only: never displayed, returned,
  or logged after setup. Initial setup requires fresh auth; rotation
  requires the full protected flow.
- TOTP (RFC 6238, SHA-1, 6 digits, 30s step, ±1 step skew): secret
  generated server-side, stored encrypted with ASP.NET Core Data
  Protection, otpauth URI returned exactly once at setup, armed only after
  a valid confirmation code. Implementation is in-repo (~1 page) and unit
  tested against the RFC Appendix B vectors.
- `VerifyProtectedAsync` is the single gate: active Owner session + fresh
  auth + required reason + master verifier (+ TOTP when enabled).
  Brute-force throttle (5 failures → 5-minute lock); every failure and
  lockout lands in the permanent `owner_recovery_security_events` stream.

### Owner lifecycle

- Promotion/demotion/creation involving Owner runs ONLY through
  `POST /owner-security/owner-role` with full verification; the ordinary
  user-update path still refuses Owner transitions (defense in depth via
  a dedicated `SetRoleProtectedAsync` that bumps the security stamp).
- The last active Owner can never be demoted.

### Private communication inspection

- Owner-only, scope + reason + fresh auth + master/TOTP. The permanent
  `private_communication_access` record is written BEFORE any content is
  returned (docs/04 step 5); a short-lived access session (15 min) scopes
  reads to the approved conversations; each read writes child metadata.
- Current state only: deleted message bodies were erased at delete time
  and are not reconstructed — no hidden copies exist to return.

### Emergency access

- Owner-only, 1–60 minutes, chosen by the Owner. Other Owners get required
  notifications on start and end; another Owner can terminate with a
  mandatory reason. Permanent audit.

### Sensitive exports (EXP)

- Employee-history export: Manager/Owner + fresh auth (policy-enforced) +
  mandatory reason. Server-generated artifact (never client-composed):
  CSV, or PDF composed server-side and watermarked
  "CONFIDENTIAL — {requester} — {local time}" on every page. EXP record,
  7-year audit, and a child access audit for every download.

### Settings

- Typed settings rows with System (Owner + fresh auth) and Management
  scopes; a key can never be rewritten through the wrong scope's route.
  Every change is audited with before/after (Permanent for system scope,
  365 days for management scope).

### Production governance records

- Deployments (PROD), rollbacks (ROLL — protected flow, refused for
  versions on the blocked-rollback list), staging refreshes (STAGE —
  protected flow), report-only recovery (REC — protected flow; marks the
  archive entry Recovered with source/time and keeps the original),
  known-good and blocked version lists, and maintenance windows.

## Decisions and notes

- Actual backup execution (daily 12:30 AM CT pg_dump + encrypted Dropbox
  push) and the full-restore orchestration are Windows-deployment
  automation — Wave 7/deployment scope. Wave 6 delivers the governance and
  audit records those scripts write to (REC/PROD/STAGE/ROLL, maintenance
  windows), per docs/10's "launch/deployment records" framing.
- Emergency sessions currently record and notify; per-endpoint gating of
  "recovery-critical functions only" attaches as those functions arrive in
  deployment automation.
- The TOTP throttle is per-Owner-config and complements (not replaces)
  Identity's password lockout.

## Carried forward

- Wave 7: offline conflict payloads, version compliance, backup restore
  drills, performance/security tests, production automation (GitHub
  Actions on the self-hosted Windows runner), disaster recovery runbook.
- Standing: re-run the suite on PostgreSQL 17 before production deploy;
  generate VAPID keys at deploy; production Data Protection key ring path.
