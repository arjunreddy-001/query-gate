# Admin Cheat Sheet

One-page reference for Query Gate administrators. Full details: [USER-GUIDE.md](./USER-GUIDE.md).

For IIS, Azure App Service, Linux and Docker, see the [deployment guide](./DEPLOYMENT.md). The public `/documentation` page also provides deployment instructions.

The workspace caches object, column and foreign-key metadata using the metadata cache settings below. JOIN suggestions depend on the managed SQL account's metadata visibility and do not grant query access. Users can save their own editor preferences, including opt-in live syntax diagnostics, in their account.

## 1. Onboard a database and a user (5 steps)

| #   | Where                                     | Action                                                                                                                 |
| --- | ----------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| 1   | **Admin → Databases → Register database** | Name, configured environment, connection string of a dedicated service account, timeout, max rows |
| 2   | Same dialog                               | **Test connection**, then **Save** (the connection string is encrypted and never shown again)                          |
| 3   | **Admin → Users → New user**              | Email, display name (optional), role, optional expiry. **Copy the temporary password. It is shown once.**              |
| 4   | **Admin → Permissions**                   | Select the database, then **Add grant** (role or user, permission, Allow/Deny, optional window)                        |
| 5   | Tell the user to sign in                  | They set a new password on first sign-in, then use **Query workspace**                                                 |

Initial default grants added at registration (configurable under Permissions):

| Role      | Development                                          | QA                  | Production |
| --------- | ---------------------------------------------------- | ------------------- | ---------- |
| Developer | SELECT, INSERT, UPDATE, DELETE, EXECUTE, SP_HELPTEXT | SELECT              | none       |
| Tester    | none                                                 | SELECT, SP_HELPTEXT | none       |

## 2. Permission rules

- Add or edit roles and environments in **Permissions**; Admin is protected. New roles have standard user pages but no grants or audit capability. Role renames preserve memberships and grants; members must sign in again when sessions are revalidated.
- Enable a role's audit capability to allow the Audit log and Access matrix. The initial Auditor role has this capability.
- Use an environment's **Defaults** to set allow grants per role for future database registrations only. Changing defaults or a registered database's environment leaves existing grants unchanged. Environment renames update associated databases without changing their grants.
- Delete a role only after removing its users, database grants and defaults. Delete an environment only after removing its databases and defaults.
- **Granular object scoping**: Grants can target the entire database (`*`), a whole schema (`sales.*`), or a specific table, view, or stored procedure (`dbo.Customers`). Specific object rules override schema rules, which override database-wide rules.
- User rule beats role rule. **Deny beats Allow** at the same level. No rule means denied.
- Time windows are checked when the query runs.
- Dangerous permissions (CREATE, ALTER, DROP, TRUNCATE, DYNAMIC_SQL, LINKED_SERVER, XP_CMDSHELL, `SYSPROC:*`): grant sparingly. They are required even for approved scripts.
- `EXPORT` is required for downloading query results in all formats (CSV, Excel, JSON, Markdown, SQL INSERT).
- `UNMASK` is required to view unmasked PII and sensitive data. Users without `UNMASK` receive in-flight masked data for emails, phone numbers, SSNs, credit cards, salaries, and secrets.
- Allow one system procedure with `SYSPROC:name`.

## 3. Daily tasks

