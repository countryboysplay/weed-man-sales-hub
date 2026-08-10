# 15 — Backend Definition of Done

A backend feature is not complete because an endpoint returns 200.

It is complete only when all applicable items pass.

## Functional
- business rule implemented server-side
- response contract documented
- validation specific and deterministic
- timezone/business-date behavior correct
- concurrent requests handled
- idempotent replay handled where needed

## Authorization
- anonymous denied if protected
- each of the four roles tested
- resource owner/nonowner tested
- fresh auth tested where required
- protected Owner credential/TOTP tested where required

## Persistence
- EF migration included
- indexes support expected query
- delete behavior explicit
- audit behavior explicit
- outbox row in same transaction where realtime/notification required

## Realtime
- SignalR event documented
- unauthorized clients cannot subscribe/read data
- reconnect state can be recovered by HTTP
- realtime failure cannot lose canonical data

## Operations
- logs include correlation ID
- health impact surfaced where needed
- scheduled job durable
- failure retry behavior tested
- retention cleanup defined

## Security
- no sensitive plaintext log
- no unsafe browser token storage dependency
- upload validation where applicable
- rate limiting where applicable
- antiforgery where applicable

## Tests
- unit
- integration
- authorization
- failure path
- concurrency/idempotency where applicable
- DST/time-boundary tests where applicable
