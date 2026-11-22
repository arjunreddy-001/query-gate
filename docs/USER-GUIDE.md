# Query Gate User Guide

Query Gate lets people run SQL against registered databases (SQL Server, PostgreSQL, MySQL, and MariaDB) **without ever seeing the database credentials**. Every query passes a SQL gate, is permission-checked, and is written to an audit log _before_ it runs.

The public homepage links to sign-in, `/documentation`, deployment options and community resources, and introduces the workspace and access workflow. Signed-in users can open the query workspace directly from the homepage. After signing in, the dashboard is at `/dashboard`. On narrow screens, use the navigation button to open the menu. For hosting options, see the [deployment guide](./DEPLOYMENT.md).

## 1. Roles

| Role          | Typical use    | Can do                                                                      |
| ------------- | -------------- | --------------------------------------------------------------------------- |
| **Admin**     | Platform owner | Everything: users, databases, permissions, approvals, settings, logs, audit |
| **Developer** | Engineers      | Query workspace, request access, submit scripts                             |
| **Tester**    | QA             | Same as Developer, with narrower default permissions                        |
| **Auditor**   | Compliance     | Read-only: Audit log and Access matrix (plus normal user pages)             |

Initial default grants applied when an admin registers a database (configurable under Permissions):

| Role      | Development                                          | QA                  | Production |
| --------- | ---------------------------------------------------- | ------------------- | ---------- |
| Developer | SELECT, INSERT, UPDATE, DELETE, EXECUTE, SP_HELPTEXT | SELECT              | none       |
| Tester    | none                                                 | SELECT, SP_HELPTEXT | none       |

Anything not explicitly allowed is **denied**.

Administrators can add, rename and delete roles and environments under **Permissions**. Only Admin is protected. New roles have standard user page access and no database grants or audit capability by default. Enable a role's audit capability to grant access to the Audit log and Access matrix; the initial Auditor role has this capability. Assign grants on the Permissions page and roles on the Users page. Renaming a role preserves memberships and grants; affected users must sign in again when their sessions are revalidated.

Use **Defaults** beside an environment to configure allow grants for each role, including specific `SYSPROC:name` permissions. Defaults apply only to databases registered afterward; editing defaults or changing a database's environment does not alter existing grants. Renaming an environment updates its databases without changing their grants. Remove dependent users, grants and defaults before deleting a role, or dependent databases and defaults before deleting an environment.

## 2. Admin quick start (first-time setup)

1. **Sign in** with the bootstrap admin (`BootstrapAdmin__Email` / `__Password`). If prompted, change the password.
2. **Register a database**: _Admin → Databases → Register database_
   - **Name**: a friendly name (e.g. `Orders`).
   - **Environment**: select a configured environment. Development, QA and Production are provided initially; administrators can manage them under Permissions.
   - **Connection string**: a database connection string for a **dedicated service account** with only the rights the tool should be able to use, e.g.
     - PostgreSQL: `Host=host;Port=5432;Database=Orders;Username=svc_dbam;Password=...`
     - SQL Server: `Server=host;Database=Orders;User Id=svc_dbam;Password=...;TrustServerCertificate=True`
     - MySQL or MariaDB: `Server=host;Port=3306;Database=Orders;Uid=svc_dbam;Pwd=...`
   - **Default timeout** and **Max rows**: per-database limits.
   - Click **Test connection**, then **Save**. The connection string is encrypted at rest and never shown again (edit it by entering a new one; leave blank to keep the current).
   - Name uniqueness is checked by **Name + Environment**. The same name can be used across environments.
3. **Create users**: _Admin → Users → New user_
   - Enter email, display name, role, optional account expiry.
   - A **temporary password is shown once**; copy it and share it securely. The user must change it at first sign-in.
4. **Grant access**: _Admin → Permissions_ (see §4), or let users request it (see §6).
5. Users sign in and can query the databases they have been granted.

## 3. Managing users (Admin → Users)

- **Search** by name or email.
- **Role**: change from the dropdown.
- **Edit**: set an **account expiry** (the account stops working after that time) and a **per-user query timeout override**.
- **Reset pwd**: generates a new temporary password (shown once).
- **Deactivate / Activate**: deactivating immediately signs the user out, revokes API tokens and cancels their running queries.
- **Bulk deactivate**: tick users, then _Deactivate selected_.
- Five failed logins lock the account for 15 minutes.

## 4. Managing permissions (Admin → Permissions)

Pick a database, then **Add grant**:

