# CLAUDE.md — Weed Man Sales Hub

You are implementing the backend for a production employee/sales PWA.

This file is the highest-priority project instruction set after explicit human instructions.

## 1. Required architecture

Use:

- .NET 10 LTS / ASP.NET Core 10
- PostgreSQL 17 on Windows Server 2019
- Entity Framework Core migrations
- ASP.NET Core Identity for users/password hashing/lockout/security stamps
- cookie-based same-origin browser authentication
- antiforgery protection for state-changing HTTP requests
- SignalR for realtime chat, presence, dashboards, acknowledgments, queue updates, support, and celebrations
- Web Push + service-worker delivery for PWA push notifications
- IIS in front of Kestrel on Windows Server 2019
- persistent database-backed scheduled-job state
- transactional outbox for durable notifications/realtime events
- immutable uploaded-file blobs outside the deployment directory
- structured JSON logs with correlation IDs

Avoid unnecessary microservices. Start as a modular monolith with strongly separated application modules.

## 2. Roles

Exactly these application roles:

1. SalesAgent
2. SalesSupervisor
3. SalesManager
4. Owner

“Management” normally means Supervisor + Manager + Owner unless a rule below narrows it.

### SalesSupervisor

May:
- add/remove/deactivate/reactivate users except protected Owner role actions
- reset employee passwords
- force logout employee sessions
- manage sales dashboard content/settings allowed to management
- announcements and recognitions
- create/manage group chats
- manage documents/resources
- tasks/forms/folders/badges
- manager settings explicitly delegated to management
- approve time off, schedule exceptions, break corrections, technical grace
- normal support queue
- serious presence-alert summary

May not:
- inspect private communication history
- view detailed lifetime presence history reserved for Manager/Owner
- perform sensitive employee-history exports reserved for Manager/Owner
- restore backups
- refresh staging from production
- use emergency Owner access

### SalesManager

Everything Supervisor can do, plus:
- detailed presence history
- sensitive employee-history exports
- advanced support diagnostics
- broader management record/report functions

Still may not:
- inspect private communications as a nonparticipant
- perform Owner-only recovery/security/deployment actions

### Owner

Full application administration, including:
- protected private communication inspection
- backup restore
- staging refresh
- emergency access
- feature/deployment/security controls
- protected Owner creation/removal/demotion workflows

Multiple Owners are allowed.

## 3. Authentication and sessions

- No public self-registration.
- Management creates accounts and assigns username/password.
- No forced password change on first login unless later enabled by explicit product decision.
- Passwords are hashed using ASP.NET Core Identity; never reversible-encrypted.
- Use secure, HttpOnly cookies over HTTPS.
- Same-origin frontend and API is preferred.
- Maintain a server-side `user_sessions` record for each signed-in browser/device.
- Every authenticated request must be tied to an active server-side session.
- Force logout revokes sessions immediately.
- Supervisor/Manager can force logout employee sessions.
- Owner can force logout any session.
- Login throttling and Identity lockout are required.
- “Forgot Password” creates a management-visible request; it does not send a self-service password-reset email by default.
- Sensitive actions require fresh authentication; default fresh-auth window is 15 minutes.

## 4. Mandatory Idle Detection

Idle Detection is NOT optional.

For roles whose work session is subject to presence monitoring:

- The frontend must verify `IdleDetector` browser support.
- The permission must be explicitly granted.
- The app must be running in a secure top-level context.
- If unsupported, denied, revoked, or initialization fails, the working application is blocked.
- The user receives a clear compatibility/remediation screen.
- Browser event/activity signals may supplement presence data but may not replace Idle Detection.
- Presence API endpoints must reject a client claiming an active work session if the required capability handshake is absent/expired.
- Store capability attestations and heartbeat timestamps per session.

Do not fake operating-system idle state by relying only on mouse/keyboard events inside the webpage.

## 5. Timezone

Business timezone: `America/Chicago`.

- Store timestamps in UTC in the database.
- Convert/display operational business times as America/Chicago.
- Never persist “CST” as a fixed UTC-6 assumption because Central Time observes daylight saving time.
- Record exact UTC timestamp plus actor/session/device metadata for audit events.

