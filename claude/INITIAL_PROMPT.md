# Initial prompt to paste into Claude Code

You are implementing the Weed Man Sales Hub backend.

Read `CLAUDE.md` completely before making changes. Then read every file in `docs/`.

The approved GUI prototypes are in `reference-gui/`. They are reference contracts, not disposable examples.

Start with **Wave 0 only** from `docs/10-implementation-waves.md`.

Before coding:
1. Summarize the architecture you will implement.
2. List the projects you will create.
3. List the first EF Core entities/tables needed for Wave 0.
4. List security middleware/order.
5. Identify any contradiction in the requirements.
6. Do not change a product rule to resolve a contradiction. Flag it explicitly.

Then implement Wave 0.

Required stack:
- .NET 10 LTS
- ASP.NET Core 10
- PostgreSQL 17
- EF Core
- ASP.NET Core Identity
- cookie authentication
- server-side session records
- SignalR
- transactional outbox
- persistent scheduled-job state
- structured logs
- IIS/Kestrel deployment target

Important:
- Idle Detection is mandatory for monitored work sessions.
- Never store auth tokens in browser localStorage.
- Never retain deleted chat message bodies in a hidden audit copy.
- Do not use floating point for money.
- Store timestamps UTC and use America/Chicago for business-time rules.
- Every authorization-sensitive behavior needs automated tests.
- Do not start Wave 1 until Wave 0 tests pass.

At the end of Wave 0, give me:
- files created/changed
- migrations created
- tests and results
- any manual server prerequisites
- any unresolved decision that genuinely blocks Wave 1
