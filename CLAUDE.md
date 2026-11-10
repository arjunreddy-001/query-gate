# Claude Code Guidelines - Query Gate

Query Gate is a .NET 10 Blazor Server application (`DbAccessManager.csproj`) with Entity Framework Core and SQL Server.

## Repository Skills & Runbooks

Specialized procedures and runbooks are located in `.github/skills/`. Whenever you are working on relevant tasks, inspect and follow the corresponding `SKILL.md`:

- **Run and Verify** (`.github/skills/run-and-verify/SKILL.md`):
  Build, test, run, and browser-verify procedures, port handling, and troubleshooting locked executables.
- **Entity Framework Migrations** (`.github/skills/ef-migrations/SKILL.md`):
  Creating, applying, resetting, and troubleshooting EF Core migrations for SQL Server.
- **SQL Gate & Permissions** (`.github/skills/sql-gate-and-permissions/SKILL.md`):
  ScriptDom SQL gate rules, dialect analysis, permission models, statement validation, execution pipelines, and audit trails.
- **Database Providers** (`.github/skills/database-providers/SKILL.md`):
  Multi-engine providers (SQL Server, PostgreSQL, MySQL, MariaDB), connections, execution plans, and metadata discovery.
- **Data Masking** (`.github/skills/data-masking/SKILL.md`):
  In-flight dynamic data masking (GDPR, HIPAA, PCI-DSS, SOC 2), strategies, UNMASK bypass, and test validation.
- **Blazor Page Conventions** (`.github/skills/blazor-page-conventions/SKILL.md`):
  Adding/updating Blazor pages, admin navigation, service patterns, and first-render lifecycle conventions.
- **UI Design System** (`.github/skills/ui-design-system/SKILL.md`):
  Styling (`app.css`), compact sidebar, themes (light/dark), density, layout, Monaco editor integration, and branding.

## Quick Architecture Map (Token Saver)

Avoid broad directory searches or recursive greps:

- **Execution & Auditing**: `Services/ExecutionServices.cs` (`ExecutionService`), `Security/AuditAuthorization.cs`
- **SQL Gate & Scopes**: `Security/SqlGate.cs` (ScriptDom + Dialect), `Security/PermissionResolver.cs`, `Security/ObjectScope.cs`
- **Engine Providers**: `Services/Providers/` (`DatabaseProviderFactory`, `IDatabaseProvider`, engine implementations)
- **Data Masking**: `Services/DataMaskingService.cs` (`MaskingRule`, `DataMaskingService`)
- **Query Workspace UI**: `Components/Pages/Query.razor`, `wwwroot/js/app.js` (Monaco & tabs), `wwwroot/app.css`
- **Admin Pages**: `Components/Pages/Admin/` (`Settings.razor`, `Permissions.razor`, `Matrix.razor`, `Databases.razor`, `Users.razor`, `Approvals.razor`, `Audit.razor`)
- **Entities & DB Context**: `Data/ApplicationDbContext.cs`, `Data/ManagedDatabase.cs`, `Data/Entities.cs`, `Data/DatabaseEngine.cs`
- **Public & Auth Pages**: `Components/Pages/Landing.razor`, `Documentation.razor`, `Components/Account/Pages/Login.razor`

## Quick Commands

- Build: `dotnet build DbAccessManager.csproj`
- Test: `dotnet test tests/DbAccessManager.Tests`
- Run: `dotnet run` (default http port: 5175, https: 7245)

## Skill Management Convention

Whenever a new skill or workflow is created in this project:

1. **Canonical Location**: Always place the skill inside `.github/skills/<skill-name>/SKILL.md` with valid YAML frontmatter (`name` and `description`). Do not store skills directly in `.agents/skills/`, `.claude/skills/`, or `.cursor/skills/`.
2. **Synchronize All References**: Update the skills catalog in:
   - `CLAUDE.md` (Claude Code)
   - `AGENTS.md` (Codex / Antigravity / general agents)
   - `.cursor/rules/skills.mdc` (Cursor)
     _(Antigravity automatically discovers new folders in `.github/skills/` via `.agents/skills.json`)_
