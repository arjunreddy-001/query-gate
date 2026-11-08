# Agent Guidelines - Query Gate

Query Gate is a .NET 10 Blazor Server application (`DbAccessManager.csproj`) with Entity Framework Core and SQL Server.

## Procedures and Skills

Before executing procedures or workflows in this project, refer to the documentation in `.github/skills/`:

| Workflow | Path | Description |
|---|---|---|
| **Build & Verify** | `.github/skills/run-and-verify/SKILL.md` | Build, run, test, port handling, and UI browser verification. |
| **EF Migrations** | `.github/skills/ef-migrations/SKILL.md` | Creating, updating, resetting migrations, and troubleshooting SQL Server schema issues. |
| **SQL Gate** | `.github/skills/sql-gate-and-permissions/SKILL.md` | ScriptDom T-SQL gate, permission mapping, execution pipeline, and audit logging. |
| **Blazor Pages** | `.github/skills/blazor-page-conventions/SKILL.md` | Page lifecycle, admin menus, navigation, and first-render handling. |
| **UI Design** | `.github/skills/ui-design-system/SKILL.md` | Design tokens (`app.css`), themes, sidebar layout, and Monaco editor integration. |

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
   *(Antigravity automatically discovers new folders in `.github/skills/` via `.agents/skills.json`)*


