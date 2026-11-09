# GitHub Copilot Instructions - Query Gate

Query Gate is a .NET 10 Blazor Server application (`DbAccessManager.csproj`) with Entity Framework Core and multi-database support (SQL Server, PostgreSQL, MySQL, MariaDB).

## Repository Skills & Runbooks

All workflows, domain runbooks, and engineering guidelines are maintained centrally in `.github/skills/`. Before executing or proposing code changes, inspect the relevant procedure:

- **Build, Run, & Verification**: `.github/skills/run-and-verify/SKILL.md`
- **EF Core Migrations**: `.github/skills/ef-migrations/SKILL.md`
- **SQL Gate & Permissions**: `.github/skills/sql-gate-and-permissions/SKILL.md`
- **Database Providers**: `.github/skills/database-providers/SKILL.md`
- **Data Masking & Compliance**: `.github/skills/data-masking/SKILL.md`
- **Blazor Pages & Navigation**: `.github/skills/blazor-page-conventions/SKILL.md`
- **UI Design System**: `.github/skills/ui-design-system/SKILL.md`

## Key Engineering Rules

1. **Fail-Closed Auditing**: Never bypass or weaken the pre-execution audit row write in `ExecutionService`. If the audit write fails, execution must block.
2. **No Blocking Async**: Never call `.GetAwaiter().GetResult()` or `.Result` in circuit/request code; always use `await`.
3. **Plain Human Copy**: No em dashes or en dashes in UI copy, documentation, or code comments. Use full stops, commas, colons, or parentheses.
