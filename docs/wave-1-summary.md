# Wave 1 — Completion Summary

Status: **complete**. Full suite: **95 tests, all passing** (38 unit,
23 integration, 30 authorization, 4 end-to-end). Wave 2 (Sales) not started.

## Built

**User lifecycle (management surface, user-admin mockup):**
`GET/PATCH /users`, `GET /users/{id}`, deactivate (optional reason +
scheduled reactivation), reactivate, schedule/cancel reactivation,
management password reset, force logout. Deactivation and password reset
revoke every session with the matching `SessionRevocationReason`, so the
browser shows the right access state ("Account deactivated" / "Session
ended — password reset"). Owner accounts can only be managed by Owners;
role changes involving Owner are refused everywhere until Wave 6's
protected workflow.

**Scheduled reactivation job** (`scheduled-reactivation`, every minute):
one-hour advance notice to management (fires exactly once via a marker
column), reactivation at the scheduled instant, notifications to the user
and management. Idempotent across crashes/lease recovery.

**Password-reset queue** (login-states mockup): anonymous
`POST /auth/forgot-password-request` answers 202 identically for unknown
usernames, records the request, and notifies **all management roles
including Owners** (decision 2026-08-10). Management lists open requests,
completes one by assigning the replacement password (runs through the
lifecycle reset → sessions revoked), or dismisses unmatched ones.

**Profile self-service** (profile mockup, mixed control): phone + birthday
+ photo are user-editable (photo = immutable blob, JPEG/PNG/WebP ≤ 5 MB);
name/role/branch/email stay manager-maintained. Self password change
verifies the current password. `GET /directory` for every active user —
deliberately without sales metrics. `branches` table + endpoints
(management create; Wave 1 keeps it minimal).

**Notification Center foundation** (CLAUDE.md §15): `notifications` rows
are the source of truth; list/read/mark-all/acknowledge/snooze/delete are
own-resource scoped; required notifications refuse deletion until
acknowledged and drive `requiredOutstandingCount` for the badge. Web Push:
`push_subscriptions` CRUD (rebind on duplicate endpoint), VAPID sender as
an **outbox side effect** — minimal lock-screen-safe payload, per-
subscription `notification_deliveries` outcomes, dead subscriptions
deactivated on 404/410, clean no-op when VAPID keys are unconfigured.
DND suppression joins in Wave 4 with the presence module.

## Migration

`WaveOneUserLifecycleAndNotifications`: `branches`,
`password_reset_requests`, `notifications`, `notification_deliveries`,
`push_subscriptions`, plus user columns (branch, hire date, birthday,
photo blob, reactivation-notice marker).

## Wave 1 gate

"Monitored user cannot access working APIs without Verified idle
capability" — built and tested in Wave 0 (authorization matrix covers
every failed attestation shape plus lease staleness).

## Notes / deferred

- Protected hard-delete endpoint: Wave 6 (needs the protected Owner flow).
- VAPID keys must be generated and configured (`WebPush:` section) before
  production push; the system runs fine without them.
- Notification *preferences* (per-category sound/vibrate) and DND
  suppression arrive with their modules (Wave 3/4).
