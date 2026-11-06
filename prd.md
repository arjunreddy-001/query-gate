# PRD: DB Access Manager (Internal Tool)

## 1. Overview
**Problem:** Developers and testers need controlled, auditable access to multiple MS SQL databases. Access is currently granted ad hoc (shared logins, manual grants), with no audit trail, expiry, or central visibility.

**Solution:** A web-based internal tool that brokers all database access. Users authenticate to the tool (not the DB), run queries via an SSMS-like UI, and the tool enforces granular permissions, logs every execution, and supports request/approval workflows and time-bound access.

**Goals**
- Least-privilege, centrally managed DB access with full auditability.
- Productive query UI (tabs, autocomplete, saved queries, formatting).
- Self-service access/script requests with admin approval.

**Non-goals (v1):** DBs other than MS SQL; replacing SSMS for DBA tasks (backup, profiler, SSIS); SSO/SAML (planned v2); query plan visualization.

## 2. Personas
| Persona | Needs |
|---|---|
| Developer | Run SELECT/DML on dev/QA DBs, save queries, request elevated access |
| Tester | Read-mostly access, run data-verification queries, request scripts |
| Admin | Onboard/offboard users, manage DBs/roles/permissions, approve requests, review audit |
| Auditor (optional role) | Read-only access to audit and access reports |

## 3. Functional Requirements

### 3.1 Authentication & Users (FR 12, 13)
- FR-1: Email/username + password login; passwords hashed with Argon2id/bcrypt; password policy (length, complexity), forced change on first login, admin-initiated reset.
- FR-2: JWT access token (15 min) + refresh token (rotating, 8 h, revocable, stored hashed). Logout revokes refresh token.
- FR-3: Account lockout after N failed attempts (configurable); login attempts audited.
- FR-4: Onboarding: admin creates user (name, email, roles, initial DB access, optional expiry); temp password generated.
- FR-5: Offboarding: deactivate user → immediately invalidates all tokens, cancels running queries, blocks login; data/audit retained. Reactivation supported. Optional bulk deactivate and "account expiry date".
- FR-6: Inactive/disabled status checked on every request (not only at token issue).

### 3.2 Databases (FR 14)
- FR-7: Admin registers a database connection (name, environment tag: Development/QA; Prod is out of scope for now, description, connection string) after deployment via UI.
- FR-8: Connection strings encrypted at rest (AES-256; key from env/secret store/KMS); never returned to the UI after save; "Test connection" action.
- FR-9: Databases can be enabled/disabled, default query timeout and max row limit set per database.
- FR-10: Tool connects with a service login per database; user-level restrictions are enforced by the tool's SQL permission gate (see 3.4). Recommend least-privilege service account per DB.

### 3.3 Roles & Permissions (FR 1, 2, 3)
- FR-11: Roles (e.g., Developer, Tester, Admin, custom) hold permission sets per database. A user may hold multiple roles and have access to multiple databases.
- FR-12: Permission catalog (granular, per DB): SELECT, INSERT, UPDATE, DELETE, EXECUTE (procs), CREATE, ALTER, DROP, TRUNCATE, sp_helptext, VIEW DEFINITION, sp_help, and "allowed system procedures" list. Optional scope: schema or object-level allow/deny lists (v1.1).
- FR-13: Grants at **role level** and **user level**. Each is Allow or Deny.
- FR-14: **Resolution precedence:** user-level rule > role-level rule > default deny. Within a level, explicit Deny beats Allow; among multiple roles, Deny wins over Allow. A user-level Allow overrides a role-level Deny and vice versa.
- FR-15: Effective-permission view per user per database showing source (user/role) of each permission.
- FR-16: Grant/revoke changes take effect immediately (no re-login) and are audited.

