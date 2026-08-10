# Weed Man Sales Hub — Backend Handoff for Claude Code

This package is the implementation handoff for the approved Connecteam replacement GUI.

## Goal

Build a production-ready backend for the Weed Man Sales Hub PWA, hosted on Windows Server 2019, with:

- ASP.NET Core 10
- PostgreSQL 17
- Entity Framework Core
- ASP.NET Core Identity
- SignalR for realtime features
- Web Push for PWA notifications
- IIS reverse proxy / process hosting
- encrypted file storage and backup workflows
- Development -> Staging -> Test -> main -> Production deployment governance

The backend must implement the business rules in `CLAUDE.md`. Those rules are authoritative unless the human owner explicitly changes them.

## Start here

1. Read `CLAUDE.md`.
2. Read `docs/00-architecture.md`.
3. Read `docs/01-domain-and-data-model.md`.
4. Read `docs/02-api-contract.md`.
5. Read `docs/03-realtime-and-notifications.md`.
6. Read `docs/04-security-auth-and-audit.md`.
7. Read `docs/05-presence-idle-detection.md`.
8. Read `docs/06-background-jobs.md`.
9. Read `docs/07-storage-backup-recovery.md`.
10. Read `docs/08-windows-server-deployment.md`.
11. Read `docs/09-testing-and-acceptance.md`.
12. Follow `docs/10-implementation-waves.md`.

## Reference GUI

The `reference-gui/` folder contains all standalone HTML mockups currently available from the approved GUI design phase. Treat their workflows, labels, role visibility, and interaction patterns as frontend contract references.

Screens 1–3 were originally approved as in-chat prototypes rather than standalone HTML. Their backend-relevant requirements are restated in `docs/11-screens-1-to-3-contract.md`.

## Do not do these things

- Do not replace the approved workflows with a generic admin template.
- Do not add public self-registration.
- Do not use JWTs in browser localStorage.
- Do not make Idle Detection optional.
- Do not use an in-memory-only scheduler for business-critical recurring jobs.
- Do not store plaintext passwords, master recovery credentials, TOTP secrets, encryption keys, or session tokens.
- Do not let Supervisor/Manager roles access Owner-only private communication inspection.
- Do not retain deleted chat message content in a hidden “audit” copy.
- Do not deploy production code by overwriting the live directory in place.
- Do not put the PostgreSQL database or uploaded files inside the deployment directory.
