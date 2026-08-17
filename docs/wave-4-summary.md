# Wave 4 summary — Workforce: presence, shifts, time off, breaks, technical, approvals

Status: complete. 185 tests green (68 unit, 78 integration, 34 authorization,
5 end-to-end contract). Two commits: part 1 (presence/shifts/exceptions),
part 2 (time off/breaks/technical/approvals).

## What shipped

### Presence (part 1)

- **Manual status** — Available / Busy / DND with a ≤35-character custom
  message, persistent until the user changes it. `POST /presence/status`.
- **DND behavior** — Web Push is suppressed for a recipient on DND (the
  durable notification row still lands); leaving DND delivers one catch-up
  summary with **counts per category only, never content previews**.
- **Derived directory** (`GET /presence/directory`) — Away/Offline are
  always server-derived: Offline = no active session seen within 5 minutes;
  Away = the session's last coarse IdleDetector transition says `idle` or
  `locked`. Precedence: Offline → OnBreak → DND → Busy → Away → Available.
  The client never asserts its own state.
- **Personal timeline** (`GET /presence/me`) — manual status, live derived
  state, today's normalized segments, own flags.
- **Presence evaluator** — minute-cadence job (`presence-evaluation`) over
  monitored roles (Sales Agents only, per the Owner's decision): writes
  presence segments, raises PRS-YYYY-##### flags per role rule set
  (defaults: late-start grace 10 min, offline grace 10 min, serious at
  20 min, break overrun grace 5 min):
  - **LateStart** — shift began, grace passed, no activity since shift start.
  - **Disappeared** — seen this shift, then offline/idle past grace;
    escalates to Serious and pushes a management notification.
  - **BreakOverrun** — active break past its limit plus grace; marks the
    break session too.
  Flags are one-per-user/category/business-date (DB unique index), so runs
  are idempotent; conditions that clear stamp `EndAtUtc` (Disappeared
  auto-resolves on return).
- **Suppression** — approved time off, a suspending schedule exception, or
  an explicit technical grace grant recolors would-be Away/Offline into
  ApprovedException/TechnicalGrace segments and blocks all flags. A
  technical report alone never pauses monitoring.
- **Rank-shaped alerts** (`GET /presence/alerts`) — Supervisors receive
  aggregate counts only; Managers/Owners get full detail and can
  resolve/suppress flags (`PATCH /presence/flags/{id}`), audited.

### Shifts and schedule exceptions (part 1)

- Shift templates hold America/Chicago **wall times** per weekday and are
  converted to UTC per date, so DST never shifts a 9 AM start. Overnight
  shifts are out of scope (validated).
- Assignments are date-ranged per user; `GET /shifts/mine` shows the
  employee's own week.
- Schedule exceptions (SCH-YYYY-#####) support replacement windows or
  whole-day scope, optional presence suspension, and required
  acknowledgment (delivered as a required notification; `POST
  /schedule-exceptions/{id}/acknowledge`, 404 for anyone but the target).

### Time off (part 2)

- Requests (TO-YYYY-#####): full-day ranges or single-day partial windows;
  overlap-refused; types seeded (Vacation, Personal, Unpaid).
- **Coverage gate on approval** per role rule: Warn / WarnAndConfirm
  (409 `coverageConfirmationRequired` until the manager retries with
  `confirmCoverage: true`) / Block (409 `coverageBlocked`). The computed
  per-day numbers freeze into `CoverageSnapshotJson` at approval. Seeded
  rule: minimum 0 (inert) with WarnAndConfirm, until management sets a floor.
- Denial requires a reason, shown to the employee. Decisions notify the
  requester and emit `timeoff.decided.v1` + `approvals.queueChanged.v1`.
- Canceling **approved** time off opens a cancellation request that
  management decides; pending requests cancel instantly.

### Breaks (part 2)

- One active break per user (service check + partial unique index).
- Same-business-day self-service corrections (BRK-YYYY-#####) that always
  preserve the original window on the correction record; approval applies
  the corrected times and clears the overrun flag for re-derivation.
- Past days are management-only edits with a mandatory, audited reason.

### Technical reports and grace (part 2)

- Reports (TECH-YYYY-#####) notify management; **only** an explicit grant
  (bound to the report and its reporter) pauses monitoring, for its window.

### Unified approvals queue (part 2)

- `GET /approvals` (management): pending time off, cancellation requests,
  break corrections, plus the open password-reset count, with a combined
  total for the badge. `approvals.queueChanged.v1` fires on every mutation.

## Decisions and notes

- **Idle heartbeats now persist the coarse transitions** (`active|idle`,
  `locked|unlocked`) on the session row — transitions only, never raw
  input, per docs/05. These drive Away derivation.
- `presence.statusChanged.v1` broadcasts to everyone (directory); flag
  events carry `subjectUserId` so the router sends them to management, not
  to the flagged agent.
- Coverage counts agents *scheduled that weekday* via active assignments;
  agents with no shift assignment don't count toward or against coverage.
- Disappeared detection measures offline gaps from last-seen and idle gaps
  from the segment boundary, so an idle-but-connected agent is still caught.
- One BreakOverrun flag per day per user (unique index trade-off);
  subsequent overruns the same day extend the existing record.
- Evaluator suppression covers "now"; segments recolor only would-be
  violations, so someone working during a grace window still shows working.

## Carried forward

- Wave 5: support tickets, management records, search, reports, sync health.
- Wave 6: Owner security (protected flows, master credential + TOTP,
  private-communication access, backups/staging, exports).
- Presence rule sets and coverage rules get their management UI in the
  settings wave; today they're seeded rows tuned via SQL.
- Standing: re-run the suite on PostgreSQL 17 before production deploy
  (dev container runs 16 — network policy blocks the PG17 apt repo).
