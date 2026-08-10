# 06 — Background Jobs & Scheduling

Do not rely on `Task.Delay` loops with no persistence for business-critical jobs.

## Persistent job tables

`schedule_jobs`
- job key
- job type
- cron/recurrence definition or next-run UTC
- timezone
- enabled
- lease owner
- lease expires
- last success/failure
- next run

`scheduled_job_runs`
- run id
- job id
- scheduled time
- started/completed
- attempt
- result
- error class
- correlation

## Job worker behavior

- claim due jobs transactionally
- use lease with expiration so a crash is recoverable
- idempotent execution
- update next run only in committed transaction
- retry policy based on job type
- prevent duplicate concurrent runs
- use America/Chicago for business schedules, convert next execution to UTC

## Required recurring jobs

- 12:30 AM CT production backup
- announcement scheduled publish
- announcement view/ack reminders
- task recurrence generation
- task overdue reminders
- time-off/schedule reminders
- DND catch-up generation
- presence flag evaluation/cleanup
- session/capability stale detection
- notification retry
- outbox dispatch
- report schedules
- report retry/auto-resume
- archive retention actions
- recognition 30-day active -> archive transition
- resource/debug/deploy temp cleanup
- PWA version compliance evaluation
- deployment validation windows
- scheduled reactivation
- maintenance-window transitions

## Transactional outbox worker

Claim rows in batches.
Use PostgreSQL row locking / skip-locked style semantics.
Backoff on delivery failures.
Never delete failed rows before retention/debug window.
Poison events move to failed state and trigger management health alert.

## Backup job

Backup is a first-class job with:
- preflight
- controlled write barrier or other consistency mechanism
- DB snapshot
- exact referenced file snapshot
- encrypted package
- off-server upload
- integrity verification
- retention prune only after new backup verified
- system health event
