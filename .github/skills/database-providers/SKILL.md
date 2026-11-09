---
name: database-providers
description: 'Work with Query Gate multi-engine database providers (SQL Server, PostgreSQL, MySQL, MariaDB). Use when adding or modifying database engines, connection strings, metadata extraction, execution plans, exception translation, or dialect analysis.'
---

# Multi-Engine Database Providers

Query Gate supports Microsoft SQL Server, PostgreSQL, MySQL, and MariaDB targets via the `IDatabaseProvider` abstraction.

## Core Files

- Provider interface: `Services/Providers/IDatabaseProvider.cs`
- Factory: `Services/Providers/DatabaseProviderFactory.cs` (registered as singleton)
- Implementations:
  - SQL Server: `Services/Providers/SqlServerDatabaseProvider.cs` (`Microsoft.Data.SqlClient`)
  - PostgreSQL: `Services/Providers/PostgreSqlDatabaseProvider.cs` (`Npgsql`)
  - MySQL: `Services/Providers/MySqlDatabaseProvider.cs` (`MySqlConnector`)
  - MariaDB: `Services/Providers/MariaDbDatabaseProvider.cs` (`MySqlConnector`)
- Engine enum and helpers: `Data/DatabaseEngine.cs` (`SqlServer = 0`, `PostgreSql = 1`, `MySql = 2`, `MariaDb = 3`)
- Plan parser: `Services/ExecutionPlanParser.cs`

## Provider Contract (`IDatabaseProvider`)

Every provider implements:
1. `CreateConnection(string connectionString)`: Returns appropriate `DbConnection` (`SqlConnection`, `NpgsqlConnection`, or `MySqlConnection`).
2. `ValidateConnectionString(string connectionString)`: Checks required fields (server/host, database, user) without opening a network connection.
3. `TestAsync(string connectionString)`: Opens connection and executes a lightweight test query (`SELECT 1`).
4. `LoadMetadataAsync(string connectionString, ct)`: Queries system catalogs for schemas, tables, views, columns, and data types for the Object Explorer and Monaco autocomplete.
5. `ExplainPlanAsync(string connectionString, string sql, int timeoutSeconds, ct)`: Generates estimated execution plan without executing data modifications.
6. `TranslateException(Exception ex, int timeoutSeconds)`: Maps engine-specific exceptions to standardized outcome codes and extracts error line numbers.
7. `RegisterMessageListener(DbConnection connection, Action<string> onMessage)`: Hooks up server console messages (for example T-SQL `PRINT` or Postgres `RAISE NOTICE`).

## Engine Details

| Engine | Default Port | Sample Connection String | Explain Mechanism |
|---|---|---|---|
| **SQL Server** | 1433 | `Server=localhost,1433;Database=mydb;User Id=sa;Password=...;TrustServerCertificate=True;` | `SET SHOWPLAN_XML ON; <query>; SET SHOWPLAN_XML OFF;` |
| **PostgreSQL** | 5432 | `Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=...;` | `EXPLAIN (FORMAT JSON, ANALYZE false) <query>` |
| **MySQL** | 3306 | `Server=localhost;Port=3306;Database=mydb;User ID=root;Password=...;` | `EXPLAIN FORMAT=JSON <query>` |
| **MariaDB** | 3306 | `Server=localhost;Port=3306;Database=mydb;User ID=root;Password=...;` | `EXPLAIN FORMAT=JSON <query>` |

## Metadata Filtering

When querying catalogs in `LoadMetadataAsync`, filter out internal/system schemas:
- SQL Server: Ignore `sys`, `INFORMATION_SCHEMA`.
- PostgreSQL: Ignore `pg_catalog`, `information_schema`, `pg_toast`.
- MySQL / MariaDB: Ignore `information_schema`, `mysql`, `performance_schema`, `sys`.

## Exception Translation

Standard outcomes emitted by `TranslateException`:
- `Timeout`: Command cancellation or execution timeout.
- `SyntaxError`: Parser error in SQL text with parsed line number if available.
- `PermissionDenied`: Insufficient engine-level permissions.
- `ObjectNotFound`: Table, view, column, or schema does not exist.
- `ConstraintViolation`: Primary key, foreign key, or unique index violation.
- `ConnectionError`: Server unreachable, network drop, bad credentials.
- `ExecutionError`: Fallback for all other database errors.

## Adding or Modifying Providers

1. If adding a new engine, add enum value in `Data/DatabaseEngine.cs` and update `DatabaseEngineInfo` helper methods.
2. Implement `IDatabaseProvider` in `Services/Providers/`.
3. Register the provider in `DatabaseProviderFactory`.
4. Update `SqlGate.ValidateDialectAsync` in `Security/SqlGate.cs` if engine-specific keywords require validation.
5. Add unit tests in `tests/DbAccessManager.Tests/MultiDatabaseProviderTests.cs`.