- **Applies to**: a _Role_ (everyone in it) or a single _User_.
- **Target Scope** (optional): `*` for the whole database, `schema.*` (e.g. `sales.*`) for all objects in a schema, or `schema.object` (e.g. `dbo.Customers`, `dbo.usp_Calculate`) for a specific table, view, or stored procedure.
- **Permission**: SELECT, INSERT, UPDATE, DELETE, EXECUTE, CREATE, ALTER, DROP, TRUNCATE, SP_HELP, SP_HELPTEXT, VIEW_DEFINITION, DYNAMIC_SQL, LINKED_SERVER, XP_CMDSHELL, EXPORT, UNMASK. To allow a specific system procedure, type `SYSPROC:name`.
- **Effect**: _Allow_ or _Deny_.
- **Window** (optional): start and end time for temporary access.

**How rules combine**

1. **Scope specificity**: An exact **object-level rule** (`dbo.Salaries`) overrides a **schema-level rule** (`dbo.*`), which overrides a **database-wide rule** (`*`).
2. A **user rule** overrides a **role rule** at the highest matching tier.
3. Within the same tier, **Deny wins** over Allow.
4. Database-wide grants automatically cover all objects unless an explicit object-level or schema-level Deny exists.
5. Users without database-wide access can be granted access to specific tables or schemas only.
6. No matching rule = denied.
7. Time windows are checked at the moment a query runs.

Permissions flagged as _dangerous_ (CREATE/ALTER/DROP/TRUNCATE, DYNAMIC_SQL, LINKED_SERVER, XP_CMDSHELL, system procedures) should be granted sparingly and are still required even for approved scripts.

`EXPORT` controls multi-format export of results (CSV, Excel, JSON, Markdown, SQL INSERT); it is checked and audited separately from SELECT.

`UNMASK` controls viewing unredacted PII and sensitive data. Without this permission, columns containing patient names, medical record numbers (MRNs), dates of birth, addresses, ZIP codes, phone numbers, emails, SSNs, IP addresses, credit cards, salaries, and secrets are dynamically masked in accordance with HIPAA Safe Harbor, GDPR, PCI-DSS, SOC 2, and CCPA standards. Administrators can configure custom column patterns, filter rules by origin (built-in vs custom) and compliance frameworks, add or delete rules, choose redaction strategies, and test pattern matching live under **Admin → Settings → Data Masking**. Destructive actions (deleting rules, global disable, reset defaults) are guarded by confirmation dialogs, and actions across the entire application (permissions, queries, approvals, settings) display highlighted top-center floating toast notifications with animated drain timers.

## 5. Using the application (all users)

### Dashboard

Overview of your accessible databases, pending requests and recent activity.

### Query workspace (_Query workspace_)

On narrow screens the Objects, Saved and History panel starts closed. Use the panel button next to the query tabs to open or close it.

1. Choose a **database** (environment badge shows Dev/QA/Prod).
2. Write SQL in the editor. Autocomplete suggests tables and views in source clauses and columns from visible table and view aliases in expressions. Press `Ctrl+Space` to request suggestions. Duplicate column names are qualified with their source alias. Column suggestions include type and nullability when available.
   - Basic derived tables, CTE projections and table-valued functions are supported. Complex or incomplete SQL may not be fully resolved; suggestions do not validate or authorize a query.
   - In an `ON` clause, select a named foreign-key suggestion to insert its join condition. Relationships depend on metadata visible to the managed SQL account. Composite keys include all columns.
   - Suggestions include common SQL snippets. Hover over a resolvable column to see its source and available type details. Basic table variables, temporary tables, scalar variables and DML targets are inferred from the current batch where possible.
   - Use **Editor settings** to change automatic suggestions, font size and optional live syntax diagnostics. Settings follow your account. Diagnostics are off by default, do not execute SQL, and can report errors while a statement is incomplete. They are unavailable for SQL over 4,000 characters.
   - The **object explorer** on the left lists tables/views/procedures.
   - **Deep Schema Explorer**:
     - Click the **Schema** folder icon button in the Object Explorer toolbar to toggle between grouping objects by database schema (e.g. `dbo`, `sales`, `audit`) and viewing them as a flat list.
     - Check the **Cols** checkbox next to the search filter to search column names across all tables and views in addition to table names.
     - Object search is **fuzzy by default**. Use the **Fuzzy** toggle to switch between fuzzy and exact contains matching.
     - Use the object type chips to filter to **Tables**, **Views**, **Procedures** or **Functions**.
   - Database dropdown shows enabled databases by default. Turn on **Show disabled** to include disabled entries.
   - **Interactive ER Diagrams**: Click the ER diagram button in the Objects explorer toolbar (or right-click any table and choose **View ER Diagram**) to launch the visual schema viewer.
     - **Focus Table & Neighborhood**: By default, the diagram loads the selected table and its 1-hop or 2-hop foreign key relationships. This keeps rendering instantaneous even on databases with hundreds of tables.
     - **Schema Scope Toggle**: Switch between 1 Hop, 2 Hops, or Full Schema to view the entire database topology.
     - **Interactive Canvas**: Table cards show column names, data types, nullability, primary key (PK) badges, and foreign key (FK) indicators. Drag table cards to reorganize the layout with live Bezier relationship paths. Use zoom in/out, fit to view, and reset controls.
     - **One-Click Querying**: Click the **Query** button on any table header inside the diagram to instantly open a new query tab populated with a starter query (`SELECT TOP 100 *`).
