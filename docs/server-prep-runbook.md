# Server Prep Runbook — Windows Server 2019

Everything you can do on the production box **before** the application is
deployed, so the deployment wave is a drop-in. Follow it top to bottom; each
section ends with a check. Commands are PowerShell **run as Administrator**
unless noted.

Source of truth: `docs/08-windows-server-deployment.md` and CLAUDE.md §21.
Nothing here installs the app itself — that arrives with the deployment wave
through GitHub Actions.

---

## 0. Before you start, have on hand

- [ ] Administrator access to the Windows Server 2019 machine.
- [ ] The DNS name the hub will live at (e.g. `saleshub.yourcompany.com`)
      and a plan for its TLS certificate (public CA / Let's Encrypt via
      win-acme / internal CA — any is fine, it just must be trusted by the
      employees' browsers, or Idle Detection and the PWA will not work).
- [ ] A drive with room for the app + database + uploads + 7 daily backups.
      The runbook assumes `D:`; substitute if yours differs.
- [ ] Admin access to your **existing company server** — it hosts the
      encrypted backup share (ADR-003; this replaced the spec's Dropbox
      destination). Ideally it's reachable from the production box over
      SMB on the LAN.
- [ ] Admin access to the `countryboysplay/weed-man-sales-hub` GitHub repo
      (to register the self-hosted runner).

> **Windows Update first.** Fully patch the OS before anything else —
> the .NET hosting bundle and PostgreSQL both behave better on a current
> 2019 build, and you don't want a surprise reboot mid-deploy later.

---

## 1. IIS and required Windows features

```powershell
Install-WindowsFeature Web-Server, Web-WebSockets, Web-Mgmt-Console `
    -IncludeManagementTools
```

- `Web-WebSockets` is **not optional** — SignalR (chat, presence, live
  dashboards) rides on it.
- Do NOT install any `Web-Asp-Net*` features; the app runs on the ASP.NET
  Core Module, not classic ASP.NET.

Harden the defaults:

```powershell
# Directory browsing off everywhere (default site included)
Set-WebConfigurationProperty -Filter /system.webServer/directoryBrowse `
    -PSPath 'IIS:\' -Name enabled -Value $false

# Remove the Default Web Site — the hub gets its own site
Remove-Website -Name 'Default Web Site'
```

**Check:** `Get-WindowsFeature Web-WebSockets` shows `Installed`.

---

## 2. .NET 10 Hosting Bundle

Download the current **ASP.NET Core 10 Hosting Bundle** (not the SDK — the
server runs, it doesn't build) from
<https://dotnet.microsoft.com/download/dotnet/10.0> → "Hosting Bundle".

Install it **after** IIS (the bundle wires the ASP.NET Core Module into
IIS; installing in the wrong order means re-running the bundle installer),
then:

```powershell
net stop was /y
net start w3svc
```

**Check:** `& "$env:ProgramFiles\dotnet\dotnet.exe" --list-runtimes` lists
`Microsoft.AspNetCore.App 10.0.x`, and IIS Manager → Modules shows
`AspNetCoreModuleV2`.

> **Globalization note:** the app resolves the business time zone by its
> IANA id (`America/Chicago`), which .NET maps through ICU. Server 2019
> ships ICU, so this works out of the box — just never enable "Invariant
> Globalization" anywhere.

---

## 3. PostgreSQL 17

Production is **PostgreSQL 17** (the dev environment ran 16 only because of
a network restriction — the suite gets re-run against 17 before launch).

1. Install from the EDB installer (<https://www.postgresql.org/download/windows/>).
   - Install as a Windows service (default), superuser `postgres` with a
     strong generated password you store in your password manager.
   - Data directory on the data drive, e.g. `D:\PostgreSQL\17\data`.
   - Locale: default; port: 5432.
   - You do NOT need Stack Builder extras.
2. Keep it **local-only** (the app and DB share the box). In
   `D:\PostgreSQL\17\data\postgresql.conf` confirm:

   ```
   listen_addresses = 'localhost'
   password_encryption = scram-sha-256
   ```

3. Create the app's dedicated role and databases (production **and**
   staging — staging must be a separate DB per CLAUDE.md §20). In
   `psql -U postgres`:

   ```sql
   -- generate two strong passwords first; store them in the password manager
   CREATE ROLE saleshub LOGIN PASSWORD '<generated-prod-password>';
   CREATE DATABASE saleshub_prod OWNER saleshub;

   CREATE ROLE saleshub_staging LOGIN PASSWORD '<generated-staging-password>';
   CREATE DATABASE saleshub_staging OWNER saleshub_staging;
   ```

   The app's EF migrations run as the deploy step under these owners — no
   superuser in any connection string, ever.

4. `pg_hba.conf`: the installer's default host lines for `127.0.0.1/32`
   and `::1/128` with `scram-sha-256` are exactly right. No `0.0.0.0`
   lines.

**Check:** `psql -h 127.0.0.1 -U saleshub -d saleshub_prod` connects with
the new password; connecting from another machine fails.

---

## 4. Directory layout and permissions

The layout from docs/08 — releases are versioned and switched atomically;
data, keys, logs, and config live **outside** every release folder:

```powershell
$root = 'D:\WeedManSalesHub'
$dirs = @(
    "$root\releases",
    "$root\shared\appsettings",
    "$root\shared\dataprotection-keys",
    "$root\shared\logs",
    "$root\data\uploads",
    "$root\data\generated",
    "$root\data\reports",
    "$root\staging\shared\appsettings",
    "$root\staging\shared\dataprotection-keys",
    "$root\staging\shared\logs",
    "$root\staging\data\uploads",
    "$root\backups\daily"
)
$dirs | ForEach-Object { New-Item -ItemType Directory -Force -Path $_ | Out-Null }
```

Create the two IIS app pools now so their identities exist, then grant
least-privilege ACLs (app pool identities are virtual accounts named
`IIS AppPool\<name>`):

```powershell
Import-Module WebAdministration
New-WebAppPool -Name 'SalesHub'         # production
New-WebAppPool -Name 'SalesHubStaging'  # staging
Set-ItemProperty IIS:\AppPools\SalesHub -Name managedRuntimeVersion -Value ''
Set-ItemProperty IIS:\AppPools\SalesHubStaging -Name managedRuntimeVersion -Value ''

# Production identity: read releases, write only where it must
icacls "$root\releases"                    /grant 'IIS AppPool\SalesHub:(OI)(CI)RX'
icacls "$root\shared\appsettings"          /grant 'IIS AppPool\SalesHub:(OI)(CI)R'
icacls "$root\shared\dataprotection-keys"  /grant 'IIS AppPool\SalesHub:(OI)(CI)M'
icacls "$root\shared\logs"                 /grant 'IIS AppPool\SalesHub:(OI)(CI)M'
icacls "$root\data"                        /grant 'IIS AppPool\SalesHub:(OI)(CI)M'

# Staging identity: confined to the staging tree + read releases
icacls "$root\releases"                            /grant 'IIS AppPool\SalesHubStaging:(OI)(CI)RX'
icacls "$root\staging"                             /grant 'IIS AppPool\SalesHubStaging:(OI)(CI)M'
```

The staging pool must have **no** rights on `shared\` or `data\` —
staging never reads production storage or keys.

**Check:** `icacls D:\WeedManSalesHub\data` lists `IIS AppPool\SalesHub`
with modify, and `SalesHubStaging` is absent.

---

## 5. TLS certificate and IIS sites

1. Obtain the certificate for your DNS name and import it into the
   machine store (`Cert:\LocalMachine\My`). If you use win-acme
   (Let's Encrypt), it can bind and auto-renew for you.
2. Create the sites pointing at a placeholder folder for now (the deploy
   pipeline will retarget `current`):

```powershell
New-Item -ItemType Directory -Force -Path 'D:\WeedManSalesHub\releases\placeholder' | Out-Null
'<html><body>SalesHub – awaiting first deployment</body></html>' |
    Out-File 'D:\WeedManSalesHub\releases\placeholder\index.html' -Encoding utf8

New-Website -Name 'SalesHub' -ApplicationPool 'SalesHub' `
    -PhysicalPath 'D:\WeedManSalesHub\releases\placeholder' `
    -HostHeader 'saleshub.yourcompany.com' -Port 80

New-WebBinding -Name 'SalesHub' -Protocol https -Port 443 `
    -HostHeader 'saleshub.yourcompany.com' -SslFlags 1
# then attach the cert to the 443 binding in IIS Manager (or via netsh/win-acme)
```

3. Site-level settings, per docs/08:
   - **HTTP → HTTPS redirect**: install the IIS URL Rewrite module and add
     the standard redirect rule (or let win-acme add it). The app also
     enforces HSTS + redirection in Production as a second layer.
   - **Environment**: on the SalesHub site set the environment variable so
     the app boots in Production mode:

     ```powershell
     Set-WebConfigurationProperty -PSPath 'IIS:\Sites\SalesHub' `
       -Filter 'system.webServer/aspNetCore/environmentVariables' -Name '.' `
       -Value @{ name = 'ASPNETCORE_ENVIRONMENT'; value = 'Production' }
     ```

     (This lands in the release web.config at deploy time too; setting the
     expectation now costs nothing. Staging site: `Staging`.)
   - **Request limits**: cap request size to the allowed upload size
     (spec default 50 MB): IIS Manager → SalesHub → Request Filtering →
     Edit Feature Settings → Maximum allowed content length = `52428800`.
4. Repeat the site creation for **staging** (`SalesHubStaging` pool,
   `staging.saleshub.yourcompany.com` or an internal-only binding).
   Staging should NOT be reachable from outside your network — bind it to
   the LAN interface or restrict by firewall.

**Check:** `https://saleshub.yourcompany.com` serves the placeholder page
with a valid padlock, and plain `http://` redirects to it.

---

## 6. Production configuration file (secrets live here, not in Git)

Create `D:\WeedManSalesHub\shared\appsettings\appsettings.Production.json`.
The deploy pipeline links/copies it next to each release. Template:

```json
{
  "ConnectionStrings": {
    "SalesHub": "Host=127.0.0.1;Port=5432;Database=saleshub_prod;Username=saleshub;Password=<generated-prod-password>"
  },
  "DataProtection": {
    "KeyRingPath": "D:\\WeedManSalesHub\\shared\\dataprotection-keys"
  },
  "Storage": {
    "Root": "D:\\WeedManSalesHub\\data"
  },
  "Database": {
    "MigrateOnStartup": false
  },
  "Workers": {
    "Enabled": true
  },
  "WebPush": {
    "Subject": "mailto:jonathan.lindsay@weedmanusa.com",
    "PublicKey": "<generated at first deploy>",
    "PrivateKey": "<generated at first deploy>"
  },
  "Seed": {
    "Owner": {
      "Username": "<your owner username>",
      "DisplayName": "<your name>",
      "Password": "<strong one-time password — change it after first login>"
    }
  }
}
```

Notes:

- **VAPID keys** (`WebPush`) get generated once at first deploy and then
  never change (changing them invalidates every browser's push
  subscription). Leave the placeholders for now.
- The **Seed Owner** block provisions your initial account on first boot
  (there is no public registration). After you log in, change the
  password, set up the master recovery credential + TOTP in the Owner
  security screen, then **remove the Seed block** from this file.
- Lock the file down:

  ```powershell
  icacls 'D:\WeedManSalesHub\shared\appsettings\appsettings.Production.json' `
      /inheritance:r /grant 'IIS AppPool\SalesHub:R' /grant 'Administrators:F' /grant 'SYSTEM:F'
  ```

Create the staging equivalent under `staging\shared\appsettings` with the
staging connection string, staging paths, and NO seed block carrying real
credentials.

**Check:** `Get-Acl` on the file shows only SalesHub (read), Administrators
and SYSTEM.

---

## 7. Firewall

```powershell
# Inbound: only HTTP (for the redirect) and HTTPS
New-NetFirewallRule -DisplayName 'SalesHub HTTP'  -Direction Inbound -Protocol TCP -LocalPort 80  -Action Allow
New-NetFirewallRule -DisplayName 'SalesHub HTTPS' -Direction Inbound -Protocol TCP -LocalPort 443 -Action Allow
```

- Do **not** open 5432 (PostgreSQL is localhost-only).
- Do **not** open 5000/5001 (Kestrel is only reached through IIS).
- Public port forwarding beyond 443 for this app is explicitly out of
  scope (CLAUDE.md).
- Outbound stays open enough for: Windows Update, GitHub (runner), SMB
  (445) to the backup server only, and the browser push services
  (FCM/Mozilla/WNS endpoints — Web Push needs outbound HTTPS only).

**Check:** from another machine, `Test-NetConnection <server> -Port 5432`
fails; `-Port 443` succeeds.

---

## 8. GitHub Actions self-hosted runner

The deploy pipeline (Wave 7) runs on a runner installed on this box.

1. GitHub → `countryboysplay/weed-man-sales-hub` → Settings → Actions →
   Runners → **New self-hosted runner** → Windows x64, and follow the
   generated commands (they include a registration token) into
   `D:\actions-runner`.
2. Install it **as a service** when the config script asks, running as a
   dedicated local user (e.g. `.\svc-deploy`) — not SYSTEM, not your
   admin account. Grant that user:
   - Modify on `D:\WeedManSalesHub\releases` and both `shared\appsettings`
     folders (it stages releases and links config),
   - permission to recycle the two app pools
     (simplest: membership in local `IIS_IUSRS` plus a scoped right to run
     `appcmd`/`Import-Module WebAdministration` via the service — the
     Wave 7 pipeline docs will pin this down exactly),
   - NO database superuser rights: migrations run with the `saleshub`
     role's own credentials from the shared config.
3. Add a runner label `production` so workflows can target it explicitly.

**Check:** the runner shows green/"Idle" on the repo's Runners page and
survives a reboot (service start type Automatic).

---

## 9. Backup destination prep (existing company server — ADR-003)

Backups go to a share on your **existing server** instead of Dropbox
(ADR-003). The Wave 7 backup job (daily 12:30 AM America/Chicago, keep 7,
encrypted before leaving this box, DB + uploads consistent snapshot) needs
the destination ready:

1. **On the existing server**, create the backup folder and share:

   ```powershell
   New-Item -ItemType Directory -Force -Path 'E:\SalesHubBackups' | Out-Null
   New-SmbShare -Name 'SalesHubBackups$' -Path 'E:\SalesHubBackups' `
       -FullAccess 'Administrators' -Description 'Encrypted SalesHub backups'
   ```

   (The trailing `$` hides the share from casual browsing; adjust the
   drive letter to wherever the free space is.)
2. **A dedicated backup account**, e.g. local user `svc-saleshub-backup`
   on the existing server (or a domain account if both boxes are joined).
   Grant it access shaped to blunt ransomware on the production box:

   ```powershell
   # Share-level: change (write) but not full control
   Grant-SmbShareAccess -Name 'SalesHubBackups$' -AccountName 'svc-saleshub-backup' `
       -AccessRight Change -Force
   # NTFS: allow create/write, deny delete of existing content
   icacls 'E:\SalesHubBackups' /grant 'svc-saleshub-backup:(OI)(CI)(W,RD,X,RA)'
   icacls 'E:\SalesHubBackups' /deny  'svc-saleshub-backup:(OI)(CI)(DE,DC)'
   ```

   Pruning to the 7-day retention then runs as a scheduled task **on the
   backup server itself** (Wave 7 ships it), so a compromised production
   box can add backups but never destroy them. Store the account
   credential in the password manager; on the production box it will be
   saved for the backup task with `cmdkey /add:<backupserver>` at Wave 7.
3. **A backup encryption key** that is NOT stored on either server
   (CLAUDE.md §20: separate recovery/encryption key — this is what makes
   the plaintext-free share safe). Generate one now:

   ```powershell
   $bytes = New-Object byte[] 32
   [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
   [Convert]::ToBase64String($bytes)
   ```

   Print it / store it in the password manager and a second secure
   location. If the production box dies, this key + the share is your
   recovery path.
4. Confirm `pg_dump.exe` exists (it ships with PostgreSQL 17,
   `D:\PostgreSQL\17\bin` — add that to the system `PATH`).
5. **Site-risk reminder** (ADR-003): both machines share a building and a
   network. Put a quarterly reminder in your calendar to copy the latest
   verified backup to an offline/offsite medium — that replaces the
   geographic separation the original Dropbox plan provided.

**Check:** from the production box,
`Test-Path '\\<backupserver>\SalesHubBackups$'` is true when run as the
backup credential; writing a test file succeeds; deleting it is refused;
and the encryption key is stored in two places, neither of them a server.

---

## 10. Coexisting with SQL Server 2019

The production box also runs SQL Server 2019. The hub stays on
PostgreSQL 17 (the backend depends on PostgreSQL-specific features —
jsonb, xmin concurrency, `FOR UPDATE SKIP LOCKED` — and the spec mandates
it); the two coexist fine, **provided you fence resources**:

1. **Cap SQL Server's memory — do not skip this.** By default SQL Server
   grows to take nearly all RAM, which starves the OS file cache
   PostgreSQL depends on. In SSMS (or `sqlcmd`) against the instance:

   ```sql
   EXEC sp_configure 'show advanced options', 1; RECONFIGURE;
   EXEC sp_configure 'max server memory (MB)', 8192; RECONFIGURE;  -- adjust
   ```

   Sizing rule of thumb: from total RAM, reserve ~2 GB for Windows,
   ~1–2 GB for the hub app (Kestrel + workers), ~2–4 GB for PostgreSQL
   and its share of file cache, and give SQL Server a fixed cap out of
   what its actual workload needs — not the leftovers of an unlimited
   default. Example on a 32 GB box with a moderate SQL workload:
   SQL Server 16 GB, everything else keeps the other 16.
2. **Port check:** SQL Server (1433) and PostgreSQL (5432) don't collide.
   If the SQL instance is exposed to the LAN, that's its own firewall
   rule and risk decision — the hub's rules in §7 don't need to change.
3. **Stagger the night work.** SQL Server Agent maintenance/backup jobs
   often run at midnight–1 AM; the hub's backup runs at 12:30 AM
   America/Chicago and reports run on their schedules. Check the Agent
   job calendar and shift one side so the disk isn't hit by both at once.
4. **Disk accounting:** whatever drive holds SQL Server's data/backups,
   subtract it from what you counted as free for PostgreSQL, uploads,
   and the 7-day local backup staging.
5. **AV exclusions** (see below) should already cover SQL Server's data
   directories if AV runs on the box; add the PostgreSQL and hub paths
   alongside, not instead.
6. If the SQL Server instance is actually **unused** — worth checking —
   uninstalling it is the cleanest "configuration" of all.

**Check:** `SELECT value_in_use FROM sys.configurations WHERE name =
'max server memory (MB)'` returns your cap, not `2147483647`.

## 11. Odds and ends

- **Server clock**: enable NTP time sync (domain or `time.windows.com`).
  All storage is UTC; presence math depends on an accurate clock. The OS
  time zone setting itself doesn't matter to the app.
- **Antivirus exclusions** (if Defender or other AV runs): exclude the
  PostgreSQL data directory and `D:\WeedManSalesHub\data` from real-time
  scanning to avoid I/O stalls; keep scanning everything else.
- **RDP**: restrict to your admin network; this box will hold employee
  data.
- **Disk monitoring**: the readiness health check reports disk capacity,
  but set a Windows alert too — backups + uploads grow.
- **Do not pre-install the app or run migrations manually.** First
  migration and seeding happen through the deployment pipeline so PROD
  records and audit trails start clean.

---

## 12. Final verification checklist

| # | Check | Expected |
|---|---|---|
| 1 | `Get-WindowsFeature Web-Server, Web-WebSockets` | both Installed |
| 2 | `dotnet --list-runtimes` | `Microsoft.AspNetCore.App 10.0.x` |
| 3 | `psql -h 127.0.0.1 -U saleshub -d saleshub_prod` | connects |
| 4 | `psql` from another machine | refused |
| 5 | `https://saleshub.…` placeholder | valid cert, HTTP redirects |
| 6 | `icacls` on data/keys/config | only the intended identities |
| 7 | GitHub runner page | Idle, service auto-start |
| 8 | Port scan from LAN | only 80/443 (and your RDP rule) open |
| 9 | Backup share write-not-delete test + offline encryption key | write ok, delete refused, key stored safely |
| 10 | SQL Server `max server memory (MB)` | your cap, not 2147483647 |

When every row passes, the box is deploy-ready: the deployment wave only
adds the GitHub Actions workflow, the first release folder, VAPID keys,
and the backup scripts.
