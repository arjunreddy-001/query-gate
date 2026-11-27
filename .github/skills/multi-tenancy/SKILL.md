---
name: multi-tenancy
description: 'Multi-tenant architecture, workspaces (/t/{slug}), custom domains, user memberships, and data isolation in Query Gate. Use when working with tenants, workspaces, ITenantContext, TenantAwareDbContextFactory, global query filters, or platform administration.'
---

# Multi-Tenancy & Workspaces in Query Gate

Query Gate employs a shared-schema, row-level isolation architecture partitioned by `TenantId` (UUID) across all tenant-scoped entities.

## 1. Core Architecture

- **Tenant isolation**: EF Core global query filters on `ApplicationDbContext` ensure queries automatically append `WHERE "TenantId" = @FilterTenantId`. If no tenant is resolved, `FilterTenantId` defaults to `Guid.Empty` (fail-closed).
- **Auto-population**: `ApplicationDbContext.SaveChangesAsync()` automatically populates `TenantId` on any newly added entity that has a `TenantId` property set to `Guid.Empty`.
- **Tenant context**: `ITenantContext` (scoped) and `ITenantContextAccessor` (singleton with `AsyncLocal<ITenantContext?>`) provide the active tenant context to scoped Blazor circuits, HTTP requests, and singleton services.
- **Factory wiring**: `TenantAwareDbContextFactory` implements `IDbContextFactory<ApplicationDbContext>` and injects the active tenant context into created contexts.

## 2. Request Resolution Pipeline

`TenantResolutionMiddleware` resolves tenants in the following priority order:

1. **Custom Domains**: Matches `Host` header against verified records in `TenantDomains`. Downstream routes require no prefix.
2. **Path-Prefix**: Matches `/t/{slug}/...`, sets `context.Request.PathBase = "/t/{slug}"` and rewrites `context.Request.Path`. Page route templates (like `@page "/query"`) match seamlessly.
3. **Platform Area**: Paths beginning with `/platform/...` are assigned to `WellKnownTenants.Platform` (`SuperAdmin` role).
4. **Header / JWT Claim Fallback**: Uses `X-Tenant-Slug` header or `tenant_slug` / `tenant_id` claims for API clients.
5. **Backward Compatibility**: If exactly one active tenant exists in the database, non-public routes automatically fallback to that tenant.

In `Components/App.razor`, the HTML `<base href>` dynamically resolves to the tenant path base:
```html
<base href="@(string.IsNullOrEmpty(HttpContext?.Request?.PathBase.Value) ? "/" : HttpContext.Request.PathBase.Value + "/")" />
```

## 3. Multi-Tenant User Memberships

- Users are represented globally by `ApplicationUser` in ASP.NET Core Identity.
- Membership in workspaces is governed by `UserTenantMembership` (linking `UserId` and `TenantId`) with a workspace role (`Admin`, `Developer`, `Auditor`, `Tester`).
- A user can belong to multiple workspaces simultaneously.
- Users switch workspaces via the topbar dropdown or the dedicated `/select-tenant` hub.
- When creating a user in `UserAdminService`, existing accounts are associated with the workspace rather than triggering email conflict errors.

## 4. Per-Tenant Settings & Governance

- `SettingEntry` has a composite primary key `(TenantId, Key)`.
- `SettingsService` and `WebhookService` automatically isolate SMTP mail servers, outgoing webhooks, and visual branding per workspace.
- The pre-execution audit row write in `ExecutionService` and `AuditService` captures `TenantId` on all entries.

## 5. Platform Administration (`SuperAdmin`)

- Platform control plane routes (`[Authorize(Roles = Roles.SuperAdmin)]`):
  - `/platform/tenants`: Create/edit/disable/delete workspaces, configure `MaxDatabases`/`MaxUsers` quotas, manage custom domains, and assign/remove workspace members and admins via `PlatformAdminService`.
  - `/platform/operators`: Promote existing users or create new `SuperAdmin` operators, and revoke operator access (protected against self-revocation and removing the last active `SuperAdmin`).
  - `/platform/health`: Cross-tenant database health, latency, quota usage, and 24-hour application error counts.
  - `/platform/audit`: Immutable `WellKnownTenants.Platform` audit trail for all platform operations.
  - `/platform/settings`: Global maintenance mode and custom domain suffix allowlists.