## 6. Sales rules

### Add Sale

Fields:
- CID: numeric string, required
- SaleType: Program | Upsell
- Campaign:
  - Program normally AS01
  - Upsell GC01 | AE01 | OS01
- Amount: decimal currency > 0
- created_by_user_id
- created_at_utc

Behavior:
- Submit by explicit button, not Enter-key implicit submission.
- Closing Add Sale discards unsaved data without warning.
- Successful submit closes immediately and broadcasts realtime updates.
- Amount > $5,000 requires a second confirmation containing the sale details.
- Each user can create only their own sales.
- Timestamp is server-authoritative.
- Use decimal/numeric; never floating point for money.

### Duplicate rules

Current calendar year:
- Program: CID must be unique among nondeleted Program sales unless the user explicitly confirms the prior sale was canceled and this is a resale.
- Upsell: CID + Campaign must be unique among nondeleted Upsell sales.
- Deleted sales are ignored for duplicate checks.
- Duplicate rule resets January 1 based on America/Chicago business date.

### Same-day employee editing

A seller may edit/delete their own sale only while the sale’s business date is today in America/Chicago.

Delete:
- requires confirmation with sale details
- no reason
- no undo
- remove it immediately from team/personal totals
- may remain visually marked Deleted until midnight in the current-day UI
- must not be restorable
- must not block a replacement duplicate
- deleted sale must not appear in active sales history after the daily boundary

Historical correction:
- Supervisor/Manager/Owner only
- reason required
- audit old values/new values/deletion, actor, time

### Sales aggregates

Team Today:
- realtime
- all active Sales Agents appear even at zero
- management appears only if they have a sale
- sort net sales descending, then count
- top 3 medals reset at business midnight
- row drilldown shows category count/net only; no CID/time

My Sales:
- monthly and YTD count/net by category
- current month and YTD or month-by-month numeric views
- authorized managers can view another agent’s equivalent view
- user export of own current-year final corrected rows to CSV

## 7. Chat rules

- Everyone can DM anyone.
- Management creates groups.
- Employees cannot create groups.
- Users cannot leave groups.
- Normal groups may be muted after being read.
- Mandatory groups cannot be muted.
- `@mention` overrides group mute unless DND is active.
- `@everyone` is management announcement behavior, not arbitrary employee chat behavior.
- Typing indicators and read receipts are realtime.
- Users may edit/delete their own messages without a time limit.
- Edited messages show “Edited.”
- Deleted message content is removed from current state.
- DO NOT retain deleted message content in a secret history table.
- Owner private-communication inspection exposes only current retained communication state.
- Private communication access is Owner-only, requires protected access flow, and creates a permanent access audit.

## 8. Announcements

- Supervisor/Manager/Owner can publish.
- Can target all users or selected users.
- May require acknowledgment.
- Track Seen separately from Acknowledged.
- Up to 3 pinned announcements.
- Auto-unpin after 7 days; announcement remains active.
- High Priority remains highlighted until all targeted nonmanagement users meet the configured seen/ack requirement.
- Managers are excluded from completion percentage.
- At 100%, notify management with title and exact Central Time.
- View By / Acknowledge By deadlines and auto reminders are configurable.
- Reminder push targets only outstanding users.

## 9. Tasks

- Management creates/assigns.
- One/multiple/everyone.
- Each assignee receives an independent task instance.
- Employee completion removes it from their active list.
- Completion history remains management-visible.
- Due date, priority, comments, attachments, recurrence.
- Recurrence produces new instances.
- `@mentions` work in comments.
- Overdue reminder behavior is configurable per task.

## 10. Forms

Native form builder:
- management-authored
- draft or published
- published forms visible to all active users
- supported field types: single-line, number, dropdown, yes/no toggle
- required flags
- conditional branching
- sections/pages
- drag/drop reorder
- duplicate question/section
- published edits take effect immediately
- open forms are refreshed; matching answers preserved; incompatible/removed answers cleared

Google Form link:
- custom display name
- opens fullscreen PWA wrapper if embeddable
- Close control only
- fallback external open if embedding blocked
- app does not track Google submission status
- Google links cannot be duplicated as native forms
- all published forms remain visible to all active users

