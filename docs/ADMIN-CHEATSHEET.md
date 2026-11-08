# Admin Cheat Sheet

One-page reference for Query Gate administrators. Full details: [USER-GUIDE.md](./USER-GUIDE.md).

For IIS, Azure App Service, Linux and Docker, see the [deployment guide](./DEPLOYMENT.md). The public `/documentation` page also provides deployment instructions.

## 1. Onboard a database and a user (5 steps)

| #   | Where                                     | Action                                                                                                                 |
| --- | ----------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| 1   | **Admin → Databases → Register database** | Name, environment (Development / QA / Production), connection string of a dedicated service account, timeout, max rows |
| 2   | Same dialog                               | **Test connection**, then **Save** (the connection string is encrypted and never shown again)                          |
| 3   | **Admin → Users → New user**              | Email, display name (optional), role, optional expiry. **Copy the temporary password. It is shown once.**              |
| 4   | **Admin → Permissions**                   | Select the database, then **Add grant** (role or user, permission, Allow/Deny, optional window)                        |
| 5   | Tell the user to sign in                  | They set a new password on first sign-in, then use **Query workspace**                                                 |

Default grants added at registration:

| Role      | Development                                          | QA                  | Production |
| --------- | ---------------------------------------------------- | ------------------- | ---------- |
| Developer | SELECT, INSERT, UPDATE, DELETE, EXECUTE, SP_HELPTEXT | SELECT              | none       |
| Tester    | none                                                 | SELECT, SP_HELPTEXT | none       |

## 2. Permission rules

- User rule beats role rule. **Deny beats Allow** at the same level. No rule means denied.
- Time windows are checked when the query runs.
- Dangerous permissions (CREATE, ALTER, DROP, TRUNCATE, DYNAMIC_SQL, LINKED_SERVER, XP_CMDSHELL, `SYSPROC:*`): grant sparingly. They are required even for approved scripts.
- `EXPORT` is required for CSV download.
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
| Test a registered database quickly     | Databases               | Click **Test** on the database row                                                                            |
| Time-boxed access                      | Permissions → Add grant | Set start and end times                                                                                       |
| Review who can do what                 | Access matrix           | Pick a database, then **Export CSV**                                                                          |
| Investigate activity                   | Audit log               | Filter by user, outcome or date, expand **SQL**, **Export CSV**                                               |
| Trace a user error                     | Application logs        | Paste the correlation ID the user reports                                                                     |

## 4. Settings (Admin → Settings)

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

**Metadata cache:** the _Object metadata cache_ card on the Settings page shows what is cached per database. Use _Enable/Disable_, per-database _Reload_ / _Clear_, or _Reload all_ / _Clear all_. All actions are audited.

## 5. Who can see what

| Page                                                       | Admin | Auditor | Developer / Tester |
| ---------------------------------------------------------- | :---: | :-----: | :----------------: |
| Approvals, Users, Databases, Permissions, Logs, Settings   |   ✔   |         |                    |
| Audit log, Access matrix                                   |   ✔   |    ✔    |                    |
| Query workspace, My access, Script requests, Notifications |   ✔   |    ✔    |         ✔          |

## 6. Rules of thumb

- Use a **separate least-privilege SQL account per database**. The tool can never do more than that account can.
- Keep **Production** grants explicit and time-boxed. There are no default Production grants.
- Never share the temporary password over chat history you don't control.
- Leave **self-approval off** unless you have a single admin.
- Set `Encryption:Key` and `Jwt:Key` in production and back the encryption key up. Losing it makes saved connection strings unreadable.
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
