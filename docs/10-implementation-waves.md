# 10 — Claude Code Implementation Waves

Do not attempt all modules in one giant change.

Each wave must finish with:
- migrations
- automated tests
- OpenAPI/contract update
- authorization tests
- basic health checks
- no TODO placeholders in security-critical paths

## Wave 0 — Solution foundation

Build:
- solution/projects
- PostgreSQL DbContext
- Identity
- cookie auth
- server-side sessions
- role policies
- ProblemDetails
- correlation IDs
- antiforgery
- rate limiting
- business time service
- public ID generator
- audit infrastructure
- transactional outbox
- scheduled job framework
- file blob abstraction
- SignalR base
- health endpoints

Gate:
- login/logout works
- user can be created by seed/management dev endpoint
- role authorization proven
- outbox test proven
- migrations clean

## Wave 1 — User lifecycle / directory / app foundation

Build:
- users
- sessions
- profile/directory
- deactivate/reactivate/scheduled reactivation
- password reset requests
- force logout
- push subscriptions
- notification canonical model
- mandatory Idle Detection capability handshake

Gate:
- monitored user cannot access working APIs without Verified idle capability

## Wave 2 — Sales

Build complete Sales domain:
- create
- duplicate logic
- same-day edit/delete
- historical corrections
- realtime aggregates
- CSV export
- celebration events

Do not move forward until sales rules are exhaustively tested.

## Wave 3 — Communication / work

- Chat
- Announcements
- Tasks
- Forms
- Resources
- Recognitions

## Wave 4 — Workforce

- Presence
- Shift assignments
- Time off
- Breaks
- Technical reports/grace
- Approvals
- coverage warnings

## Wave 5 — Management

- employee management records
- support
- advanced search
- reports
- archive
- sync/system health

## Wave 6 — Owner / production governance

- sensitive exports
- private comm access
- emergency access
- settings audit
- backup/recovery
- staging refresh
- maintenance
- launch/deployment records
- rollback governance

## Wave 7 — Production hardening

- offline conflict behavior
- version compliance
- backup restore drills
- performance
- security tests
- dependency patching
- production automation
- disaster recovery runbook

## Commit discipline

Claude Code should:
- use small coherent commits
- never mix unrelated refactors with business-rule changes
- update migrations and tests in same logical change
- document any deviation from this spec in an ADR and ask for explicit human approval before implementing product-rule changes