- `SuperAdmin` users have implicit `Admin` access across all tenant workspaces (`Roles.AdminOrSuperAdmin`, `PageBase.IsAdmin`, `AccessConfigurationService.CanAuditAsync`), while workspace `Admin`s can manage access inside their workspace (`/admin/users`) without creating workspaces or assigning `SuperAdmin`.
- Platform operations stamp `TenantId = WellKnownTenants.Platform` for platform audit and settings isolation.

## 6. SuperAdmin Setup & Promotion Options

There are four ways to configure or elevate an account to `SuperAdmin`:

### Method 1: Initial Web Setup (`/Account/Setup`)
When no users exist, completing `/Account/Setup` creates the root user with both `Roles.Admin` (on the `default` workspace) and `Roles.SuperAdmin`.

### Method 2: Platform Operators UI (`/platform/operators`)
An existing `SuperAdmin` can promote any user or create a new operator account directly in `/platform/operators`.

### Method 3: Bootstrap Configuration
`Program.cs` checks configuration keys `BootstrapAdmin:Email` and `BootstrapAdmin:Password` during application startup (`SeedAsync`).
- If the account does not exist, it is created and granted both `Roles.Admin` and `Roles.SuperAdmin`, and added as an `Admin` in the default workspace.
- If the account already exists, `SeedAsync` automatically checks for `Roles.SuperAdmin` and adds it without resetting the password.
- Environment variables: `BOOTSTRAP_ADMIN_EMAIL` and `BOOTSTRAP_ADMIN_PASSWORD` (Docker Compose), or `BootstrapAdmin__Email` and `BootstrapAdmin__Password` (systemd/CLI).

### Method 2: Direct PostgreSQL Database Assignment
When direct database access is available and restarting the application or changing environment variables is inconvenient:
```sql
-- 1. Ensure the SuperAdmin role exists
INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
VALUES (gen_random_uuid()::text, 'SuperAdmin', 'SUPERADMIN', gen_random_uuid()::text)
ON CONFLICT ("NormalizedName") DO NOTHING;

-- 2. Assign SuperAdmin role to the user
INSERT INTO "AspNetUserRoles" ("UserId", "RoleId")
SELECT u."Id", r."Id"
FROM "AspNetUsers" u
JOIN "AspNetRoles" r ON r."NormalizedName" = 'SUPERADMIN'
WHERE u."NormalizedEmail" = UPPER('admin@company.com')
ON CONFLICT DO NOTHING;
```
Note: Active user sessions must sign out and sign back in for the new role claim to take effect in the authentication cookie.

## 7. Workspace Provisioning & User Onboarding Workflow

Query Gate intentionally avoids open self-service signup to enforce strict governance over database access.

### Creating a Workspace
1. A user with the `SuperAdmin` role navigates to `/platform/tenants`.
2. Click **Create Workspace** and enter:
   - **Name**: Organization or team title (e.g., "Acme Finance").
   - **Slug**: URL identifier containing lowercase alphanumeric characters and hyphens (e.g., `acme-finance`), accessible at `/t/acme-finance/`.
   - **Contact Email**: Optional tenant contact.
   - **Plan**: Optional plan tier (e.g., "Enterprise").
   - **Initial Administrator**: Email of the user who will manage the workspace. If the account exists, they receive instant `Admin` membership in the new workspace.
3. Once created, custom domains can be attached via the **Domains** modal.

### Onboarding Users into Workspaces
1. Workspace administrators log into `/t/{slug}/dashboard`.
2. Navigate to **Admin -> Users** (`/admin/users`) and click **New user**.
3. If the email already exists in the system, `UserAdminService` adds a `UserTenantMembership` link for that workspace with the assigned role without email conflict errors.
4. If the user is new, an account is created with a generated temporary password and flagged with `MustChangePassword = true`.
5. Users switch between assigned workspaces using the topbar dropdown or `/select-tenant`.