### 3.4 Query Execution & Enforcement (FR 4, 9, 5)
- FR-17: Execute ad hoc queries and multi-statement scripts (including `GO` batch separators), and upload `.sql` files.
- FR-18: **Permission gate:** parse each batch using a T-SQL parser (e.g., ScriptDom/ `tsql` parser) to classify statements and required permissions. Unparseable or unknown statements are denied by default. Handle dynamic SQL (`EXEC(...)`, `sp_executesql`) as denied unless EXECUTE-dynamic permission is granted. Block bypass patterns (comments, nested procs, `xp_cmdshell`, linked-server/OPENROWSET unless explicitly granted).
- FR-19: Configurable timeout: system default, per-database default, per-user/role override, per-run override in UI up to a max ceiling. Query cancelled server-side on timeout; user can also Cancel a running query.
- FR-20: Row limit on result sets (default 1,000, configurable) with "truncated" indicator; export results to CSV/Excel (permission-controlled).
- FR-21: Display multiple result sets, messages, rows affected, execution time, errors with line numbers.
- FR-22: Optional safeguards: confirmation prompt for UPDATE/DELETE without WHERE; auto-wrap in transaction with explicit Commit/Rollback option ("dry run" rollback); optional "read-only mode" toggle.
- FR-23: Concurrency limits per user and per DB to protect servers.

### 3.5 Audit & Logging (FR 5, 16)
- FR-24: Audit record per execution: user, roles, DB, full SQL text, statement types, outcome (success/denied/error/timeout/cancelled), rows affected/returned, duration, timestamp, client IP, user agent, request ID, linked approval/script request if any.
- FR-25: Also audit: login/logout/failures, user/role/grant changes (before/after), access requests/approvals, DB config changes, exports.
- FR-26: Audit is append-only (no edit/delete via app); searchable/filterable (user, DB, date, status, text) and exportable; retained indefinitely (no retention/compliance policy required).
- FR-27: Application error log: structured logs (level, message, stack trace, correlation ID, user, endpoint); admin UI viewer with filters; correlation ID shown to users on error. Sensitive data (passwords, tokens, connection strings) never logged.

### 3.6 Query Workspace UX (FR 6, 7, 8, 10, 11)
- FR-28: SSMS-like tabs: multiple query tabs, each bound to a database, rename/reorder/close, unsaved-changes indicator, tab state restored after refresh. Run selection only (F5/Ctrl+Enter), run all, cancel.
- FR-29: Monaco-based editor: T-SQL syntax highlighting, line numbers, find/replace, themes (light/dark).
- FR-30: Autocomplete for tables, views, columns, procs, schemas, and keywords based on the selected DB metadata; schema-aware (alias.column resolution); only objects the user is permitted to see; metadata cached and refreshable.
- FR-31: Object explorer sidebar (tables, views, procs, columns, types) with quick actions (SELECT TOP 100, view definition if permitted).
- FR-32: Saved queries: up to **50 per user per database** (enforced server-side, clear message at limit); name, tags, description; search, edit, delete; optional share to a team (v1.1).
- FR-33: One-click copy of query text (editor and history entries).
- FR-34: SQL formatter (button + shortcut, e.g., Shift+Alt+F) with configurable style (keyword case, indentation).
- FR-35: Personal query history (last N executions) with re-run/copy.

### 3.7 Access Request Workflows (FR 17, 18, 19, 20)
- FR-36: **Access request (self-service):** user selects DB, permissions needed, justification, duration (or permanent), optional ticket reference. Routed to admin(s)/approver(s); approve/reject/modify with comments; notifications by SMTP email, plus in-app notifications shown to the user on login (unread badge/notification center). Applies to all workflow events (request submitted/approved/rejected, access expiring, script executed).
- FR-37: **Temporary / time-bound access:** any grant (user-level or role-level, or via request) may have start and end time. Expiry enforced at evaluation time (not only by a job) plus a background job that cleans up and notifies. Reminders before expiry; extension requests supported.
- FR-38: **Script execution request:** user submits a script against a DB, with description/justification. Admin reviews (syntax highlighting, detected statement types, diff-friendly view), approves/rejects, and approves. **Execution rule:** if the admin approves the script unchanged, the system runs it automatically (option on approval; audit records requester, approver and executor=System); if the admin edits the script during review, auto-run is disabled, the modified script is stored as a new version (original kept), and an admin must run it manually. Both versions are retained. Result and audit linked to the request. Approver cannot approve own request (configurable). Requester is notified of status.
- FR-39: Request statuses: Pending, Approved, Rejected, Cancelled, Expired, Executed, Failed. Full status history.
- FR-40: **Centralized access view:** admin page showing matrix of users × databases with effective permissions, source (role/user/request), and expiry; filter by DB, user, role, environment, expiring soon; export to CSV. Also per-database "who has access" and per-user "what they can access".
- FR-41: Periodic access review: admin-triggered/scheduled recertification campaign listing access to confirm or revoke (v1.1).

