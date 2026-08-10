# 08 — Windows Server 2019 Deployment

## Production components

```text
Internet
  |
HTTPS
  |
IIS 10
  |
ASP.NET Core Module
  |
Kestrel (.NET 10)
  |
  +-- PostgreSQL 17 Windows service
  +-- external data directory
  +-- Data Protection key directory
  +-- logs
```

## IIS requirements

- install Web Server (IIS)
- install WebSocket Protocol feature
- install current .NET 10 Hosting Bundle
- HTTPS binding with trusted certificate
- redirect HTTP -> HTTPS
- request limits aligned to allowed upload sizes
- app pool identity with least-privilege filesystem permissions
- no directory browsing
- production environment variable set explicitly

## Directories

Example:

```text
D:\WeedManSalesHub\
  releases\
    2026.08.10.1\
    2026.08.12.1\
  current\            # pointer/junction or deployment-selected active path
  shared\
    appsettings\
    dataprotection-keys\
    logs\
  data\
    uploads\
    generated\
    reports\
```

Database and uploaded files never live inside a release folder.

## PostgreSQL

- PostgreSQL 17
- local-only listen unless remote DB is explicitly needed
- dedicated DB/service account
- strong generated credential stored using secure server secret strategy
- migrations executed by controlled deploy identity, not broad runtime privileges if practical
- connection encryption even locally if desired; remote connections require TLS

## GitHub Actions

Recommended workflow:

1. PR -> build/tests.
2. merge Development -> deploy dev.
3. promote to Staging -> deploy staging.
4. Test branch/environment -> acceptance.
5. merge main -> create production candidate.
6. Launch Center final Owner confirmation.
7. self-hosted Windows runner downloads signed/versioned artifact.
8. unpack to new release directory.
9. run DB migration preflight.
10. run readiness checks.
11. apply migration strategy.
12. switch IIS/current pointer.
13. smoke tests.
14. mark Pending Validation.
15. rollback if critical checks fail.

## Atomic deployment

Never copy new binaries over the active running application.

Use versioned release folders and switch active target.

Maintain:
- current
- previous
- Known Good
- Do Not Roll Back To list

## Migrations

Classify migrations:
- backward-compatible expand
- data backfill
- contract/destructive

Production deploy must reject unsafe destructive migration unless explicitly handled by migration plan and readiness rules.

Prefer expand/backfill/contract across releases for high-risk changes.

## Health endpoints

Anonymous minimal:
- `/health/live`

Protected/internal:
- `/health/ready`
- DB
- storage
- background workers
- outbox lag
- push configuration
- backup status
- disk capacity
- current version

## Logs

Structured rolling logs:
- application logs
- deployment logs
- backup/recovery logs
- security audit in DB

Debug logs may have short retention; security audit retention follows business rules.
