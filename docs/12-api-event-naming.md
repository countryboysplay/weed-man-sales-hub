# 12 — Naming Standards

## API

Use plural nouns:
- `/sales`
- `/users`
- `/announcements`

Commands that do not map cleanly to CRUD:
- `/sales/{id}/historical-correction`
- `/time-off/{id}/approve`
- `/auth/fresh-auth`

## Domain events

`module.entityAction.v1`

Examples:
- `sales.saleCreated.v1`
- `sales.saleDeleted.v1`
- `chat.messageCreated.v1`
- `chat.messageDeleted.v1`
- `presence.statusChanged.v1`
- `announcements.progressChanged.v1`
- `tasks.taskCompleted.v1`
- `support.ticketCritical.v1`
- `security.privateCommunicationAccessStarted.v1`

## Public IDs

Use DB-backed yearly counter with transaction/locking.

Do not derive next public ID from `COUNT(*) + 1`.

Recommended table:

```text
public_id_sequences
  prefix
  year
  last_value
  updated_at
PK(prefix, year)
```

Allocate inside transaction and format zero-padded 5 digits.