### 3.8 Additional Productivity Features (FR 21)
**v1:** query history; result export; object explorer; dark mode; keyboard shortcuts; environment color-coding (Dev/QA badges); notifications center; dashboard (my access, pending requests, expiring access); session timeout warning.
**v1.1+:** query snippets/templates and shared team library; execution plan (estimated) viewer; result grid sorting/filtering/column resize; compare results; scheduled read-only queries; break-glass emergency access with mandatory justification and elevated audit; data masking for sensitive columns; anomaly alerts (bulk deletes, off-hours prod access); SSO (Azure AD/OIDC) and MFA/TOTP; REST API/CLI.

## 4. Non-Functional Requirements
- **Security:** HTTPS only; secrets encrypted; parameterized queries for the tool's own DB; RBAC on every endpoint; rate limiting; antiforgery/XSS protections (CSP, refresh token in httpOnly secure cookie); deployed inside VPN only, HTTPS still required; OWASP Top 10 review; dependency scanning; SQL-gate tests against bypass attempts.
- **Performance:** UI responses <300 ms for non-query endpoints (p95); autocomplete <150 ms from cache; supports ~50 users, 4–7 databases, ~20 concurrent query sessions.
- **Reliability:** Query cancel is reliable; connection pooling per DB; graceful handling of unavailable DBs; health endpoint.
- **Auditability:** Audit write is part of execution path; if audit write fails, execution is blocked (fail-closed) for write operations.
- **Scalability/Deployment:** Hosted on IIS (Windows Server) with ASP.NET Core Hosting Module; config via appsettings/environment variables; EF Core migrations applied at deploy; single instance is sufficient (data-protection keys persisted to disk/DB).
- **Compatibility:** MS SQL Server 2016+, Azure SQL; latest Chrome/Edge/Firefox.
- **Accessibility/Usability:** Keyboard-first operation; responsive layout for desktop.
- **Backup/Retention:** Tool DB backup guidance. No retention or compliance requirements (internal, VPN-only); logs kept indefinitely, optional manual purge.