Native Email Request workflow:
- CID
- customer email
- quote type
- lawn area/coverage
- optional Open -> Completed tracking
- management marks complete
- submitter notified
- completed request disappears and is not archived

## 11. Resources

- nested folders
- files: PDF, XLSX, DOCX, PPTX, images
- links: video and websites
- management upload/replace/delete/reorder
- employee read only
- agents cannot download
- managers may download supported office/PDF files
- PDF employee viewer is watermarked and no-download
- manager PDF download is watermarked with manager and date
- office-file manager download is unchanged
- secure authenticated access only
- personal favorites
- fuzzy search over metadata and indexed supported content
- remember folder/location/sort/view per user
- sensitive staging exclusion placeholder flag
- manager resource-download audit visible to Owner for 365 days

## 12. Presence, shifts, time off, breaks

Presence statuses:
- Available
- Busy
- DND
- Away
- On Break
- Offline / device disconnected states as operational signals

DND:
- manually persistent until user changes it
- suppresses all notifications
- management can override another user’s DND per current product requirement
- no reason/audit for the override unless changed later
- when DND ends, show catch-up summary of active missed categories without content previews

Presence:
- assigned shifts define expected work windows
- role/shift-configurable grace thresholds
- meaningful issues reach management dashboard
- serious issues push management
- detailed history Manager/Owner; Supervisor serious summary only
- employee sees current-day timeline and durations
- approved exceptions/time off/technical grace suspend or alter relevant flags

Time off:
- full or partial date/time
- configurable request types
- optional employee reason
- any management role may approve
- denial reason required
- approval note optional
- employee receives push + in-app result
- employee may cancel pending
- canceling approved time off creates a cancellation request requiring management approval
- coverage thresholds can warn/confirm/block

Breaks:
- employee manually starts/ends Lunch/Break/Other
- one active break
- presence alerts pause while on break
- limits configurable
- employee same-day correction request allowed before midnight
- correction approval recalculates flags but preserves original audit
- after midnight, management correction requires reason and before/after audit
- missed-break request/addition supported

Technical:
- employee reports Internet/Computer/Browser-PWA/Other
- report does not automatically grant presence grace
- management explicitly grants technical grace with start/end/reason/report link
- original flags remain linked/audited

## 13. Employee records and management notes

Management note categories:
- Attendance
- Coaching
- Technical
- Other

Rules:
- append-only primary note and follow-up model
- author/time/category
- text only
- Normal/High priority
- High may auto-pin and notify management
- management can require acknowledgments from selected managers
- Open/Resolved
- reopen requires reason and preserves prior resolution
- links to PRS/BRK/TECH/TO/SCH records
- managers can link/unlink existing records; unlink reason required
- management tags use one configurable shared library

Sensitive history export:
- Manager/Owner
- PDF/CSV
- reason required
- fresh auth
- shared `EXP-YYYY-#####`
- PDF confidential watermark with manager/time
- export audit retained 7 years
- re-download creates child access audit

## 14. Support

- employee can report problem with description
- automatically capture app version, browser, device, time, current page, correlation context
- optional screenshot/attachment
- statuses: Open, InProgress, WaitingOnUser, Resolved, Closed
- employee can confirm closure after Resolved
- management can force close
- internal management notes separate from employee-visible replies
- assignee or primary + collaborators
- Low/Normal/High/Critical
- Critical highlights dashboard and pushes all management
- system may suggest priority but management can override
- full chronology
- closed tickets searchable for life of employee record unless hard delete rules apply
- similar ticket detection
- links to TECH/sync/presence/deployment/reference IDs

## 15. Notifications

Use durable notification records plus delivery records.

- Notification Center is source of truth for in-app notification state.
- Web Push is a delivery channel, not the source of truth.
- DND suppresses pushes.
- Required/protected notification remains in Notification Center until retention/ack rules allow removal.
- user preferences only apply to optional categories
- required notification always contributes to app badge
- tap opens exact referenced object
- optional snooze
- grouped inbox and Mark All Read

## 16. Offline and sync

