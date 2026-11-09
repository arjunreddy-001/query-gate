---
name: sql-gate-and-permissions
description: 'Work on the Query Gate SQL gate, permission model, execution pipeline and audit trail. Use when adding a permission, changing which SQL statements are allowed or denied, editing SqlGate, ExecutionService, PermissionResolver, audit logging, approved scripts, or the metadata cache.'
---

# SQL gate, permissions and auditing

## Pipeline (`Services\ExecutionServices.cs`, `ExecutionService.ExecuteAsync`)

1. Load the managed database and user. Deny if the user is inactive or the database is disabled.
2. `SqlGate.Analyze` (`Security\SqlGate.cs`, ScriptDom) parses the batch and returns required permissions, hard denials, unfiltered UPDATE/DELETE and statement types.
3. Hard denials and parse errors stop the run.
4. Required permissions are resolved against the user's grants with `PermissionResolver`. A missing permission means Denied.
5. UPDATE or DELETE without WHERE needs explicit confirmation (`NeedsConfirmation`).
6. Concurrency slots (per user, per database), then **write the audit row first**. If the audit write fails the query is not executed (`AuditFailure`). This is fail closed and must not be weakened.
7. Execute, cap rows by the database or global row limit, update the audit row with outcome, duration and rows, then add query history.

## Permissions (`Security\PermissionCatalog.cs`, class `Perm`)

`Catalog` lists every code. `Writes` and `Dangerous` (DYNAMIC_SQL, LINKED_SERVER, XP_CMDSHELL, `SYSPROC:*`) are separate sets. Approved scripts skip non-dangerous requirements but still need dangerous ones.

To add a permission:
1. Add the constant and include it in `Catalog` (and `Writes` or `Dangerous` where it belongs).
2. Make `SqlGate` emit it as a requirement.
3. Check the Permissions and Matrix admin pages still list it.
4. Add tests in `tests\DbAccessManager.Tests` for allowed, denied and edge cases.
5. Update `docs\USER-GUIDE.md` and `docs\ADMIN-CHEATSHEET.md`.

## Object-Level Scoping (`Security/ObjectScope.cs`)

Grants can be scoped to specific tables, views, or schemas rather than entire databases:
- Formats: `*` (entire database), `schema.*` (all objects in schema), `schema.table` (specific object), or `table` (object in default schema).
- `ObjectScope.Matches(ruleScope, targetObject)` resolves wildcards and case-insensitively checks if a rule permits or denies access to a target.
- Database and role scope rules (`DatabaseRoleScopeRule`, `AccessConfiguration`) define default inclusions/exclusions per database or role.
- If a user has a permission grant with an object scope list, only queries referencing matched objects are permitted.

## Dialect Gate (`Security/SqlGate.cs`, `ValidateDialectAsync`)

For non-SQL Server engines (PostgreSQL, MySQL, MariaDB):
1. ScriptDom is T-SQL specific, so non-SQL Server queries are analyzed using `SqlGate.ValidateDialectAsync`.
2. Comments (`--`, `/* ... */`) and string literals (`'...'`, `"..."`, `` `...` ``) are stripped first to prevent false positive keyword matching.
3. Forbidden commands are denied: `CREATE`, `ALTER`, `DROP`, `TRUNCATE`, `GRANT`, `REVOKE`, `EXECUTE`, `CALL`, `COPY`, `INTO OUTFILE`, `PG_READ_FILE`, `LOAD DATA`, and dangerous system procedures.
4. Validated statement types are classified into SELECT, INSERT, UPDATE, DELETE requirements.

## Rules

- Be conservative: when the gate cannot parse or classify something, deny it.
- Never log or display decrypted connection strings. They are decrypted only inside `RunAsync` through `CryptoService`.
- Every action that changes state should call `AuditService.TryWriteAsync` (best effort) or `WriteAsync` (required, used for execution and export).
- Always `await` settings reads. Blocking with `.GetAwaiter().GetResult()` in request or circuit code made the Run button hang before.
- Metadata (object explorer, autocomplete) is cached by `MetadataService` under `meta:<databaseId>`, controlled by the `MetadataCacheEnabled` and `MetadataTtlMinutes` settings. Admins reload or clear it from the Settings page.
- Run the full test suite after any gate change and add a test for every new rule.
