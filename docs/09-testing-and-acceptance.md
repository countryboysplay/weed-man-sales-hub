# 09 — Testing & Acceptance

## Test layers

### Unit
Business rules without DB:
- sales duplicate logic
- sales business-date boundaries
- role rules
- coverage calculations
- presence rule evaluation
- record-ID formatting
- notification DND policy
- announcement completion
- task recurrence
- retention policy decisions

### Integration
Real PostgreSQL test database:
- EF mappings
- unique constraints
- transaction/outbox behavior
- concurrency
- idempotency
- cascade/hard-delete rules
- session revocation
- audit writes
- scheduled-job claims

### Authorization matrix
Dedicated tests for every endpoint:
- anonymous
- agent
- supervisor
- manager
- owner
- owner without fresh auth
- owner with fresh auth but missing master recovery
- resource ownership/member/nonmember

### SignalR
- authenticated connection
- group membership
- forbidden group data isolation
- reconnect
- chat delivery
- read receipt
- presence state
- sales event
- notification update

### PWA contract
Backend contract tests for:
- idle capability verified
- unsupported/denied blocked
- stale capability blocked
- offline idempotent replay
- conflict payload

## Critical acceptance scenarios

1. Agent adds $421 AS01 sale -> sale saves once -> Team Today and My Sales update realtime -> celebration evaluates -> notification/event no duplicates.
2. Retry same POST with same idempotency key -> returns original success, no duplicate row.
3. Program duplicate CID current year -> 409 structured prior-sale response -> explicit resale confirmation permits new row.
4. Agent deletes own sale same business day -> totals recalc -> no restore -> duplicate no longer blocked.
5. Agent tries historical delete -> 403.
6. Supervisor corrects historical sale with reason -> before/after audit.
7. User deletes chat message -> body unavailable to all including later Owner private inspection.
8. Owner private inspection without fresh protected auth -> denied.
9. DND user receives no push but canonical notifications accumulate.
10. Idle permission missing -> presence-monitored working API blocked.
11. Idle capability becomes stale -> heartbeat/presence transition handled.
12. Time-off approval triggers coverage warning and decision audit.
13. Break correction recalculates affected presence flags but preserves original record.
14. Technical report alone does not suppress attendance flag; approved technical grace does.
15. Hard delete removes employee-owned operational data and recalculates team totals while protected global security audits obey exception policy.
16. Outbox worker crashes after DB commit -> event is delivered after restart without duplicate user-visible durable state.
17. Backup is created, encrypted, uploaded, verified, then retention prune runs.
18. Restore authentication without master/TOTP -> blocked.
19. Full restore selects older backup -> blocked.
20. Report-only recovery from older retained backup -> REC record created without production restore.

## DST tests

America/Chicago:
- spring-forward day
- fall-back repeated hour
- business-midnight duplicate reset
- shift boundaries
- scheduled 12:30 AM backup
- due/reminder calculations

Use a time provider abstraction so tests can freeze time.
