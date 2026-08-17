# ADR-003 — Backup destination: on-premises server share instead of Dropbox

Date: 2026-08-17
Status: Accepted (Owner decision)

## Context

CLAUDE.md §20 and docs/07 specify daily encrypted backups to an
"off-server Dropbox destination." docs/07 also requires the destination to
sit behind a storage-adapter interface precisely so the provider can change
("Implement storage adapter interface so Dropbox is the initial provider
but can be changed later. Do not bake Dropbox path logic into business
services.").

The company already operates a second server with ample storage. The Owner
(Jonathan Lindsay) directed that backups go to that existing on-premises
server rather than Dropbox.

## Decision

The initial backup storage provider is a **network share on the existing
company server** (SMB/UNC path), not Dropbox.

Everything else in the backup policy is unchanged:

- daily at 12:30 AM America/Chicago
- keep 7 daily backups
- database + uploaded storage as a consistent snapshot
- backups encrypted **before** leaving the production box
- encryption/recovery key stored separately from the production server
  (and now also NOT solely on the backup server)
- full restore and report-only recovery remain protected Owner flows
  (fresh auth + master credential + TOTP + reason)

The Wave 7 backup job writes through the same storage-adapter seam the
spec mandated, so moving to Dropbox (or anywhere else) later is a
configuration/adapter change, not a redesign.

## Consequences

- **Still off-server**: the destination is a different machine, so a
  production hardware failure does not take the backups with it.
- **New shared fate to accept**: unlike Dropbox, both machines live in the
  same building/network. A site-level event (fire, flood, theft,
  ransomware that reaches both boxes) could claim production *and*
  backups. Mitigations required by this ADR:
  - backups are encrypted client-side, so the backup server never holds
    plaintext employee data;
  - the production box gets **write-only-style** access to the share (no
    delete/modify of prior days where the backup server's OS supports it,
    e.g. via per-day folders and NTFS deny-delete ACLs) to blunt
    ransomware that compromises production;
  - the recovery/encryption key lives in the password manager and one
    other secure location, on neither server;
  - periodically (quarterly at minimum) copy the latest verified backup to
    an offline or offsite medium — this replaces the geographic separation
    Dropbox provided.
- No third-party account, API tokens, or egress bandwidth required; the
  nightly window is limited by LAN speed instead of upload speed.
- `docs/server-prep-runbook.md` §9 now prepares the share and service
  account instead of a Dropbox app folder.