- selected safe data may be cached read-only
- selected actions may be queued as Pending Sync
- sensitive actions are blocked offline
- use idempotency keys for queued mutations
- server resolves safe merges automatically
- unsafe conflicts return structured field-level conflict payload
- client may retry or discard with explicit loss confirmation
- Sync Health management view shows per user/device/browser pending/failures/severity
- remote resync/refresh/safe-cache-clear commands are audited for 90 days

## 17. Hard delete

Hard deletion is an explicit destructive lifecycle action.

It removes user-owned operational data:
- account/profile
- employee-owned sales
- messages
- native form submissions
- task assignments/comments
- recognitions/comments
- attachments
- presence history
- employee management records
- other employee-owned operational history

Then recalculate live aggregates where needed.

Exceptions:
- global/permanent Owner security audit records may persist when required for security/recovery governance.
- private communication access audit persists, but deleted message content must not be retained merely for that audit.

## 18. Record IDs

Human-readable record IDs use yearly sequences:

- REC-YYYY-#####
- NOTE-YYYY-#####
- TECH-YYYY-#####
- TO-YYYY-#####
- BRK-YYYY-#####
- SCH-YYYY-#####
- PRS-YYYY-#####
- SUP-YYYY-#####
- PROD-YYYY-#####
- STAGE-YYYY-#####
- ROLL-YYYY-#####
- EXP-YYYY-#####

Yearly sequence resets January 1 in America/Chicago.
Internal database PKs should still be UUID/ULID.
Reopened records retain their original public record ID.

## 19. Owner security

Protected Owner actions require:
- current Owner session
- fresh authentication
- required reason
- master recovery credential where specified
- TOTP where specified
- permanent or long-retention security audit

Master recovery credential:
- write-only
- never displayed after setup
- stored only as strong hash/verifier
- TOTP secret encrypted at rest with ASP.NET Core Data Protection
- owner creation/removal/demotion/recovery requires protected flow

Emergency access:
- Owner-only
- recovery-critical functions only
- maximum 60 minutes
- Owner chooses duration
- other Owners notified when started/ended
- another Owner may terminate session with reason
- permanent audit
- safe completion of an in-flight critical transaction if emergency session expires

## 20. Backups and staging

Production backup:
- daily 12:30 AM America/Chicago
- keep 7 daily backups
- encrypted off-server Dropbox destination
- database + uploaded storage consistent snapshot
- separate server recovery/encryption key
- full production restore uses latest verified backup only
- Owner + fresh auth + master credential + TOTP + reason
- restore enters maintenance and creates a transactional rollback point
- run health checks before reopening production

Report-only recovery:
- may inspect retained older backups
- recovers only selected report artifacts
- creates `REC-YYYY-#####`
- original report preserved
- recovered artifact marked Recovered with source/time/Owner
- Owner recovery audit

Staging:
- management only
- separate DB/storage
- realistic production-like data
- exclude private DM/group chat content
- exclude passwords/secrets
- sensitive docs replaced with placeholders
- Owner refresh from production requires fresh auth/master/reason/audit

## 21. Deployment

Windows Server 2019:
- IIS -> Kestrel ASP.NET Core application
- WebSocket Protocol IIS feature enabled
- PostgreSQL 17 as separate Windows service
- production uploaded storage outside deploy directory
- ASP.NET Data Protection key ring persisted outside deploy directory
- HTTPS only

Git:
- private GitHub repository
- Development -> Staging -> Test -> main -> Production
- self-hosted Windows GitHub Actions runner
- atomic deployment to a new version directory
- run migration preflight and smoke tests
- switch current pointer only after readiness passes
- failed deployment automatically keeps/returns prior known-good version
- never overwrite production in place

## 22. Implementation quality rules

- Modular monolith.
- Domain logic must not live in controllers.
- Thin API endpoints.
- Application services/use-cases enforce business rules.
- EF Core configurations separate from domain entities.
- No floating-point currency.
- UTC persistence + America/Chicago business-date service.
- Idempotency on mutation endpoints used by offline queue.
- Optimistic concurrency token on mutable business records.
- Transactional outbox for events/notifications.
- Correlation ID on each request.
- Health endpoints for liveness/readiness/dependencies.
- Comprehensive automated tests for permission and business-rule boundaries.
