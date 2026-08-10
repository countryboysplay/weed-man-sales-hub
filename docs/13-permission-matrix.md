# 13 — Permission Matrix

Legend:
- Y = allowed
- N = denied
- Own = only own resource
- Summary = limited summary only
- Protected = Owner + protected security flow
- Config = only where a configured setting delegates the action

| Capability | Sales Agent | Sales Supervisor | Sales Manager | Owner |
|---|---:|---:|---:|---:|
| Use Sales Dashboard | Y | Y | Y | Y |
| Add own sale | Y | Y | Y | Y |
| Edit/delete own same-day sale | Own | Own | Own | Own |
| Correct historical sale | N | Y | Y | Y |
| View another agent sales summary | N | Y | Y | Y |
| Export own sales | Own | Own | Own | Own |
| DM any active user | Y | Y | Y | Y |
| Participate in groups | Y | Y | Y | Y |
| Create/manage groups | N | Y | Y | Y |
| Inspect private communications as nonparticipant | N | N | N | Protected |
| Publish announcements | N | Y | Y | Y |
| View management announcement progress | N | Y | Y | Y |
| Create/assign tasks | N | Y | Y | Y |
| Build/publish forms | N | Y | Y | Y |
| Manage resources/folders | N | Y | Y | Y |
| Download manager-allowed resources | N | Y | Y | Y |
| Issue recognitions | N | Y | Y | Y |
| Manage badge library | N | Y | Y | Y |
| View directory | Y | Y | Y | Y |
| Add/remove/deactivate ordinary users | N | Y | Y | Y |
| Reset employee password | N | Y | Y | Y |
| Force employee logout | N | Y | Y | Y |
| Force Owner logout | N | N | N | Y |
| Promote/demote Owner | N | N | N | Protected |
| View own current-day presence | Own | Own | Own | Own |
| View team live presence | N | Y | Y | Y |
| View serious presence alert summary | N | Summary | Y | Y |
| View detailed presence history | N | N | Y | Y |
| Approve time off | N | Y | Y | Y |
| Approve schedule exception | N | Y | Y | Y |
| Approve break correction | N | Y | Y | Y |
| Grant technical grace | N | Y | Y | Y |
| Add management note | N | Y | Y | Y |
| View management note | N | Y | Y | Y |
| Sensitive employee-history export | N | N | Y | Y |
| View support normal queue | N | Y | Y | Y |
| Advanced support diagnostics | N | N | Y | Y |
| View System Health summary | N | Y | Y | Y |
| View Sync Health | N | Y | Y | Y |
| Remote resync/refresh/cache-clear | N | Y | Y | Y |
| Manage ordinary settings delegated to management | N | Config | Config | Y |
| Owner/System security settings | N | N | N | Y |
| Manage feature controls | N | N | N | Y |
| Schedule maintenance | N | N | N | Y |
| Backup restore | N | N | N | Protected |
| Report-only backup recovery | N | N | N | Protected |
| Refresh staging from production | N | N | N | Protected |
| Emergency access | N | N | N | Protected |
| Production launch final approval | N | N | N | Protected |
| Rollback production | N | N | N | Protected |
| View permanent security audit | N | N | N | Y |

## Authorization implementation rule

Do not infer permissions from UI visibility.

Every API endpoint and SignalR server method must have a server-side policy/resource authorization test.

For resource ownership, use resource-based authorization:
- own sale
- conversation membership
- own task instance
- own time-off request
- own notification
- management target employee

For protected Owner actions, simple `[Authorize(Roles="Owner")]` is insufficient. The application service must validate:
- active Owner session
- fresh-auth assertion
- reason
- master recovery verifier where required
- TOTP where required
- emergency-session scope if action is being done under emergency access
