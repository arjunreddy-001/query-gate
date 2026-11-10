# Agent Guidelines - Query Gate

Query Gate is a .NET 10 Blazor Server application (`DbAccessManager.csproj`) with Entity Framework Core and SQL Server.

## Procedures and Skills

Before executing procedures or workflows in this project, refer to the documentation in `.github/skills/`:

| Workflow               | Path                                               | Description                                                                                         |
| ---------------------- | -------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| **Build & Verify**     | `.github/skills/run-and-verify/SKILL.md`           | Build, run, test, port handling, and UI browser verification.                                       |
| **EF Migrations**      | `.github/skills/ef-migrations/SKILL.md`            | Creating, updating, resetting migrations, and troubleshooting SQL Server schema issues.             |
| **SQL Gate**           | `.github/skills/sql-gate-and-permissions/SKILL.md` | ScriptDom T-SQL gate, dialect analysis, permission mapping, execution pipeline, and audit logging.  |
| **Database Providers** | `.github/skills/database-providers/SKILL.md`       | Multi-engine providers (SQL Server, PostgreSQL, MySQL, MariaDB), connections, plans, and metadata.  |
| **Data Masking**       | `.github/skills/data-masking/SKILL.md`             | In-flight dynamic data masking (GDPR, HIPAA, PCI-DSS, SOC 2), strategies, UNMASK bypass, and tests. |
| **Blazor Pages**       | `.github/skills/blazor-page-conventions/SKILL.md`  | Page lifecycle, admin menus, navigation, and first-render handling.                                 |
| **UI Design**          | `.github/skills/ui-design-system/SKILL.md`         | Design tokens (`app.css`), themes, sidebar layout, and Monaco editor integration.                   |

## Quick Architecture Map (Token Saver)

Avoid broad directory listings or recursive greps; check these specific files:

- **Execution & Auditing**: `Services/ExecutionServices.cs` (`ExecutionService`), `Security/AuditAuthorization.cs`
- **SQL Analysis & Scopes**: `Security/SqlGate.cs` (ScriptDom + Dialect), `Security/PermissionResolver.cs`, `Security/ObjectScope.cs`
- **Engine Providers**: `Services/Providers/` (`DatabaseProviderFactory`, `IDatabaseProvider`, engine implementations)
- **Data Masking**: `Services/DataMaskingService.cs` (`MaskingRule`, `DataMaskingService`)
- **Query Workspace UI**: `Components/Pages/Query.razor`, `wwwroot/js/app.js` (Monaco & tabs), `wwwroot/app.css`
- **Admin UI**: `Components/Pages/Admin/` (`Settings.razor`, `Permissions.razor`, `Matrix.razor`, `Databases.razor`, `Users.razor`, `Approvals.razor`, `Audit.razor`)
- **Entities & DB Context**: `Data/ApplicationDbContext.cs`, `Data/ManagedDatabase.cs`, `Data/Entities.cs`, `Data/DatabaseEngine.cs`
- **Public & Auth Pages**: `Components/Pages/Landing.razor`, `Documentation.razor`, `Components/Account/Pages/Login.razor`

## Token & Development Rules

1. **Check Skills First**: Always review the corresponding `.github/skills/<skill-name>/SKILL.md` before exploring code.
2. **Fail-Closed Auditing**: Never weaken or skip the pre-execution audit row write in `ExecutionService`.
3. **No Blocking Async**: Never call `.GetAwaiter().GetResult()` or `.Result` in circuit/request code; always use `await`.
4. **Plain Human Copy**: No em dashes or en dashes in UI copy, documentation, or code comments. Use full stops, commas, colons, or parentheses.
5. **Multi-Engine Consistency**: When adding or updating SQL operations, support all four engines or validate dialect constraints.
6. **Environment Variable for Tests**: On macOS/Linux sandboxes, prepend `DOTNET_CLI_HOME=$HOME` if running build or tests.

## Baseline Commands

- **Build**: `dotnet build DbAccessManager.csproj`
- **Tests**: `dotnet test tests/DbAccessManager.Tests`
- **Run**: `dotnet run` (port 5175 HTTP, 7245 HTTPS)

## Skill Management Convention

Whenever a new skill or workflow is created in this project:

1. **Canonical Location**: Always place the skill inside `.github/skills/<skill-name>/SKILL.md` with valid YAML frontmatter (`name` and `description`). Do not store skills directly in `.agents/skills/`, `.claude/skills/`, or `.cursor/skills/`.
2. **Synchronize All References**: Update the skills catalog in:
   - `AGENTS.md` (Codex / Antigravity / general agents)
   - `CLAUDE.md` (Claude Code)
   - `.cursor/rules/skills.mdc` (Cursor)
     _(Antigravity automatically discovers new folders in `.github/skills/` via `.agents/skills.json`)_