3. **Run** with `F5` or `Ctrl+Enter`. Select text first to run only the selection.
4. Other actions:
   - **AI Query Assistant (BYOAI)**: Press `Ctrl+I` or click the **AI Assistant** button in the query toolbar to open the natural language prompt bar.
     - Type an instruction in plain English (for example, "Find top 10 customers by order total in 2024").
     - The assistant inspects schema metadata (tables, columns, foreign keys) and crafts an optimized SQL query for your target engine. Table row data is never transmitted to the AI provider.
     - Every generated query is checked by the server-side `SqlGate` parser before display. If the query requires elevated permissions, introduces dangerous DDL, or lacks a WHERE clause on updates, an alert warns you upfront.
     - Click **Insert to Editor** to place the generated SQL directly into your active query tab.
     - **Error Diagnosis & Repair**: When a query encounters a syntax error or database exception, the error banner presents an **Explain & Fix with AI** button. Clicking it asks the AI assistant to pinpoint the root cause and propose corrected SQL.
   - **Explain**: compiles the query and generates an estimated execution plan using the target engine's explain capability (SQL Server `SET SHOWPLAN_XML ON`, PostgreSQL `EXPLAIN (FORMAT JSON)`, MySQL/MariaDB `EXPLAIN FORMAT=JSON`). It compiles without executing data modifications or reading data. The resulting **Execution Plan** tab displays:
     - **View Modes**: Switch between **Tree (DAG)**, **Operator Table**, and **Raw Plan** (XML or JSON).
     - **Interactive DAG Tree**: Visual operator hierarchy showing data flow from leaf table/index scans to root result operators. High-cost bottleneck operators are highlighted with distinct warning styling and cost badges. Click any node in the tree to inspect details, estimated rows, and runtime metrics.
     - **AI Plan Analysis**: Click **Explain Plan with AI** in the plan header to receive an automated analysis of performance bottlenecks, index opportunities, and query structure advice.
     - Estimated Subtree Cost, Optimization Level, Estimated Rows, and compile time metrics.
     - **Missing Index Recommendations**: identifies missing index groups from the query optimizer with estimated cost impact percentage, one-click **Copy Index Script**, and an **Add to Editor** shortcut that appends the DDL to the SQL editor.
     - **Operator Breakdown**: sorts operators by cost descending with percentage bars, estimated rows, target objects, and warnings for full index or table scans.
     - **Showplan Viewer**: collapsible view with a one-click **Copy Plan** button.
   - **Format** (`Shift+Alt+F`), **Save query** (`Ctrl+S`), **Cancel** a running query.
   - **Form Mode & Parameterized Queries**: write queries with `:paramName`, `@paramName`, or `{{paramName}}` tokens and click **Form Mode** in the toolbar to run queries via auto-generated forms. Parameters are bound dynamically via ADO.NET parameters without string concatenation.
   - **Global Command Palette**: press `Cmd+K` on macOS or `Ctrl+K` on Windows/Linux to open the command palette for instant route navigation, database switching, and workspace actions.
   - **Workspace Layout Split**: toggle between stacked vertical (editor top, results bottom) and side-by-side horizontal (editor left, results right) split layouts with smooth draggable divider resizing.
   - **Team Query Library & Shared Snippets**: save queries with category tags, check **Share with Team & Workspace**, and switch between **My Queries** and **Team Library** in the Saved side panel.
   - **Production Safeguards**: targets in `Production` display an environment warning banner, an editor watermark overlay, and a confirmation modal for destructive write statements (UPDATE, DELETE, DROP, TRUNCATE, ALTER).
   - **Dry run**: executes inside a transaction and **rolls back**, so use it to test INSERT/UPDATE/DELETE safely.
   - **Timeout**: override per run (limited by the admin maximum).
   - **Tabs** are remembered in your browser. **Saved queries** (up to 50 per database) and **History** are in the side panel.
