# 11 — Backend Contract for Approved Screens 1–3

These were approved before standalone HTML export.

## Screen 1 — Sales Agent Dashboard

Backend data:
- Team Sales Today
- My Sales Today
- Add Sale
- current ranking
- monthly personal sales
- high-priority announcement
- active tasks

Realtime:
- team sale totals
- rank changes
- personal totals
- celebration events
- announcement/task state

Add Sale:
- CID autofocus frontend behavior
- Program/Upsell
- AS01 / GC01 / AE01 / OS01
- amount
- server validates all business rules

## Screen 2 — Chat

Desktop:
- app navigation + conversation list + current chat
- app sidebar may collapse fully to zero width

Mobile:
- conversation list / chat optimized for touch

Backend:
- DM/group
- presence
- typing
- read receipts
- edit/delete
- attachments
- mandatory group mute rules
- current-state-only Owner protected inspection

## Screen 3 — Announcements

Feed:
- priority
- acknowledgment required
- pinning
- attachments
- reactions/comments

Management:
- seen / acknowledged / not seen
- target progress
- reminders
- scheduled publish
- exact completion event and realtime progress

High Priority logic follows `CLAUDE.md`.
