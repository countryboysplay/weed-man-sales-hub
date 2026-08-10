# Weed Man Sales Hub

Employee/sales PWA for Weed Man — the Connecteam replacement.

Backend: ASP.NET Core 10 (.NET 10 LTS), PostgreSQL 17, EF Core, ASP.NET Core Identity,
SignalR, Web Push. Production host: Windows Server 2019 (IIS -> Kestrel).

- `CLAUDE.md` — authoritative business rules
- `docs/` — architecture, domain model, API contract, security, waves
- `reference-gui/` — approved GUI prototypes (frontend contract references)
- `src/`, `tests/` — the .NET solution (from Wave 0 on)

See `docs/handoff-README.md` for the original handoff index.

## Development quickstart

```bash
# prerequisites: .NET 10 SDK, PostgreSQL (17 in production; 16+ works for dev)
createdb saleshub_dev   # role/credentials in appsettings.Development.json
dotnet build
dotnet run --project src/SalesHub.Api    # migrates + seeds the dev Owner
dotnet test                              # 82 tests; needs local PostgreSQL
```

Integration tests create and drop their own databases; point them at another
server with `SALESHUB_TEST_PG="Host=...;Username=...;Password=..."`.

Wave status: **Wave 0 complete** — see `docs/wave-0-summary.md`.
