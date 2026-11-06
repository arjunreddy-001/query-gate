# Query Gate

Blazor Server (.NET 10) tool that brokers access to SQL Server databases with a T-SQL gate, fail-closed auditing, permissions, and approval workflows. See [prd.md](./prd.md).

## Run

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=...;Database=DbAccessManager;User Id=...;Password=...;TrustServerCertificate=True"
$env:BootstrapAdmin__Email = "admin@example.com"
$env:BootstrapAdmin__Password = "<unique password, 12+ chars>"
dotnet ef database update
dotnet run
```

The schema was regenerated as a single `InitialSchema` migration. If you applied earlier migrations, drop the database (or the old `__EFMigrationsHistory` and tables) before updating. Development also migrates on startup.

Full usage documentation: [docs/USER-GUIDE.md](./docs/USER-GUIDE.md). Admin quick reference: [docs/ADMIN-CHEATSHEET.md](./docs/ADMIN-CHEATSHEET.md).

## Configuration

| Key | Purpose |
|---|---|
| `ConnectionStrings:DefaultConnection` | Application database |
| `Encryption:Key` | Base64 32-byte key for managed connection strings (dev falls back to `App_Data/dev-encryption.key`; always set in production) |
| `Jwt:Key` | JWT signing key (random per process if unset, which invalidates tokens on restart) |
| `BootstrapAdmin__Email` / `__Password` | One-time first admin |
| `Smtp:*` | Optional email notifications |

## What's included

- Custom-designed UI with light/dark themes, role-based navigation, idle timeout, notifications.
- Query workspace: Monaco editor with schema-aware completion, tabs, saved queries, history, formatting, dry-run (rollback), timeout, cancel, results grid, CSV export (needs `EXPORT`, audited).
- SQL gate (ScriptDom): blocks parse errors, `USE`, cross-database references and security DDL; maps statements to required permissions; confirms unfiltered UPDATE/DELETE.
- Fail-closed auditing: the audit row is written before execution; failure to write blocks the query.
- Access and script requests with approval, edit-before-approve, auto-run, expiry and reminders.
- Admin: approvals, users, databases, permissions (allow/deny, time windows), access matrix, audit log, app logs, settings.
- JWT API under `/api/v1` (login, refresh, logout, change-password, databases, effective access, execute, cancel).

## Limitations

CSV is the only export format; no object-level permission scope; targets SQL Server; LocalDB is untested.
