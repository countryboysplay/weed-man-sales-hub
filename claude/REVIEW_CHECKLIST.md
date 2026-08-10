# Claude Code Review Checklist

Use this before accepting each implementation wave.

## Architecture
- [ ] Modular boundaries preserved
- [ ] Controller/endpoint is thin
- [ ] Domain rule enforced server-side
- [ ] No frontend-only security rule
- [ ] No new unnecessary service/dependency

## Database
- [ ] EF migration included
- [ ] Indexes added for real query paths
- [ ] Money decimal
- [ ] timestamps UTC
- [ ] business date explicit where needed
- [ ] concurrency token where mutable
- [ ] deletion semantics match product rules

## Security
- [ ] policy tests
- [ ] no secret logging
- [ ] fresh auth where required
- [ ] session revocation works
- [ ] rate limiting
- [ ] antiforgery
- [ ] audit retention class correct

## Realtime
- [ ] canonical DB state first
- [ ] outbox in same transaction
- [ ] SignalR event can be replay/recovered by HTTP state
- [ ] reconnect scenario tested

## Presence
- [ ] Idle Detection required
- [ ] capability lease validated
- [ ] server authoritative timestamp
- [ ] no raw keystroke/activity collection

## Operations
- [ ] health check updated
- [ ] structured logs
- [ ] failure is recoverable
- [ ] deployment rollback compatibility considered
