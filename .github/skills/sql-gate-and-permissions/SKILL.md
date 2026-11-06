[SKILL.md](https://github.com/user-attachments/files/34457361/SKILL.md)
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

## Rules

- Be conservative: when the gate cannot parse or classify something, deny it.
- Never log or display decrypted connection strings. They are decrypted only inside `RunAsync` through `CryptoService`.
- Every action that changes state should call `AuditService.TryWriteAsync` (best effort) or `WriteAsync` (required, used for execution and export).
- Always `await` settings reads. Blocking with `.GetAwaiter().GetResult()` in request or circuit code made the Run button hang before.
- Metadata (object explorer, autocomplete) is cached by `MetadataService` under `meta:<databaseId>`, controlled by the `MetadataCacheEnabled` and `MetadataTtlMinutes` settings. Admins reload or clear it from the Settings page.
- Run the full test suite after any gate change and add a test for every new rule.
