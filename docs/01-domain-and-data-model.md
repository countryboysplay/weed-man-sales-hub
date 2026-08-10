# 01 — Domain & Data Model

Use UUID or ULID internal primary keys. Human-readable record IDs are separate unique columns.

## Core identity tables

### users
Identity-backed user plus:
- id
- username
- display_name
- role
- email
- phone
- branch_id
- hire_date
- birthday
- profile_photo_blob_id
- is_active
- deactivated_at_utc
- deactivated_by_user_id
- deactivation_reason
- scheduled_reactivation_at_utc
- created_at_utc
- deleted_at_utc only during deletion workflow if a tombstone is briefly needed

### user_sessions
- id
- user_id
- token_hash / server session verifier
- created_at_utc
- last_seen_at_utc
- revoked_at_utc
- revoked_by_user_id
- revoke_reason
- device_id
- browser_family/version
- os_family/version
- pwa_installed
- app_version
- ip_hash or security-safe IP metadata according to policy
- idle_capability_state
- idle_permission_verified_at_utc
- idle_detector_started_at_utc
- last_idle_heartbeat_at_utc

### password_reset_requests
Management-mediated forgot-password flow.

## Branches and shifts

### branches
- id
- name
- timezone (normally America/Chicago)
- active

### shift_templates
- id
- name
- role
- day_of_week
- start_local_time
- end_local_time
- active

### user_shift_assignments
Versioned assignment range.

### schedule_exceptions
Public ID SCH.
- user
- date/range
- replacement start/end
- label
- reason
- presence behavior
- acknowledgment required
- acknowledgment deadline
- status

## Sales

### sales
- id
- seller_user_id
- cid as varchar, numeric validated
- sale_type
- campaign
- amount numeric(12,2)
- business_date date
- created_at_utc
- updated_at_utc
- state Active/Deleted
- deleted_at_utc
- deleted_by_user_id
- row_version / concurrency token

### sale_corrections
Historical management correction audit:
- sale_id or prior_sale_reference
- correction_type
- before_json
- after_json
- reason
- actor
- timestamp

Important:
- Same-day employee deletion is functionally irreversible and removed from aggregates immediately.
- Historical deletion/correction must preserve correction audit, not a restoreable employee-facing sale.

### sale_duplicate_overrides
Only if needed to record explicit “prior canceled/resale” confirmation:
- sale_id
- prior_sale_id
- confirmed_by
- timestamp

## Chat

### conversations
- id
- type Direct/Group
- name
- mandatory
- created_by
- active

### conversation_members
- conversation_id
- user_id
- muted_at_utc
- joined_at_utc
- read_position/message timestamp
- group members cannot self-leave

### messages
- id
- conversation_id
- sender_user_id
- body
- edited_at_utc
- created_at_utc
- deleted_at_utc
- reply_to_message_id optional

Deletion must erase body/content from canonical current state. Do not copy deleted content to an audit-content table.

### message_attachments
References immutable file blobs.

### message_reactions

### message_receipts
Per-user read receipt if needed for exact iMessage-style state.

## Announcements

### announcements
- id
- author
- title/body
- priority
- require_ack
- view_by_utc
- acknowledge_by_utc
- published_at_utc
- scheduled_publish_at_utc
- archived_at_utc
- pin_rank nullable
- auto_unpin_at_utc

### announcement_targets
- announcement
- user
- seen_at_utc
- acknowledged_at_utc
- reminder state

Use expanded target rows at publication time so completion is stable even if teams later change.

## Tasks

### task_definitions
Shared definition:
- title
- description
- due
- priority
- recurrence
- reminder rules
- created_by

### task_instances
Independent assignee copy:
- definition_id
- assignee_user_id
- status
- due_at_utc
- completed_at_utc

### task_comments
### task_attachments

## Forms

### forms
- id
- type Native/GoogleLink
- display_name
- draft/published
- pinned rank
- external_url if GoogleLink

### form_versions
Published definition snapshots.

### form_fields
Question/section graph.

### form_submissions
- form_version
- user
- opened/submitted/status
- exact timestamps

### form_answers

### email_requests
Dedicated optimized workflow if implementation is clearer than generic form-submission workflow.

## Resources

### resource_folders
Adjacency list:
- id
- parent_id
- name
- sort_order

### resources
- file/link type
- folder
- title
- description
- blob_id
- external_url
- sensitive_staging_placeholder
- sort_order
- search_document state

### resource_favorites
user + resource/folder target

### resource_download_audit
Owner-visible 365-day download log.

## Recognitions

