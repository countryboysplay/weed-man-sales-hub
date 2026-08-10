# 03 — Realtime, SignalR & Notifications

## SignalR hubs

Prefer a small number of hubs, not one hub per feature.

### `/hubs/app`

Authenticated application events:
- sales
- celebrations
- announcements
- tasks
- approvals
- support
- notifications
- system commands

### `/hubs/chat`

Chat-specific events:
- message created/edited/deleted
- typing start/stop
- reaction
- read receipt
- conversation metadata changes

### `/hubs/presence`

High-frequency but coarse events:
- status update
- presence directory update
- management presence alert
- session capability/connection state

## Groups

SignalR groups:
- `user:{userId}`
- `conversation:{conversationId}`
- `role:{role}`
- `management`
- `owners`
- `branch:{branchId}` if useful

Group membership is transport routing only. Authorization must still be enforced by server-side policies and resource checks.

## Event envelope

```json
{
  "eventId": "uuid",
  "eventType": "sales.saleCreated.v1",
  "occurredAt": "2026-08-10T20:15:31.220Z",
  "correlationId": "...",
  "payload": {}
}
```

## Transactional outbox

For any durable business event that must trigger realtime/push:

1. application use case changes canonical DB state
2. same transaction inserts outbox row
3. commit
4. background worker claims outbox row
5. worker creates notification/delivery rows as needed
6. sends SignalR
7. sends Web Push where policy permits
8. marks processed or schedules retry

This prevents “sale saved but dashboard never updated” style partial failures.

## Chat realtime behavior

Typing:
- ephemeral only
- never stored long term
- throttled
- auto-expire server/client state

Read receipts:
- durable read marker or receipt
- broadcast changed read state

Message deletion:
- canonical message body removed/nullified
- emit `chat.messageDeleted.v1`
- clients remove content
- no deleted-content audit archive

## Presence realtime behavior

Presence updates should be coarse enough to avoid excessive writes.

Recommended:
- session heartbeat around 30–60 seconds while active
- IdleDetector state transitions sent immediately
- server presence evaluator produces intervals/segments
- UI broadcast only when visible presence status changes

Do not write every mouse/key event.

## Push notification model

Use standard Web Push with VAPID credentials.

Store subscription per user session/device.

Web Push payload should be minimal:
- notification id
- safe title
- safe preview
- reference route
- category

Never include sensitive management note content, private chat content, or security details in a lock-screen push.

## DND

Server checks DND before push delivery.

DND does not delete the canonical in-app notification. It suppresses external notification channels.

When DND ends:
- build a catch-up summary of active missed notification categories
- no content previews in that summary

## Celebration event

Sale create transaction emits `sales.saleCreated.v1`.

Celebration policy service evaluates:
- global enabled
- audience
- user visual/sound preferences
- cooldown
- summary combine window

The sale event itself is durable.
The animation event is presentation-oriented and may be ephemeral.

## Backpressure

If a client disconnects:
- durable notification state is still available through HTTP
- chat messages are canonical in DB
- presence connection state updates server-side
- on reconnect client resynchronizes using last known cursor/version

SignalR is not the only copy of important data.
