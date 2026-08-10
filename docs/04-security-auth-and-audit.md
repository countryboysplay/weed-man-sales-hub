# 04 — Security, Authentication & Audit

## Browser authentication

Prefer same-origin cookie auth.

Cookie:
- Secure
- HttpOnly
- appropriate SameSite
- short-enough validation interval
- server-side session record required
- rotation/reissue on sensitive identity changes

State-changing requests:
- antiforgery token/header
- rate limiting
- authorization policy
- idempotency where applicable

## Identity

Use ASP.NET Core Identity for:
- password hashing
- lockout
- security stamp
- role membership
- password validation

Extend Identity user rather than writing custom password cryptography.

## Authorization

Create named policies:

- `Employee`
- `Management`
- `SupervisorOrAbove`
- `ManagerOrOwner`
- `OwnerOnly`
- `FreshAuthRequired`
- `ProtectedOwnerRecovery`
- resource-based policies for conversations, records, sales, exports, etc.

Role checks alone are insufficient for:
- editing own sale
- conversation membership
- viewing employee record
- private communication inspection
- sensitive exports

## Fresh authentication

On successful password re-entry:
- create a short-lived fresh-auth assertion tied to user session
- default 15 minutes
- do not place reusable password in memory/storage longer than required
- revoke fresh-auth assertion when base session is revoked

## Master recovery credential

Separate from ordinary Owner password.

Store:
- strong one-way verifier/hash only

Never:
- log it
- display it after setup
- save plaintext
- store in appsettings.json

## TOTP

Protected Owner recovery TOTP secret:
- encrypted at rest using ASP.NET Core Data Protection
- recovery setup requires protected workflow
- validate with reasonable clock skew
- brute-force throttle

## Data Protection

Persist ASP.NET Core Data Protection key ring outside deployment folders.

Use for:
- TOTP secret encryption
- other small protected secret payloads
- antiforgery/cookie framework integrations

Do not use Data Protection as a replacement for password hashing.

## Private communication inspection

Owner-only.

Flow:
1. Owner chooses target and scope.
2. Reason required.
3. Fresh auth required.
4. protected verification step.
5. create permanent `private_communication_access` event before content is returned.
6. issue short access-session ID.
7. all reads during that access session are logged as child access metadata if desired.
8. expire access session quickly.

Content returned is current state only.
Deleted messages are not reconstructed.

## Audit retention classes

Define enum/config:
- Operational90Days
- Operational365Days
- AccountLifetime
- SevenYears
- Permanent
- Configurable

Examples:
- remote sync action: 90 days
- resource download audit: 365 days
- sensitive export: 7 years
- private communication access: Permanent
- emergency access: Permanent
- account timeline: Life of account, then removed with hard delete unless explicitly security-exempt

## Audit schema

Every audit event should be structured, not only text:

```json
{
  "action": "sales.historicalCorrection",
  "actorUserId": "...",
  "targetType": "Sale",
  "targetId": "...",
  "publicRecordId": null,
  "reason": "...",
  "before": {},
  "after": {},
  "occurredAtUtc": "...",
  "sessionId": "...",
  "deviceId": "...",
  "correlationId": "...",
  "retentionClass": "SevenYears"
}
```

## Sensitive exports

Server generates artifact.
Do not let browser reconstruct sensitive export from arbitrary client-side fetched data.

Export:
- requires reason + fresh auth
- creates EXP record
- artifact access requires authorization
- PDF watermark applied server-side
- re-download creates child audit

## Logging policy

Never log:
- passwords
- recovery credential
- TOTP secret/code
- auth cookie
- push subscription private auth secret in plaintext logs
- private message bodies as routine request logs
- uploaded file contents

Log:
- correlation ID
- route
- actor ID
- target ID
- outcome
- duration
- safe error class