## 5. Proposed Architecture
- **Frontend:** Blazor (Server or Interactive Server render mode, .NET 8+) with MudBlazor/Radzen components; BlazorMonaco for the editor; sql formatting via ScriptDom generator or a JS sql-formatter via JS interop.
- **Backend:** ASP.NET Core Web API (C#; Microsoft.SqlServer.TransactSql.ScriptDom for parsing, native fit for MSSQL) Same solution as the Blazor app (single full-stack .NET project with a service layer; REST endpoints optional). Auth via JWT (also used for any API calls) with cookie-backed session for Blazor Server.
- **Tool DB:** MS SQL Server (separate from managed DBs), EF Core migrations.
- **Background jobs:** Hangfire/Quartz (hosted services) for expiry, email sending, cleanup.
- **Logging:** Serilog → tool DB + file; optional sink to central logging.
- **Components:** Auth service, Access/Permission engine, SQL gate/parser, Execution engine (pooling, timeout, cancel), Metadata service (schema cache), Request workflow service, Audit service, Notification service.

## 6. Data Model (Tool DB, key tables)
- `Users` (Id, Username, Email, PasswordHash, IsActive, MustChangePassword, FailedAttempts, LockoutEnd, ExpiresAt, CreatedBy/At)
- `Roles` (Id, Name, Description, IsSystem)
- `UserRoles` (UserId, RoleId, StartsAt, EndsAt)
- `Databases` (Id, Name, Environment, EncryptedConnectionString, DefaultTimeoutSec, MaxRows, IsActive)
- `PermissionTypes` (Id, Code e.g. SELECT, DROP, SP_HELPTEXT)
- `RolePermissions` (RoleId, DatabaseId, PermissionTypeId, Effect[Allow/Deny], StartsAt, EndsAt)
- `UserPermissions` (UserId, DatabaseId, PermissionTypeId, Effect, StartsAt, EndsAt, SourceRequestId)
- `AccessRequests` (Id, RequesterId, DatabaseId, Permissions, Justification, Ticket, Duration, Status, ReviewerId, ReviewedAt, Comments)
- `ScriptRequests` (Id, RequesterId, DatabaseId, ScriptText, Justification, Status, ReviewerId, ExecutedAt, ResultSummary, AuditId)
- `SavedQueries` (Id, UserId, DatabaseId, Name, SqlText, Tags, CreatedAt, UpdatedAt) — unique, count ≤ 50 per (UserId, DatabaseId)
- `QueryHistory`, `AuditLogs` (append-only), `AppLogs`, `RefreshTokens`, `Notifications`, `Settings`.
- Indexes on AuditLogs (UserId, DatabaseId, Timestamp).

## 7. Key API Surface (REST, `/api/v1`)
`/auth/login|refresh|logout|change-password` · `/users` (CRUD, activate/deactivate) · `/roles` · `/databases` (+`/test`, `/metadata`) · `/permissions/user|role` · `/access/effective`, `/access/matrix` · `/query/execute|cancel` · `/saved-queries` · `/history` · `/access-requests` · `/script-requests` · `/audit` · `/logs` · `/notifications` · `/settings`.

## 8. Screens
Login · Dashboard · Query Workspace (tabs, explorer, editor, results, saved/history panel) · My Access & Requests · Request Access · Script Requests (submit/review) · Admin: Users, Roles, Databases, Permissions, Access Matrix, Approvals, Audit Log, App Logs, Settings.

## 9. Default Role Seeds
- **Admin:** manage everything; all permissions via admin screens.
- **Developer:** SELECT, INSERT, UPDATE, DELETE, EXECUTE, sp_helptext on Dev DBs; SELECT on QA.
- **Tester:** SELECT, sp_helptext on QA/UAT; limited UPDATE on test data.
- QA: Developers get elevated access only via request/time-bound grants. Prod is out of scope.

## 10. Success Metrics
- 100% of query executions audited.
- 0 shared DB credentials among users.
- ≥80% access changes via self-service workflow; median approval time <1 business day.
- Time-bound grants auto-expire with 0 lingering access.
- User satisfaction ≥4/5 on query workspace.

## 11. Release Plan
- **Phase 1 (MVP, ~8–10 wks):** auth/JWT, users on/offboarding, DBs, roles, user/role permissions with precedence, SQL gate, query workspace (tabs, autocomplete, format, copy, saved queries, timeout), audit and error logs.
- **Phase 2 (~4–6 wks):** access requests, script requests, time-bound access and expiry jobs, access matrix, notifications, history, export.
- **Phase 3:** access reviews, break-glass, MFA/SSO, masking, anomaly alerts, API/CLI.

## 12. Risks & Mitigations
| Risk | Mitigation |
|---|---|
| SQL gate bypass (dynamic SQL, nested procs) | AST-based parsing, default deny, security test suite; DB-side least-privilege service accounts as defense in depth |
| Connection string leak | Encryption, no read-back, secret store, log scrubbing |
| Long/heavy queries affect servers | Timeouts, row limits, concurrency caps, cancellation |
| Audit tampering | Append-only, restricted DB permissions, optional hash chaining |
| Stale access | Expiry enforcement at evaluation time, reviews |
| Metadata cache stale | Manual refresh, TTL |

## 13. Decisions (resolved open questions)
1. **DB connectivity:** one service account per managed database; all per-user restrictions are enforced by the tool's SQL permission gate.
2. **Script requests:** auto-run option when approved unchanged; manual admin run if the script was modified during approval.
3. **Notifications:** SMTP email plus in-app notifications shown on login.
4. **Retention/compliance:** none required (internal, VPN-only).
5. **Scale/scope:** ~50 users, 4–7 databases, Development and QA only (no Prod).
6. **Stack/hosting:** .NET full-stack with Blazor, MS SQL Server tool DB, hosted on IIS.

## 14. Additional Decisions
1. **Blazor Server** (interactive server render mode) is the UI model.
2. **SMTP settings** (host, port, SSL, credentials, sender address) are configurable in `web.config` / app settings, not hard-coded; credentials stored encrypted or via environment variables.
3. **Auto-run scripts** are executed by the system (background service) as soon as an unchanged approval occurs. The audit log records the requester, approver, and "system" as executor, linked to the script request.

## 15. Remaining Open Questions
None.