5. Results appear in a grid (the row cap applies); the **Messages** tab shows errors and row counts.
   - **Results Grid Overhaul**:
     - **Interactive Column Resizing**: drag the vertical dividers between column headers to resize columns to any width.
     - **Live Aggregates Bar**: inspect real-time aggregate statistics (Count, Distinct, Sum, Avg, Min, Max) on the sticky status bar at the bottom of the table.
     - **Cell Inspector Drawer**: double-click any cell to open a slide-out drawer with formatted JSON and XML viewing and 1-click clipboard copying.
   - **1-Click Visual Charting**: switch to the **Visual Chart** tab to generate interactive SVG Bar, Line, Area, or Donut charts directly from your query results with customizable category and metric columns.
   - **Dynamic Data Masking**: users without the `UNMASK` permission automatically see sensitive columns (emails, credit cards, SSNs, phone numbers, salaries, passwords/API keys) redacted in-flight. Table headers show a lock icon on redacted columns, and a `Masked (N columns redacted)` badge appears in the results header. Redaction is enforced server-side before delivery, guaranteeing that all exports also contain only masked data.
   - **Export menu**: hover over the compact **Export ▾** button to download as **CSV**, Excel spreadsheet (**Excel** `.xls`), or JSON array (**JSON**). You can also copy results directly as a **Markdown** table or open the **SQL INSERT…** dialog. All export options require the `EXPORT` permission and are audited.
   - **SQL INSERT export**: automatically infers the target table name from your `FROM` clause (e.g. `[Products]`). You can select one or more duplicate check columns to wrap each row in an idempotent `IF NOT EXISTS (SELECT 1 FROM [Products] WHERE col1 = 'val1' AND col2 = 'val2') BEGIN INSERT ... END` block, preventing duplicates when inserting into other databases.
   - **Grid filtering & sorting**: use the search box in the results toolbar to filter displayed rows in real time. Click any column header to sort ascending or descending.
   - **Cell copy**: double-click any cell to copy its value to the clipboard or inspect it in the cell drawer.

**What the SQL gate does for you**

- Blocks: syntax errors, `USE`, cross-database references, `GRANT/DENY/REVOKE`, security-related DDL and unrecognised statements.
- Maps each statement to a required permission (SELECT → SELECT, etc.) and rejects the whole batch if you lack any of them.
- Asks for **confirmation** before an `UPDATE`/`DELETE` that has **no WHERE clause**.
- If the audit record cannot be written, the query **does not run** (fail-closed).

### My access (_My access_)

- **Effective permissions** per database, with their source (user rule / role rule).
- Your active grants and expiry times.
- **Request access**: choose database, permissions, duration (hours), justification and optional ticket number. Extending an expiring grant is done the same way (mark it as an extension).

### Script requests (_Script requests_)

For changes you are not permitted to run directly (e.g. DDL on QA/Production):

1. Create a request: title, database, justification, and the **script**.
2. The gate analyses it and shows the permissions it needs.
3. An admin reviews it (and may edit it). Once approved, it runs either automatically (as _System_) or when an admin clicks **Run now**. Results are recorded on the request.

### Notifications

The bell icon shows approvals, rejections, grant expiry reminders and script results (and email, if SMTP is configured).

### Account

User menu (top right) → _Account settings_ / _Change password_.

- In Account settings, you can update **Display name** and **Phone number**. Phone number is optional.
- In Personal data, **Download** and **Delete** actions are shown side by side.

The light/dark theme toggle is in the top bar. The sidebar is icon-only by default and expands when you hover over it; use _Pin sidebar_ at its bottom to keep it open. In the Query workspace you can hide the object panel and drag the bar between editor and results to resize. Sessions end after 30 minutes of inactivity (warning at 25).

## 6. Approving requests (Admin → Approvals)

**Access tab**

- Open _Review_, optionally change the granted **duration**, add a comment, then **Approve** or **Reject** (a comment is required to reject).
- Approval creates user-level, time-limited Allow grants automatically; they are removed when they expire, and the user is reminded before expiry.