| Task                                   | Where                   | How                                                                                                           |
| -------------------------------------- | ----------------------- | ------------------------------------------------------------------------------------------------------------- |
| Approve access                         | Approvals → Access      | Review, adjust duration, Approve. Reject needs a comment. Grants are created automatically and expire on time |
| Approve a script                       | Approvals → Scripts     | Review the gate analysis, edit if needed, tick _Run automatically_ or press **Run now** later                 |
| Reset a password                       | Users                   | **Reset pwd**, then copy the one-time password                                                                |
| Offboard someone                       | Users                   | **Deactivate**. This ends their sessions, revokes tokens and cancels their running queries                    |
| Offboard many                          | Users                   | Tick the users, then **Deactivate selected**                                                                  |
| Temporary account                      | Users → Edit            | Set an account expiry                                                                                         |
| Slow-query limit for one user          | Users → Edit            | Timeout override (seconds)                                                                                    |
| Change a connection string             | Databases → Edit        | Enter the new one. Leave blank to keep the current one                                                        |
| Rename a database / change environment | Databases → Edit        | Update Name and Environment, then Save                                                                        |
| Include disabled databases in list     | Databases               | Tick **Show disabled**                                                                                        |
| Disable a database                     | Databases → Edit        | Untick _Enabled_                                                                                              |
| Test a registered database quickly     | Databases               | Click **Check** on the database row to probe connection and latency                                           |
| Monitor database health and latency    | Databases               | Live connectivity, latency, KPI summary cards, and diagnostics are integrated directly on the Databases page  |
| Time-boxed access                      | Permissions → Add grant | Set start and end times                                                                                       |
| Review who can do what                 | Access matrix           | Pick a database, then **Export CSV**                                                                          |
| Investigate activity                   | Audit log               | Filter by user, outcome or date, expand **SQL**, **Export CSV**                                               |
| Trace a user error                     | Application logs        | Paste the correlation ID the user reports                                                                     |

## 4. Settings (Admin → Settings)

The Settings page is organized into five tabs: **General & Limits**, **Email & SMTP**, **Webhooks & Alerts**, **Data Masking**, and **Metadata Cache**.

| Setting                                    | Default      |
| ------------------------------------------ | ------------ |
| Default query timeout / maximum ceiling    | 30 s / 300 s |
| Default row limit                          | 1000         |
| Concurrent queries per user / per database | 3 / 10       |
| Allow self-approval                        | No           |
| History kept per user                      | 100          |
| Expiry reminder                            | 24 h before  |
| Object metadata cache (on/off) / TTL       | On / 10 min  |
| Temporary password length                  | 16           |
| Dynamic Data Masking (on/off)              | On           |
| Additional roles for DB health             | (blank, Admin only) |

**Metadata cache:** the _Object metadata cache_ card on the Settings page shows what is cached per database, with the environment in a separate column. Use _Enable/Disable_, per-database _Reload_ / _Clear_, or _Reload all_ / _Clear all_. All actions are audited.

**Email & SMTP:** configure outgoing SMTP server (host, port, SSL/TLS, username, encrypted password, sender address) directly in the UI. Use _Send test email_ to verify delivery immediately. Falls back to environment/appsettings if unconfigured in the UI.

**Collaboration Webhooks:** add webhooks for Slack, Microsoft Teams, Zoho Cliq, Discord, or generic endpoints. Supports event filters (all, access/script requests only, script executions only, or database health alerts only) and instant _Test_ dispatch.

**Data Masking:** manage dynamic data masking rules directly in the UI with HIPAA Safe Harbor, GDPR, PCI-DSS, SOC 2, and CCPA compliance. Filter rules by Origin (All, Built-in, Custom) and regulatory standards. Configure column patterns, select masking strategies (`Name`, `MedicalRecord`, `DateOfBirth`, `Address`, `ZipCode`, `Email`, `CreditCard`, `Ssn`, `Phone`, `IpAddress`, `Salary`, `Secret`, or `CustomText` with custom strings like `[CONFIDENTIAL]`), enable/disable rules or global masking, add/edit rules, test pattern matching with instant live preview, or reset to defaults. Includes security confirmation modals before destructive actions (deleting rules, disabling global masking, resetting to defaults). All administrative actions across the app (grants, approvals, user lifecycle, settings) report instant feedback through highlighted top-center floating toast notifications with animated drain timers. All rule updates are audited.

## 5. Who can see what

| Page                                                       | Admin | Auditor | Developer / Tester |
| ---------------------------------------------------------- | :---: | :-----: | :----------------: |
| Approvals, Users, Permissions, Logs, Settings              |   ✔   |         |                    |
| Databases (management / health monitoring)                 |   ✔   | Config  |       Config       |
| Audit log, Access matrix                                   |   ✔   |    ✔    |                    |
| Query workspace, My access, Script requests, Notifications |   ✔   |    ✔    |         ✔          |

