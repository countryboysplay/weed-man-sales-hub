# Wave 3 — Completion Summary

Status: **complete**. Full suite: **171 tests, all passing**
(68 unit, 64 integration, 34 authorization, 5 end-to-end). Wave 4 not started.

Delivered in three commits: Chat + Announcements, Tasks + Recognitions,
Forms + Resources. Three migrations, one per commit.

## Chat (CLAUDE.md §7)

Get-or-create DMs (canonical pair key); management-only group creation and
editing; **no self-leave route exists at all**; mandatory groups refuse
muting and going mandatory clears existing mutes; `@everyone` reserved for
management; own-only edits (flagged "Edited", no time limit); own-only
deletes that **erase the body in canonical state with no hidden copy** —
tested against the audit stream. Reactions, cursor-paged history,
per-member read positions, attachment blobs (allowlist, 25 MB). Realtime:
membership-routed outbox fan-out for durable events; `/hubs/chat` carries
ephemeral typing only (membership checked server-side, nothing stored).
Durable notifications go to DM recipients and mentioned users; ordinary
group traffic stays unread-badge only. DND suppression joins in Wave 4.

## Announcements (CLAUDE.md §8)

Targets expanded to rows at publish (management targets excluded from the
completion percentage); Seen and Acknowledged tracked separately; **three-pin
cap** with **seven-day auto-release** that keeps the announcement active;
completion at 100% notifies management once with the exact Central time;
scheduled publish, configurable reminders reaching **only outstanding
users**, and archiving — all via the every-minute maintenance job.

## Tasks (CLAUDE.md §9)

Management-only creation assigning one, several, or everyone; independent
per-assignee instances; completion clears the assignee's list while history
stays management-visible with per-definition progress; comments with
explicit @mention notifications; daily/weekly/monthly recurrence minting
period-keyed instances exactly once (unique index makes job re-runs
harmless); per-definition overdue reminders capped at one per instance per
day.

## Recognitions (CLAUDE.md §13)

Seeded built-in badge library + management-created custom badges;
management-only issuing with recipient notification and a company-wide
realtime event; reactions/comments open to everyone; 30-day
active-to-archive transition via the work-maintenance job.

## Forms (CLAUDE.md §10)

Native builder with the four field types (single-line, number, dropdown,
yes/no), required flags, sections, and conditional branching — the graph
lives as a versioned JSON snapshot in `form_versions` (an implementation
choice over the sketched `form_fields` rows: versioning and immediate-
effect published edits fall out naturally; flagging per the deviation
rule). Published edits create a new version instantly; submissions validate
against the current version — matching answers kept, hidden-branch and
removed-field answers cleared, required/typed/option rules enforced
server-side. Google Form links (https-only, custom display name, no
submission tracking). The dedicated **Email Request workflow**: CID +
customer email + quote type + lawn area/coverage, management queue with
notifications, and completion that notifies the submitter and **deletes the
request — deliberately not archived**.

## Resources (CLAUDE.md §11)

Nested folders, file/link/video resources (PDF/XLSX/DOCX/PPTX/images,
100 MB cap, replace = new immutable blob), favorites, metadata search
(ILIKE; content indexing arrives with the search module in Wave 5),
sensitive-staging placeholder flag. **Access rules, all server-side:**
employees read-only; agents have no download route at all; the PDF viewer
streams a **derived copy watermarked with the viewer's identity and
date** (PDFsharp, diagonal repeating stamp, Liberation fonts on Linux /
platform fonts on Windows); manager downloads are always audited
(365-day Owner-visible log with automatic retention sweep) and PDFs carry
the manager+date watermark while office files pass unchanged. No
filesystem path ever leaves the server.

## Notes / deferred within scope

- Chat @mentions are explicit ids from the client (the composer knows who
  was picked); free-text `@name` parsing is a frontend concern.
- Announcement/resource attachments-in-feed, per-user folder/sort/view
  memory, and celebration policy settings land with the settings/feature-
  control work (they are presentation preferences, not business rules).
- If watermarking ever fails on a malformed PDF, the viewer serves the
  original and logs an error — authorization and audit still applied; the
  failure is loud in ops. No content is ever served to a role that could
  not access it.
