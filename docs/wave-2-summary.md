# Wave 2 — Completion Summary

Status: **complete**. Full suite: **150 tests, all passing** twice in a row
(68 unit, 43 integration, 34 authorization, 5 end-to-end). Wave 3 not started.

## Built

**Add Sale** (`POST /sales`), server-authoritative everywhere:
- CID digits-only; campaigns AS01 (Program) / GC01 · AE01 · OS01 (Upsell);
  amount positive decimal, two decimals, `numeric(12,2)`.
- **$5,000 rule**: amounts above the threshold return
  `409 largeAmountConfirmationRequired` with the sale details echoed;
  resubmit with `largeAmountConfirmed`. Exactly $5,000 needs no confirmation.
- **Duplicate rules, current business year (America/Chicago, resets Jan 1):**
  Program CID duplicates return the structured 409 from docs/02 — prior
  sale's date/amount plus a ten-minute Data-Protection confirmation token
  bound to (seller, CID, prior sale). Resubmitting with the token records a
  `sale_duplicate_overrides` row. Upsell CID+campaign duplicates block hard
  (no override path). Deleted sales never block; last year's sales never block.
- **Idempotency-Key** on create: same key + same payload replays the stored
  201; same key + different payload → `409 idempotencyKeyReuse`.
- Timestamps and business dates assigned server-side; sellers can only ever
  create their own sales (the seller is the session, never a request field).

**Same-day seller edit/delete**: own sale + business date = today (CT) only.
Edits revalidate every rule including re-entered duplicate territory and the
large-amount confirmation. Delete is the spec'd tombstone: no reason, no
undo, out of totals instantly, visible in `me/today` until business
midnight, frees the CID for a replacement, gone from history/export.

**Historical corrections (management)**: reason required; `sale_corrections`
keeps before/after JSON; `audit_events` row at SevenYears retention;
historical delete mirrors it with a Delete correction. Sellers attempting
yesterday's rows get `403 sameDayWindowClosed`.

**Aggregates** (all realtime via outbox → SignalR `all` group):
- Team Today: every active Sales Agent appears at zero; management only
  with a sale; net desc, then count. Drilldown: per-campaign count/net only —
  verified to leak no CID and no timestamps.
- My Sales: today (with tombstones), month-by-month + YTD by category,
  another-agent summary for management, own current-year CSV export
  (active rows only).

**Events**: `sales.saleCreated/Updated/Deleted/Corrected.v1` broadcast to
every signed-in user; the payload carries seller/type/campaign/amount and
**never the CID** (celebrations mockup rule, asserted end-to-end).

**The monitored-work gate is now live on a real surface**: the whole
`/sales` group requires `MonitoredWorkSession` — agents without a Verified
idle capability get `403 idleCapabilityRequired` on every sales route;
management passes without the handshake.

## Migration

`WaveTwoSales`: `sales` (xmin concurrency token, seller/date + cid/date
indexes), `sale_corrections`, `sale_duplicate_overrides`. Duplicate
semantics stay an application rule per `db/schema-notes.sql` so the resale
flow remains possible.

## Deferred within scope

- Celebration *policy* (enable/cooldown/combine windows, personal
  mute/animation choice) is presentation + settings — arrives with the
  settings/feature-control work; the durable sale event it feeds from is done.
- Business-midnight rollover of Team Today medals is a client render concern;
  the API is date-scoped already.
