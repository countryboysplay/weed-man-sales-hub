# 14 — Retention & Deletion Matrix

This matrix prevents accidental over-retention.

| Data | Normal retention | User hard delete effect | Special rule |
|---|---|---|---|
| User profile/account | Life of account | Delete | hard delete removes |
| User sessions | security/operational period | Delete user-owned session records | global security exception only if explicitly required |
| Active sales | operational/history | Delete employee-owned rows and recalc | historical correction audit treatment follows approved governance |
| Same-day deleted sale | until daily UI boundary if needed | already irreversible | not restorable; excluded totals immediately |
| Chat message current content | current state | Delete employee-owned content | deleted message body must not be secretly retained |
| Chat attachments | current referenced state | Delete/purge according to hard-delete | message shell may show Deleted only if product requires |
| Private communication access audit | Permanent | Preserve | audit metadata only; no deleted message reconstruction |
| Announcement | until manual archive / configured retention | user-target state removed if user hard deleted | announcement object not owned by target user |
| Task employee instance | management completion history | delete user-owned assignment/history on hard delete | definition may remain for other assignees |
| Form submission | workflow-specific | delete user submission | Email Request completed normally disappears/not archived |
| Resource files | until management deletes | unaffected unless user-owned transient upload | download audits separate |
| Resource manager download audit | 365 days | preserve or anonymize only per explicit security policy | Owner-visible |
| Recognition | active 30 days then archive | delete employee-owned recognition/comment as specified | shared badge definition remains |
| Presence history/flags | Life of user unless configured | delete | Supervisor access remains summary-only before deletion |
| Break records | Life of user unless configured | delete | correction original audit deleted with employee hard-delete unless security-exempt |
| Time-off records | Life of user unless configured | delete | team calendar recalculates |
| Technical reports | Life of user | delete | linked general system outage records may persist separately |
| Management notes | Life of user | delete | sensitive export audit can persist separately |
| Support ticket | Life of employee | delete | global system incident may remain separately |
| Notification | configurable by type | delete user notifications | push delivery rows tied only to that user also removed |
| Remote device action audit | 90 days | normally delete user-scoped record on hard delete unless security policy says global | do not retain content unnecessarily |
| Resource download audit | 365 days | security decision applies | configured Owner audit |
| Settings change audit | 365 days | not employee-owned if system setting | shared/system audit |
| Sensitive export EXP audit | 7 years | Preserve | may identify deleted employee only as necessary for audit; do not preserve full deleted source content solely because export existed |
| Backup/staging/restore security audit | long/permanent per security policy | Preserve | global system security record |
| Emergency access audit | Permanent | Preserve | Owner security |
| Deployment PROD/STAGE/ROLL records | Permanent unless explicit change | Preserve | system governance |
| Account timeline | Life of account | Delete with hard delete | except separate global protected security events |
| Report schedule | configured retention | delete personal schedule if owner user hard-deleted | shared schedules handled separately |
| Report failure history | 90 days normal; 365 days Owner detail where specified | remove user-owned where required | system-wide failure record may persist |
| Backup archives | 7 daily copies | hard delete does not rewrite existing historical backups | restore/recovery access is protected and backup retention automatically expires old copies |

## Backup caveat

A hard deletion affects the live canonical application immediately.

Because disaster-recovery backups are point-in-time snapshots, deleted data can remain inside an encrypted retained backup until that backup naturally expires under the seven-day retention policy.

Do not implement a dangerous process that rewrites every retained backup after each user hard delete.

Access to retained backups is Owner-protected and cannot be used as a normal operational history browser.

## General rule

When uncertain:
1. canonical live data follows product deletion rule;
2. operational derived caches are rebuilt/purged;
3. only explicitly designated security/governance records survive;
4. survival of an audit record does not imply survival of deleted content.
