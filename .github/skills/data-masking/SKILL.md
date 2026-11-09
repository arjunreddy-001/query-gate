---
name: data-masking
description: 'Configure and implement in-flight dynamic data masking and compliance frameworks (GDPR, HIPAA, PCI-DSS, SOC 2). Use when changing masking strategies, rule matching, compliance presets, UNMASK permission, or test mask evaluation.'
---

# Dynamic Data Masking & Compliance

Query Gate applies in-flight dynamic data masking to query results and exports, redacting sensitive PII without modifying underlying database storage.

## Core Files

- Service: `Services/DataMaskingService.cs`
- Model: `MaskingRule` in `Services/DataMaskingService.cs`
- Permission code: `Perm.UNMASK` in `Security/PermissionCatalog.cs`
- Pipeline hook: `Services/ExecutionServices.cs` (`ExecutionService.ExecuteAsync`)
- Export hook: `Services/ExportFormatters.cs`
- Admin UI: `Components/Pages/Admin/Settings.razor` (under Data Masking section)
- Unit tests: `tests/DbAccessManager.Tests/DataMaskingTests.cs`

## Architecture & Storage

- Configuration is persisted in the application database `Settings` table:
  - `DataMasking:Enabled`: string `"true"` or `"false"`.
  - `DataMasking:Rule:{id}`: JSON-serialized `MaskingRule`.
- Thread-safe in-memory caching via `_cachedConfig`. Always call `DataMaskingService.InvalidateCache()` after creating, updating, or deleting rules.

## Masking Strategies

| Strategy | Description | Example Input | Example Output |
|---|---|---|---|
| `Name` | Preserves first initial, replaces surname | `John Doe` | `J*** D**` |
| `MedicalRecord` | Redacts middle digits of medical/patient ID | `MRN-98472-A` | `MRN-***-A` |
| `DateOfBirth` | Truncates to year only | `1985-04-12` | `1985-**-**` |
| `Address` | Redacts street number and specific line | `123 Main St, Suite 400` | `*** Main St` |
| `ZipCode` | Preserves first 3 digits | `94105` | `941**` |
| `Email` | Masks local mailbox, preserves domain | `john.doe@example.com` | `j***e@example.com` |
| `CreditCard` | Masks all except last 4 digits (PCI-DSS) | `4111111111111234` | `****-****-****-1234` |
| `Ssn` | Masks first 5 digits | `123-45-6789` | `***-**-6789` |
| `Phone` | Masks all except last 4 digits | `+1 (555) 234-5678` | `***-***-5678` |
| `IpAddress` | Masks last two octets | `192.168.1.42` | `192.168.*.*` |
| `Salary` | Rounds to order of magnitude range | `125000` | `[100,000 - 150,000]` |
| `Secret` | Full redaction (default fallback) | `secret_api_key_123` | `[REDACTED]` |
| `CustomText` | Replaces value with `CustomMask` | `Confidential info` | User-defined mask |

## Compliance Standards

Supported standard tags: `GDPR`, `HIPAA`, `PCI-DSS`, `SOC 2`, `CCPA`, `FERPA`.
Use `DataMaskingService.StandardBadgeClass(standard)` for color badges (`brand`, `info`, `ok`, `warn`).
Built-in preset rules are seeded via `DataMaskingService.GetDefaultRules()`.

## Execution Pipeline Integration

1. `ExecutionService.ExecuteAsync` runs the query and fetches rows.
2. Checks user permissions: if the user holds `Perm.UNMASK` on the database, masking is completely bypassed.
3. If unmasked access is granted, the audit trail records that unmasked data was returned.
4. If masking is active, columns matching rule patterns (`MaskingRule.Patterns` comma-separated list matched against column names case-insensitively) are transformed row by row using `DataMaskingService.MaskValue`.
5. Exports (CSV, Excel, JSON, Markdown, INSERT statements) automatically honor the masked results.

## Testing Masking Rules

Use `DataMaskingService.MaskValue(sampleValue, strategy, customMask)` to verify masking transformations in unit tests and UI test preview components without hitting a database.
