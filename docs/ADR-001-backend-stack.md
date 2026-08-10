# ADR-001 — Backend Stack

## Status

Accepted for implementation handoff.

## Decision

Use:
- ASP.NET Core 10 on .NET 10 LTS
- PostgreSQL 17
- EF Core
- ASP.NET Core Identity
- SignalR
- IIS -> Kestrel on Windows Server 2019

## Rationale

The production target is Windows Server 2019 and the application requires:
- persistent authenticated PWA sessions
- high-volume realtime interaction
- WebSockets
- strong role/policy authorization
- background workers
- secure key management
- predictable Windows hosting

ASP.NET Core integrates these concerns cleanly in one production runtime. PostgreSQL 17 is the chosen relational database for robust transactions, JSONB where useful, indexing, concurrency, and a supported Windows Server 2019 installer.

## Consequences

Positive:
- one primary backend language/runtime
- first-class Windows/IIS hosting
- SignalR reduces custom realtime infrastructure
- Identity and Data Protection reduce custom security code
- modular monolith is operationally simple

Tradeoffs:
- Windows Server remains a deployment constraint
- mandatory Idle Detection creates a browser/device compatibility gate
- PostgreSQL administration must be included in operations/runbooks
