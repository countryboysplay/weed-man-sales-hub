# 02 — HTTP API Contract

Base path: `/api/v1`

Use RFC 7807 ProblemDetails for errors. All mutations that can originate from the offline queue accept `Idempotency-Key`.

## Conventions

- JSON camelCase.
- IDs are opaque strings to client.
- Money serialized as JSON number with two-decimal semantics; backend uses decimal.
- Date/time fields ending `At` use ISO-8601 UTC.
- Business-date-only fields use `YYYY-MM-DD`.
- List endpoints support cursor pagination where lists can grow.
- Fuzzy search endpoints enforce permission filtering before returning results.

## Identity

- `POST /auth/login`
- `POST /auth/logout`
- `GET /auth/me`
- `GET /auth/sessions`
- `DELETE /auth/sessions/{sessionId}`
- `POST /auth/forgot-password-request`
- `POST /auth/fresh-auth`
- `POST /auth/idle-capability/verify`
- `POST /auth/idle-capability/heartbeat`

Management:
- `POST /users`
- `GET /users`
- `GET /users/{id}`
- `PATCH /users/{id}`
- `POST /users/{id}/deactivate`
- `POST /users/{id}/reactivate`
- `POST /users/{id}/schedule-reactivation`
- `DELETE /users/{id}/schedule-reactivation`
- `POST /users/{id}/reset-password`
- `POST /users/{id}/force-logout`
- protected hard-delete endpoint

## Sales

- `POST /sales`
- `GET /sales/me/today`
- `GET /sales/me/summary?month=...`
- `GET /sales/team/today`
- `GET /sales/team/today/{userId}/breakdown`
- `GET /sales/search?cid=...`
- `PATCH /sales/{id}` same-day seller edit
- `DELETE /sales/{id}` same-day seller delete
- `POST /sales/{id}/historical-correction`
- `DELETE /sales/{id}/historical-delete`
- `GET /sales/users/{userId}/summary`
- `GET /sales/me/export/current-year.csv`

Create Sale request:

```json
{
  "cid": "482193",
  "saleType": "Program",
  "campaign": "AS01",
  "amount": 421.00,
  "duplicateResaleConfirmation": null
}
```

If duplicate prior Program sale requires confirmation, return 409 ProblemDetails with structured extension:

```json
{
  "type": ".../duplicate-sale",
  "title": "A program sale already exists for this CID this year.",
  "status": 409,
  "extensions": {
    "priorSale": {
      "saleId": "...",
      "businessDate": "2026-04-12",
      "amount": 399.00
    },
    "confirmationToken": "short-lived-server-token"
  }
}
```

Resubmit with confirmation token after employee confirms prior canceled/resale.

## Chat

- `GET /conversations`
- `GET /conversations/{id}`
- `GET /conversations/{id}/messages`
- `POST /conversations/direct`
- management `POST /conversations/groups`
- management `PATCH /conversations/groups/{id}`
- `POST /conversations/{id}/messages`
- `PATCH /messages/{id}`
- `DELETE /messages/{id}`
- `POST /messages/{id}/reactions`
- `DELETE /messages/{id}/reactions/{reaction}`
- `POST /conversations/{id}/read`
- `POST /conversations/{id}/mute`
- `DELETE /conversations/{id}/mute`

Owner protected:
- `POST /security/private-communications/access`
- `GET /security/private-communications/access/{accessSessionId}/conversations/...`

## Announcements

- `GET /announcements`
- management `POST /announcements`
- management `PATCH /announcements/{id}`
- management `POST /announcements/{id}/publish`
- management `POST /announcements/{id}/archive`
- management `POST /announcements/{id}/pin`
- `POST /announcements/{id}/seen`
- `POST /announcements/{id}/acknowledge`
- management `GET /announcements/{id}/progress`
- management `POST /announcements/{id}/remind-outstanding`

## Tasks

- `GET /tasks/my`
- `GET /tasks/{id}`
- management `POST /tasks`
- management `PATCH /tasks/definitions/{id}`
- `POST /tasks/{id}/complete`
- `POST /tasks/{id}/comments`
- attachment endpoints
- management progress/history endpoints

## Forms

- `GET /forms`
- `GET /forms/{id}`
- management CRUD for native forms
- publish/unpublish
- `POST /forms/{id}/submissions`
- `PATCH /forms/submissions/{id}` where workflow allows
- Email Request specialized endpoints if modeled separately

## Resources

- `GET /resource-folders/...`
- `GET /resources/...`
- `GET /resources/search?q=...`
- management folder/resource CRUD
- favorite endpoints
- authenticated preview stream endpoint
- management download endpoint
- manager PDF-watermark download endpoint

Never expose direct filesystem paths.

## Recognitions

- `GET /recognitions`
- management `POST /recognitions`
- reactions/comments endpoints
- management badge library endpoints

## Presence

- `GET /presence/me`
- `GET /presence/directory`
- `POST /presence/manual-status`
- `POST /presence/heartbeat`
- `POST /presence/idle-state`
- `GET /presence/me/today-timeline`
- management summary endpoints
- Manager/Owner detailed-history endpoints
- presence rule configuration endpoints

Every presence mutation must validate:
1. authenticated active session
2. mandatory Idle Detection capability verified for monitored role/session
3. recent capability heartbeat
4. server timestamp

## Shifts / time off / breaks

- `GET /schedule/me`
- management shift assignment endpoints
- `POST /time-off`
- `GET /time-off/me`
- `POST /time-off/{id}/cancel`
- management approve/deny
- approved cancellation request endpoints
- `POST /breaks/start`
- `POST /breaks/{id}/end`
- `POST /breaks/{id}/correction-request`
- management correction endpoints
- `POST /technical-reports`
- management grant technical grace

## Management records

- management `GET /employees/{id}/management-record`
- `POST /employees/{id}/notes`
- `POST /notes/{id}/followups`
- `POST /notes/{id}/resolve`
- `POST /notes/{id}/reopen`
- link/unlink
- tag endpoints
- Manager/Owner export endpoints

## Support

- `POST /support`
- `GET /support/my`
- `GET /support/{id}`
- reply endpoints
- management queue/search
- assignment/priority/status endpoints
- internal note endpoint
- resolve/close/reopen

## Notifications

- `GET /notifications`
- `POST /notifications/{id}/read`
- `POST /notifications/mark-all-read`
- `POST /notifications/{id}/acknowledge`
- optional `POST /notifications/{id}/snooze`
- push-subscription CRUD
- preferences endpoints

## Search

- `GET /search?q=...`
- management natural-language endpoint may be `/search/interpret`
- returned result includes `whyMatched`
- all search runs after permission predicates

Owner-sensitive search audit is server-side and not client-optional.

## Reports/archive

- schedule CRUD
- report run/history
- archive browse/access
- recovery preview/execute
- export endpoints

## System / sync

- management `/system/health`
- management `/sync/health`
- remote device command endpoints
- Owner backup/recovery
- Owner deployment/staging/maintenance endpoints

## ProblemDetails extensions

Standardize:
- `code`
- `correlationId`
- `fieldErrors`
- `retryable`
- `currentVersion`
- `conflict`
- `requiredPermission`
- `requiredFreshAuth`
- `recordId`
