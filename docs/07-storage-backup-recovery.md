# 07 — Storage, Backup & Recovery

## Production file storage

Keep outside deployment path, e.g.:

```text
D:\WeedManSalesHub\Data\
  uploads\
  generated\
  reports\
  staging-placeholders\
  temp\
```

Actual path must be configuration-driven.

Files are immutable blobs:
- new upload -> new blob ID/storage key
- replacement -> new blob
- reference changes transactionally
- old blob becomes unreferenced and later eligible for purge according to rules

## Upload security

- server-generated storage names
- allowlist content types/extensions by feature
- maximum size by feature
- hash each file SHA-256
- do not trust browser MIME
- antivirus/malware scanning integration point
- never execute uploaded content
- authenticated streaming endpoints
- authorization checked before each preview/download

## PDF watermark

Generate/stream a derived PDF:
- employee viewer: user identity + timestamp watermark, no download affordance
- manager PDF download: manager identity + date watermark

Original PDF remains immutable.

## Backup consistency

The backup must represent a coherent DB + file state.

Recommended implementation:
1. acquire application backup write barrier for mutations affecting DB/file references
2. drain active file mutations
3. capture database backup
4. capture exact file storage snapshot/manifest corresponding to database state
5. release write barrier as soon as the consistency point is secured
6. encrypt package
7. upload to Dropbox
8. verify checksum/decryptability
9. mark backup Verified
10. prune oldest backup beyond seven retained only after verification

Claude Code may implement a more robust Windows snapshot mechanism if verified, but it must preserve this consistency guarantee.

## Encryption

Backup archives:
- modern authenticated encryption
- separate server-held backup key
- key never included unencrypted inside same backup
- Owner recovery process documented
- key access limited to service account / Owner recovery workflow

## Full restore

Only latest Verified production backup.

Workflow:
1. protected Owner auth
2. required reason
3. maintenance start
4. create temporary current-state rollback point
5. validate backup manifest
6. restore DB/files to staging restoration location
7. swap/activate transactionally where feasible
8. migrations/version compatibility check
9. smoke tests
10. reopen production only when healthy
11. preserve restore audit

If restore validation fails, return to pre-restore state.

## Report-only recovery

Older retained backups may be mounted/read in isolated recovery context.

- never overwrite production tables just to inspect one report
- recover selected report artifact
- create REC public ID
- mark artifact Recovered
- store source backup and recovery metadata
- preserve original if it exists

## Dropbox

Implement storage adapter interface so Dropbox is the initial provider but can be changed later.

Interface concepts:
- UploadAsync
- ExistsAsync
- DownloadAsync
- DeleteAsync
- ListAsync
- VerifyMetadataAsync

Do not bake Dropbox path logic into business services.