Audit and Access matrix access is configurable per role; Databases access defaults to Admin and SuperAdmin and can be extended to additional roles in read-only monitoring mode via the HealthVisibleRoles setting; the Auditor column describes the initial configuration.

## 6. Rules of thumb

- Use a **separate least-privilege SQL account per database**. The tool can never do more than that account can.
- Keep **Production** grants explicit and time-boxed. There are no Production defaults in the initial configuration.
- Never share the temporary password over chat history you don't control.
- Leave **self-approval off** unless you have a single admin.
- Set `Encryption:Key` and `Jwt:Key` in production and back the encryption key up. Losing it makes saved connection strings unreadable.
- All delete and remove actions (revoking grants, deleting roles/environments, deactivating accounts, removing webhooks, clearing cache, deleting saved queries, cancelling requests) require explicit confirmation dialogs to prevent accidental loss.
- The audit log is written **before** a query runs. If auditing is down, queries are refused. This is intended.

## 7. Quick fixes

| Problem                                            | Fix                                                                                                    |
| -------------------------------------------------- | ------------------------------------------------------------------------------------------------------ |
| User says "permission denied"                      | Check **Access matrix** for the database, then add a grant or approve their request                    |
| Cannot create duplicate database names across envs | Allowed now. Only the **Name + Environment** pair must be unique                                       |
| User locked out                                    | Locks clear after 15 minutes. Check they aren't Inactive or Expired in Users                           |
| Test connection fails with a certificate error     | Add `TrustServerCertificate=True`                                                                      |
| Approved script didn't run                         | It was edited (auto-run is off) or lacks a dangerous permission. Use **Run now** or add the permission |
| Lost the admin password                            | Use another admin's **Reset pwd**, or restart with new `BootstrapAdmin__*` values for a new admin      |

## 8. Multi-Tenancy and Workspaces

### Architecture Overview
- **Path-Prefix**: Workspaces are reached at `/t/[slug]/` (for example, `/t/acme/dashboard`).
- **Custom Domains**: Point customer CNAME or A records to the server, then add the domain in the platform UI to enable direct routing without path prefixes.
- **Tenant Isolation**: Every database, audit entry, setting, and permission grant is isolated using row-level filters (`TenantId`).
- **Workspace Switching**: Use the topbar workspace dropdown or visit `/select-tenant` to switch between organizations.

### SuperAdmin Role Setup
The `SuperAdmin` role enables platform-wide workspace management at `/platform/tenants`. Configure it using one of two methods:

1. **Bootstrap Configuration (Recommended)**:
   Set `BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD` (or `BootstrapAdmin__Email` and `BootstrapAdmin__Password`). On startup, `Program.cs` automatically creates the account or promotes an existing account to `SuperAdmin` and `Admin`.
2. **Direct PostgreSQL SQL Assignment**:
   When immediate elevation is required without restarting the server:
   ```sql
   INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
   VALUES (gen_random_uuid()::text, 'SuperAdmin', 'SUPERADMIN', gen_random_uuid()::text)
   ON CONFLICT ("NormalizedName") DO NOTHING;

   INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
   SELECT u."Id", r."Id"
   FROM "AspNetUsers" u
   JOIN "AspNetRoles" r ON r."NormalizedName" = 'SUPERADMIN'
   WHERE u."NormalizedEmail" = UPPER('admin@company.com')
   ON CONFLICT DO NOTHING;
   ```
   (Log out and log back in to refresh claims).

### Tenant Provisioning & Onboarding Workflow
- **Creating a Workspace**: SuperAdmins open `/platform/tenants`, click **Create Workspace**, provide name, slug (lowercase alphanumeric and hyphens), contact email, plan, and an initial administrator email.
- **Adding Custom Domains**: Click **Domains** on the tenant row in `/platform/tenants`, enter the hostname (e.g. `sql.acme.com`), and point DNS to the server.
- **Adding Users to Workspaces**: Workspace admins open **Admin -> Users** (`/admin/users`), click **New user**, enter email and workspace role (`Admin`, `Developer`, `Auditor`, `Tester`). If the user already exists globally, they are joined to the workspace without email conflicts; new users receive a generated temporary password.