**Scripts tab**

- _Review_ shows the requester's script and the gate analysis (required permissions, blocks, unfiltered writes).
- You may **edit the script**. A modified script cannot auto-run; an admin must press **Run now**.
- Choose **Run automatically after approval** to have the system execute it.
- Approvers cannot approve their own requests unless _Allow approvers to approve their own requests_ is on in Settings.
- Pending requests expire automatically if not reviewed.

## 7. Auditing and oversight

### Audit log (Admin or roles with audit capability)

Filter by user, category, outcome, date range and SQL/detail text; expand **SQL** to see the exact statement. Records include client IP, duration, rows, and who executed it (user, admin, or System). **Export CSV** is itself audited. Entries cannot be edited or deleted in the UI.

### Access matrix (Admin or roles with audit capability)

Select a database to see, for every user, which permissions are effectively allowed (dangerous ones highlighted). **Export CSV** for access reviews.

### Application logs (Admin)

Errors and warnings with correlation IDs. Users who hit an error see a correlation ID; paste it into the search box to find the details.

## 8. Settings (Admin → Settings)

| Setting                                           | Default |
| ------------------------------------------------- | ------- |
| Default query timeout (s)                         | 30      |
| Maximum timeout ceiling (s)                       | 300     |
| Default result row limit                          | 1000    |
| Concurrent queries per user                       | 3       |
| Concurrent queries per database                   | 10      |
| Allow self-approval                               | No      |
| Query history entries per user                    | 100     |
| Expiry reminder (hours before)                    | 24      |
| Cache object metadata (explorer and autocomplete) | Yes     |
| Object metadata cache TTL (min)                   | 10      |
| Temporary password length                         | 16      |

The cache card below the settings lets admins reload or clear cached object metadata per database or for all databases.

**Automated Schema PII Discovery:** under **Admin → Settings → Data Masking**, click **Scan Schema for PII** to scan any registered database against 11 regulatory compliance categories (GDPR, HIPAA, PCI-DSS, SOC 2). The scanner highlights unprotected columns and provides a 1-click **Protect** button that automatically creates dynamic masking rules without manual regex authoring.

**Email & SMTP:** configure outgoing mail settings (host, port, SSL/TLS, username, password, sender address) directly in the UI. Passwords are encrypted at rest with AES-256-GCM. Use the _Send test_ button to verify deliverability with live SMTP status feedback.

**Collaboration webhooks:** configure instant webhook dispatches to Slack, Microsoft Teams, Zoho Cliq, Discord, or generic webhook receivers. Filter alerts by all events, requests only, or script executions only. Use the _Test_ button to verify end-to-end webhook delivery.

**AI Assistant (BYOAI):** configure your organization's chosen AI provider and model under the _AI Assistant_ tab.
- Query Gate provides no default AI provider. You bring your own API key or endpoint.
- Supported providers: OpenAI, Azure OpenAI, Google Gemini, Anthropic Claude, or any OpenAI-compatible local/private endpoint (Ollama, vLLM, LocalAI).
- API keys are encrypted at rest using AES-256-GCM.
- Configure default model name, custom base URL, and temperature. Use the _Test connection_ button to verify API connectivity.
- Privacy assurance: only schema names, column types, and foreign key relations are sent in prompts. Live database rows and sensitive cell values are never sent.

**Single Sign-On (SSO & OIDC):** configure federated enterprise authentication under the _SSO & OIDC_ tab.
- Connect to OpenID Connect identity providers such as Okta, Microsoft Entra ID, Keycloak, Auth0, or Google Workspace.
- Configure Authority URL, Client ID, Client Secret (encrypted at rest), requested Scopes (`openid profile email`), and custom button label.
- Use the _Test discovery_ button to validate the provider's `.well-known/openid-configuration` endpoint.
- **Strict Authorized-Only Access Policy**: uninvited or unauthorized external users cannot log in. A user must first be created or invited by an administrator under _Admin → Users_ with a matching email before they are permitted to sign in via SSO. All authentication attempts (successful or rejected) are recorded in the audit log.

The effective timeout is the lowest of the admin ceiling and the first available of: per-run override, per-user override, per-database default.

## 9. API (optional, `/api/v1`)

Authenticate with `POST /api/v1/auth/login` (returns a 15-minute JWT; a refresh cookie lasts 8 hours and rotates on `/auth/refresh`). Then use `Authorization: Bearer <token>`:

