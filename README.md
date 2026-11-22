# Query Gate

Blazor Server (.NET 10) tool that brokers governed access to SQL Server, PostgreSQL, MySQL and MariaDB databases with a SQL gate, fail-closed auditing, permissions, and approval workflows. See [prd.md](./prd.md).

## Run

For IIS, Azure App Service, Linux and Docker, see the [deployment guide](./docs/DEPLOYMENT.md). The application also provides a public homepage and documentation at `/documentation`.

### PowerShell (Windows)

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=QueryGate;Username=postgres;Password=<password>"
$env:BootstrapAdmin__Email = "admin@example.com"
$env:BootstrapAdmin__Password = "<unique password, 12+ chars>"
dotnet ef database update
dotnet run
```

### macOS / Linux (bash, zsh)

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5432;Database=QueryGate;Username=postgres;Password=<password>"
export BootstrapAdmin__Email="admin@example.com"
export BootstrapAdmin__Password="<unique password, 12+ chars>"
dotnet ef database update
dotnet run
```

The schema was regenerated as a single `InitialSchema` migration. If you applied earlier migrations, drop the database (or the old `__EFMigrationsHistory` and tables) before updating. Development also migrates on startup.

Full usage documentation: [docs/USER-GUIDE.md](./docs/USER-GUIDE.md). Admin quick reference: [docs/ADMIN-CHEATSHEET.md](./docs/ADMIN-CHEATSHEET.md).

## Configuration

| Key                                    | Purpose                                                                                                                       |
| -------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `ConnectionStrings:DefaultConnection`  | PostgreSQL application database                                                                                               |
| `Encryption:Key`                       | Base64 32-byte key for managed connection strings (dev falls back to `App_Data/dev-encryption.key`; always set in production) |
| `Jwt:Key`                              | JWT signing key (random per process if unset, which invalidates tokens on restart)                                            |
| `BootstrapAdmin__Email` / `__Password` | Initial admin creation or SuperAdmin role promotion (platform workspace management at `/platform/tenants`)    |
| `Smtp:*`                               | Optional email notifications                                                                                                  |
| `Database:ApplyMigrationsOnStartup`    | Opt in to applying EF migrations at startup outside Development; use one instance at a time                                  |
| `DataProtection:KeyPath`               | Optional persistent directory for ASP.NET Core data protection keys                                                           |

## What's included

- Multi-tenancy & Workspaces: row-level isolation via EF Core global query filters, path-prefix routing (`/t/{slug}/`), custom domain mappings, multi-workspace user memberships, per-tenant settings (SMTP, webhooks, branding), and platform administration (`/platform/tenants`).
- Custom-designed UI with light/dark themes, role-based navigation, idle timeout, notifications.
- Multi-database support: connects natively to Microsoft SQL Server, PostgreSQL, MySQL, and MariaDB targets with engine-specific metadata extraction, statement splitting, dialect analysis, and execution plan estimation.
- AI Query Assistant with Policy Guardrails (BYOAI): Natural language querying (Ctrl+I), syntax and runtime error repair coach, and execution plan optimization advisor. Supports OpenAI, Azure OpenAI, Google Gemini, Anthropic Claude, and local Ollama or vLLM models. Privacy-first architecture: sends compact schema metadata only (zero row data), verifies generated queries through SqlGate AST parsing, and blocks unauthorized destructive commands.
- Interactive Entity-Relationship (ER) Diagrams: Visual database schema exploration with table cards, column data types, key markers, dynamic Bezier connector paths, SVG pan/zoom, and 1-click query tab opening. Defaults to 1-hop or 2-hop neighborhood graphs for optimal performance, with on-demand full schema rendering.
- Visual Execution Plan Tree: Hierarchical operator DAG with 3-way toggle (Interactive Tree, Operator Table, Raw XML/JSON). Highlights critical path bottleneck operators, percentage costs, scan warnings, and missing index recommendations across SQL Server, PostgreSQL, MySQL, and MariaDB.
- Enterprise OIDC Single Sign-On: Single sign-on with enterprise identity providers including Okta, Microsoft Entra ID, Keycloak, Auth0, and Google Workspace. Strict authorized-only security ensures uninvited external users cannot log in directly, with fail-closed audit logging on all authentication attempts.
- Query workspace: Monaco editor with schema-aware completion, tabs, saved queries, history, formatting, dry-run (rollback), timeout, cancel, results grid with column sorting, row filtering, double-click cell copy, and multi-format export: CSV, Excel (.xls), JSON, Markdown table, and SQL INSERT statements (needs `EXPORT`, audited).
- Dynamic Data Masking: multi-standard in-flight redaction (GDPR, HIPAA, PCI-DSS, SOC 2, CCPA) covering patient names, medical record IDs, dates of birth, street addresses, ZIP codes, credit card PANs, CVVs, SSNs, phone numbers, emails, IP addresses, credentials, and salaries; manageable in the UI with compliance tagging and custom rules.
- SQL gate: blocks parse errors, `USE`, cross-database references and security DDL; maps statements to required permissions; confirms unfiltered UPDATE/DELETE.
- Fail-closed auditing: the audit row is written before execution; failure to write blocks the query.
- Access and script requests with approval, edit-before-approve, auto-run, expiry and reminders.
- Admin: approvals, users, databases, permissions (allow/deny, time windows), access matrix, audit log, app logs, settings.
- Email & Webhooks: in-UI SMTP server configuration with live test delivery and AES-256 encrypted passwords; team collaboration webhook alerts (Slack, Microsoft Teams, Zoho Cliq, Discord, generic webhooks) with event filtering and instant verification.
- JWT API under `/api/v1` (login, refresh, logout, change-password, databases, effective access, execute, cancel).

## Limitations

The Query Gate application database requires PostgreSQL 14+ (or bundled PostgreSQL 17 in Docker). Managed database targets can be SQL Server, PostgreSQL, MySQL, or MariaDB.

## AI Agent Skills (Cross-Platform)

This repository maintains all development workflows and runbooks centrally in [`.github/skills/`](./.github/skills/) following the Agent Skills specification.

**There is no need to add skills specific to a platform or IDE** (such as `.claude/skills/`, `.cursor/skills/`, or `.agents/skills/`). The repository is pre-configured with cross-platform configuration files (avoiding symlinks for seamless compatibility across Windows, macOS, and Linux) so all AI coding assistants share `.github/skills/` as the single source of truth:

- **Google Antigravity**: Discovers and indexes skills via [`.agents/skills.json`](./.agents/skills.json).
- **Claude Code**: Referenced via [`CLAUDE.md`](./CLAUDE.md).
- **Cursor**: Referenced via [`.cursor/rules/skills.mdc`](./.cursor/rules/skills.mdc).
- **Codex & General Agents**: Referenced via [`AGENTS.md`](./AGENTS.md).
- **GitHub Copilot**: Referenced via [`.github/copilot-instructions.md`](./.github/copilot-instructions.md).
- **Windsurf**: Referenced via [`.windsurfrules`](./.windsurfrules).

When adding a new skill, define it once inside `.github/skills/<skill-name>/SKILL.md` and reference it in the platform instruction files (`AGENTS.md`, `CLAUDE.md`, `.cursor/rules/skills.mdc`, `.github/copilot-instructions.md`).


