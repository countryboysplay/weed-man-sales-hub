# Wave 5 summary — Management: records, support, search, reports, sync health

Status: complete. 202 tests green (68 unit, 95 integration, 34 authorization,
5 end-to-end contract). Three commits: records, support, system ops.

## What shipped

### Employee management records (part 1)

- Management notes (NOTE-YYYY-#####): categories Attendance / Coaching /
  Technical / Other, Normal/High priority, text only, append-only. High
  auto-pins and notifies the rest of management.
- Follow-up chronology is append-only; resolutions and reopens are
  follow-up entries too, so **reopening (mandatory reason) preserves the
  prior resolution** and the original NOTE id.
- Selected managers can be required to acknowledge (required
  notifications; only targets can ack).
- Validated record links to PRS/BRK/TECH/TO/SCH/SUP/NOTE; **unlinking
  keeps the row** with removed-by/at and a mandatory reason.
- One shared management tag library (unique labels) applied to public-id
  records.
- `GET /employees/{id}/management-record` returns notes plus the
  employee's recent related records (flags, time off, corrections,
  technical reports, tickets). Everything is management-only; audit
  retention is AccountLifetime.

### Support (part 2)

- Tickets (SUP-YYYY-#####): context (app version, browser family, device,
  correlation id, page) captured **server-side** from the session at
  creation; optional attachment by blob reference.
- Suggested priority from description keywords (kept on the row even
  after management overrides); Critical notifies all management, on
  creation or on escalation.
- Visibility: `InternalNote` vs `EmployeeReply` — reporters never receive
  internal notes, and their detail view omits diagnostics, which are
  Manager/Owner only (permission matrix). Supervisors get the normal
  queue without diagnostics.
- Lifecycle: Open → InProgress (assignment or reporter reply) →
  WaitingOnUser (management employee-visible reply) → Resolved →
  Closed by reporter confirmation or management force close; reopen keeps
  the SUP id. Similar recent tickets (same issue type, 14 days) surface
  at creation.
- Collaborators, links to other public records, queue filters by
  status/priority.

### Search, sync health, reports/archive (part 3)

- `GET /search?q=` — permission predicates run BEFORE matching: people
  (everyone), announcements/resources, sales by CID (own vs management),
  support (own vs management), management notes (management only, and any
  search returning note hits writes a server-side audit event the client
  cannot opt out of). Exact public-id lookup jumps straight to the
  record; every hit carries `whyMatched`.
- Sync: clients report accepted/rejected queued operations
  (`POST /sync/actions`); `GET /sync/health` aggregates 7 days per
  user+device with severity (OK / Warning / High at ≥5 failures).
  Remote device commands (Resync / Refresh / ClearSafeCache) are
  management-issued, delivered via outbox → target user's SignalR group,
  fetchable at `/sync/commands/pending`, acknowledged by the device, and
  audited (90-day class).
- `GET /system/health` (management): outbox pending/failed, scheduled-job
  last/next runs, stale-job detection.
- Reports: schedules (SalesSummary / PresenceAttendanceSummary /
  SupportTrends × Daily / Weekly / Monthly at a business-time hour) run
  via the `report-runner` job (every 5 minutes, lease-based like all
  jobs); periods are completed business-date ranges. Artifacts are CSVs
  in immutable blob storage, browsable in the Archive Center; downloads
  are audited. Failed runs record the error and notify management
  (Report Failures panel). On-demand runs via `POST /reports/run`.

## Decisions and notes

- Similar-ticket detection is same-issue-type-within-14-days — surfaced,
  never auto-merged. A trigram/text-search upgrade is a Wave 7 candidate.
- Search uses `LOWER(...) LIKE` (provider-neutral, translated by EF);
  docs/01's `search_document` full-text state is deferred until real
  volume warrants tsvector columns — flagged, not silently dropped.
- The natural-language `/search/interpret` endpoint (docs/02 "may be") is
  deferred; the structured search covers the acceptance path.
- Sync pending counts come from client reports of queued operations;
  the server records accepted/rejected outcomes per docs/01 sync_actions
  ("only if server needs explicit queue visibility").
- Recovery records (REC), sensitive exports (EXP), and archive recovery
  marking belong to Wave 6's protected Owner flows; `ArchiveEntry` already
  carries the Recovered fields they will set.

## Carried forward

- Wave 6: Owner security — protected flows (fresh auth + master
  credential + TOTP + reason), private-communication access, emergency
  access, sensitive exports (EXP) incl. employee-history export with
  watermarked PDF, backups/recovery (REC), staging refresh, deployment/
  rollback governance records, settings audit.
- Standing: re-run the suite on PostgreSQL 17 before production deploy.