### recognition_badges
built-in/custom library

### recognitions
- recipient_user_id
- author_user_id
- badge_id
- category
- message
- recognition_date
- created_at
- active_until = created + 30 days

### recognition_reactions/comments

## Presence

### presence_sessions
One contiguous work-presence session:
- user_id
- user_session_id
- started_at_utc
- ended_at_utc
- assigned_shift info snapshot
- idle capability verification reference

### presence_segments
Normalized intervals:
- Available
- Busy
- DND
- Away
- OnBreak
- Offline
- TechnicalGrace
- ApprovedException

### presence_flags
Public ID PRS.
- user
- category
- severity
- start/end
- source
- status
- linked record IDs
- resolved/suppressed by approved exception

### presence_rule_sets
Role/shift configurable thresholds.

## Time off

### time_off_types
configurable label/order/paid flag

### time_off_requests
Public ID TO.
- user
- type
- full/partial
- start/end
- reason
- status
- reviewed_by
- review note
- denial reason
- coverage result snapshot

### time_off_cancellation_requests

## Breaks

### break_types
### break_sessions
### break_correction_requests
Public ID BRK for correction/event record where applicable.
Preserve original before/after.

## Technical

### technical_reports
Public ID TECH.
- reporter
- type
- description
- page
- app/browser/device metadata
- logs/reference metadata
- start timestamp

### technical_grants
- technical_report
- start/end
- granted_by
- reason

## Employee Management Record

### management_notes
Public ID NOTE.
- employee
- category
- priority
- status
- note_body
- created_by
- created_at
- resolved_by/time
- resolution_note
- pinned_rank
- acknowledgment requirement

### management_note_followups
Append-only.

### management_note_ack_targets
### record_links
Generic validated linking between NOTE/PRS/BRK/TECH/TO/SCH/SUP etc.
### management_tags
### tagged_entities

## Support

### support_tickets
Public ID SUP.
- reporter
- priority
- suggested_priority + reason
- status
- assignee
- primary_assignee
- issue_type
- description
- page/context
- app/browser/device
- created/resolved/closed

### support_messages
Visibility EmployeeReply/InternalNote.

### support_collaborators
### support_attachments
### support_links

## Notifications

### notifications
Canonical in-app notification:
- user
- category
- required
- title
- safe_preview
- reference_type/id
- created
- read_at
- acknowledged_at
- snoozed_until
- expires_at
- protected_from_clear

### notification_deliveries
- notification_id
- channel WebPush/InApp/SignalR
- attempt
- state
- last_error
- delivered_at

### push_subscriptions
per browser/device:
- endpoint
- p256dh
- auth
- user/session/device
- active

## Offline sync

### idempotency_keys
- user/session
- key
- operation
- request_hash
- response_json
- expires_at

### sync_actions
Only if server needs explicit queue visibility; client is primary queue holder but server records accepted/rejected sync operations.

### remote_device_commands
- command type Resync/Refresh/ClearSafeCache
- requested_by
- target_session/device
- status
- audit metadata

## Files

### file_blobs
Immutable:
- id
- sha256
- content_type
- original_name
- byte_length
- storage_key
- created_at
- created_by
- scan_status
- deleted/purge state

Never overwrite a blob. “Replace” creates a new blob and updates the resource reference.

## Audit and security

### audit_events
General structured audit:
- id
- category
- action
- actor_user_id
- target_type/id
- reason
- before_json
- after_json
- timestamp
- session/device/correlation
- retention_class

### private_communication_access
Permanent:
- owner
- scope
- target conversation(s)
- reason
- auth event
- started/ended
- permanent audit

No copied message-content history.

### sensitive_exports
Public ID EXP.
### sensitive_export_access
Child re-download audit.

### emergency_access_sessions
Owner emergency flow.

### owner_recovery_security_events

## Reports and archive

### report_schedules
### report_runs
### report_artifacts
### archive_entries
### recovery_records
Public ID REC.
### report_failure_events

## Deployment

### deployment_records
PROD
### staging_records
STAGE
### rollback_records
ROLL
### known_good_versions
### blocked_rollback_versions
### maintenance_windows

## System settings

### settings
typed settings with scope:
- company
- role
- user
- environment

### settings_audit
before/after for sensitive/shared settings.

## Durable eventing

### outbox_messages
- id
- event_type
- payload_json
- occurred_at
- available_at
- attempts
- claimed_at
- processed_at
- last_error

### scheduled_jobs
Persistent schedule configuration/state.

### scheduled_job_runs
Each execution, outcome, retries.
