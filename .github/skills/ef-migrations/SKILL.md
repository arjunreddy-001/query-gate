---
name: ef-migrations
description: 'Create, apply, reset or troubleshoot Entity Framework Core migrations for Query Gate (SQL Server). Use when changing entities or the DbContext, adding a column or table, running database update, or fixing errors like "object already exists" or "pending model changes".'
---

# EF Core migrations

- Context: `Data\ApplicationDbContext.cs`. Entities: `Data\Entities.cs`, `ManagedDatabase.cs`, `PermissionGrant.cs`, `ApplicationUser.cs`.
- Migrations live in `Data\Migrations`. The app also runs `MigrateAsync()` and seeds the bootstrap admin at startup (see `Program.cs`), so pending migrations apply on the next run.
- Tool: `dotnet-ef` (global, 10.x). Connection string: `ConnectionStrings:DefaultConnection` in `appsettings.json`.

## Add a migration

1. Edit the entity or `OnModelCreating`.
2. `dotnet ef migrations add <PascalCaseName> --output-dir Data\Migrations`
3. Read the generated `Up` method. Check column types, lengths and indexes, and that nothing destructive was added by accident.
4. `dotnet ef database update`
5. Build and run the tests.

If the build fails because the app is running, stop it first (see `run-and-verify`).

## Reset a development database

Only with the user's explicit approval, because it deletes data:
`dotnet ef database drop --force` then `dotnet ef database update`.
Use this when the database has tables from an older migration set ("There is already an object named 'AspNetRoles'").

## Rules

- Never edit a migration that has been applied to a shared database. Add a new one.
- Never run `drop` against a database the user has not named.
- Secrets (connection passwords, encryption keys) must not appear in migrations or docs. `App_Data\` and `*.key` are git-ignored.
- Seeding uses `BootstrapAdmin:Email` and `BootstrapAdmin:Password`. Both must be set together. Prefer `dotnet user-secrets set` over `appsettings.json`.