| Endpoint                                            | Purpose                                              |
| --------------------------------------------------- | ---------------------------------------------------- |
| `POST /auth/login`, `/auth/refresh`, `/auth/logout` | Session handling (rate-limited)                      |
| `POST /auth/change-password`                        | Change password                                      |
| `GET /databases`                                    | Databases you can access                             |
| `GET /access/effective/{databaseId}`                | Your effective permissions                           |
| `POST /query/execute`                               | Run SQL (same gate, permissions and audit as the UI) |
| `POST /query/{runId}/cancel`                        | Cancel a run                                         |

## 10. Multi-Tenancy and Workspaces

Query Gate supports multi-tenant workspaces with complete data isolation across organizations.

### Architecture and Isolation
- **Workspace URLs**: Each workspace is accessible via path prefix `/t/{slug}/` (for example, `/t/acme/dashboard` or `/t/acme/query`).
- **Custom domains**: Connect custom hostnames (such as `data.acme.com`) to any workspace. Point your DNS record to the server, and Query Gate automatically routes requests by Host header without requiring a path prefix.
- **Data isolation**: All database registrations, permission grants, audit logs, saved queries, query history, access requests, and notifications are isolated per workspace via row-level security with Entity Framework Core global query filters.
- **Multiple memberships**: A single user identity can belong to multiple workspaces with different roles in each. Switch between workspaces using the topbar workspace dropdown or the `/select-tenant` page.
- **Tenant-level settings**: SMTP email mailers, notification webhooks, and branding are configured independently per workspace under _Admin -> Settings_.

### SuperAdmin Role and Platform Administration
Users with the `SuperAdmin` role operate outside individual tenant data boundaries and manage all workspaces, domains, and global platform status at `/platform/tenants`.

To grant or assume the `SuperAdmin` role:

1. **Bootstrap Configuration (Recommended)**:
   Configure the bootstrap administrator in your environment file or deployment settings:
   ```env
   BOOTSTRAP_ADMIN_EMAIL=admin@company.com
   BOOTSTRAP_ADMIN_PASSWORD=YourPassword!1234
   ```
   On application startup, Query Gate creates the user (or locates an existing account with that email) and assigns both `Admin` and `SuperAdmin` roles.

2. **Direct PostgreSQL Database Assignment**:
   If the application is already running and you want to elevate an existing account without restarting:
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
   Log out and log back in to refresh the authentication session.

### Provisioning Workspaces and Onboarding Users
Query Gate uses an administrative onboarding model rather than public self-service signup to protect internal database infrastructure:

1. **Create a Workspace**: Sign in as a `SuperAdmin`, open `/platform/tenants`, and click **Create Workspace**. Provide the organization name, slug, contact email, and an initial administrator email.
2. **Configure Custom Domains (Optional)**: Click **Domains** on the workspace row, add your hostname (e.g., `sql.acme.com`), and point your DNS CNAME or A record to the Query Gate host.
3. **Add Users to the Workspace**: A workspace administrator signs in to `/t/{slug}/dashboard`, opens **Admin -> Users** (`/admin/users`), and clicks **New user**. If the user already has an account, they are immediately granted access to the workspace; otherwise, a temporary password is generated for initial sign-in.
4. **Switching Workspaces**: Users assigned to multiple workspaces can switch at any time using the workspace dropdown in the top header or by visiting `/select-tenant`.

## 11. Operations notes

- Set `Encryption:Key` (Base64, 32 bytes) and `Jwt:Key` in production; otherwise keys are not stable across restarts.
- Apply migrations with `dotnet ef database update` during deployment.
- Configure `Smtp:*` to enable email notifications.
- A background worker runs every minute: sends expiry reminders, removes expired grants, expires stale requests, and runs approved auto-run scripts.

## 12. Troubleshooting

| Symptom                                    | Fix                                                                                  |
| ------------------------------------------ | ------------------------------------------------------------------------------------ |
| "Permission denied" on run                 | Check _My access_; request the missing permission or ask an admin                    |
| Query blocked by the gate                  | Read the message: remove `USE`/cross-database references, or submit a Script request |
| Can't log in                               | Account may be inactive, expired or locked (15 min); ask an admin                    |
| Editor looks like a plain text box         | Monaco failed to load (network/CSP); queries still work                              |
| Database test fails with certificate error | Add `TrustServerCertificate=True` to the connection string                           |
| Unexpected error page                      | Note the correlation ID and give it to an admin (Application logs)                   |
