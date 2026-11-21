using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbAccessManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Settings",
                table: "Settings");

            migrationBuilder.DropIndex(
                name: "IX_ScriptRequests_Status_CreatedAt",
                table: "ScriptRequests");

            migrationBuilder.DropIndex(
                name: "IX_SavedQueries_UserId_DatabaseId_Name",
                table: "SavedQueries");

            migrationBuilder.DropIndex(
                name: "IX_RequestStatusHistory_RequestType_RequestId",
                table: "RequestStatusHistory");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_QueryHistory_UserId_ExecutedAt",
                table: "QueryHistory");

            migrationBuilder.DropIndex(
                name: "IX_PermissionGrants_RoleId_DatabaseId_PermissionCode_Schema_Ob~",
                table: "PermissionGrants");

            migrationBuilder.DropIndex(
                name: "IX_PermissionGrants_UserId_DatabaseId_PermissionCode_Schema_Ob~",
                table: "PermissionGrants");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_UserId_IsRead_CreatedAt",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_ManagedDatabases_Name_Environment",
                table: "ManagedDatabases");

            migrationBuilder.DropPrimaryKey(
                name: "PK_EnvironmentDefaultGrants",
                table: "EnvironmentDefaultGrants");

            migrationBuilder.DropIndex(
                name: "IX_DatabaseRoleScopeRules_DatabaseId_RoleId",
                table: "DatabaseRoleScopeRules");

            migrationBuilder.DropIndex(
                name: "IX_DatabaseEnvironments_Name",
                table: "DatabaseEnvironments");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_Timestamp",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_UserId_DatabaseId_Timestamp",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AppLogs_Timestamp",
                table: "AppLogs");

            migrationBuilder.DropIndex(
                name: "IX_AccessRequests_Status_CreatedAt",
                table: "AccessRequests");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Settings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ScriptRequests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SavedQueries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "RequestStatusHistory",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "RefreshTokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "QueryHistory",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "PermissionGrants",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Notifications",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "ManagedDatabases",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "EnvironmentDefaultGrants",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "DatabaseRoleScopeRules",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "DatabaseEnvironments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AuditLogs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppLogs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AccessRequests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_Settings",
                table: "Settings",
                columns: new[] { "TenantId", "Key" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_EnvironmentDefaultGrants",
                table: "EnvironmentDefaultGrants",
                columns: new[] { "TenantId", "EnvironmentId", "RoleId", "PermissionCode" });

            migrationBuilder.CreateTable(
                name: "TenantDomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDomains", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantRoles",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantRoles", x => new { x.TenantId, x.RoleId });
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ContactEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Plan = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserTenantMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTenantMemberships", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScriptRequests_TenantId_Status_CreatedAt",
                table: "ScriptRequests",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedQueries_TenantId_UserId_DatabaseId_Name",
                table: "SavedQueries",
                columns: new[] { "TenantId", "UserId", "DatabaseId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestStatusHistory_TenantId_RequestType_RequestId",
                table: "RequestStatusHistory",
                columns: new[] { "TenantId", "RequestType", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TenantId_UserId",
                table: "RefreshTokens",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryHistory_TenantId_UserId_ExecutedAt",
                table: "QueryHistory",
                columns: new[] { "TenantId", "UserId", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_RoleId",
                table: "PermissionGrants",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_TenantId_RoleId_DatabaseId_PermissionCode_~",
                table: "PermissionGrants",
                columns: new[] { "TenantId", "RoleId", "DatabaseId", "PermissionCode", "Schema", "ObjectName" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_TenantId_UserId_DatabaseId_PermissionCode_~",
                table: "PermissionGrants",
                columns: new[] { "TenantId", "UserId", "DatabaseId", "PermissionCode", "Schema", "ObjectName" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_UserId",
                table: "PermissionGrants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TenantId_UserId_IsRead_CreatedAt",
                table: "Notifications",
                columns: new[] { "TenantId", "UserId", "IsRead", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedDatabases_TenantId_Name_Environment",
                table: "ManagedDatabases",
                columns: new[] { "TenantId", "Name", "Environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentDefaultGrants_EnvironmentId",
                table: "EnvironmentDefaultGrants",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseRoleScopeRules_DatabaseId",
                table: "DatabaseRoleScopeRules",
                column: "DatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseRoleScopeRules_TenantId_DatabaseId_RoleId",
                table: "DatabaseRoleScopeRules",
                columns: new[] { "TenantId", "DatabaseId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseEnvironments_TenantId_Name",
                table: "DatabaseEnvironments",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_Timestamp",
                table: "AuditLogs",
                columns: new[] { "TenantId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TenantId_UserId_DatabaseId_Timestamp",
                table: "AuditLogs",
                columns: new[] { "TenantId", "UserId", "DatabaseId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AppLogs_TenantId_Timestamp",
                table: "AppLogs",
                columns: new[] { "TenantId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_TenantId_Status_CreatedAt",
                table: "AccessRequests",
                columns: new[] { "TenantId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantDomains_Domain",
                table: "TenantDomains",
                column: "Domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantDomains_TenantId",
                table: "TenantDomains",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTenantMemberships_UserId_TenantId",
                table: "UserTenantMemberships",
                columns: new[] { "UserId", "TenantId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantDomains");

            migrationBuilder.DropTable(
                name: "TenantRoles");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "UserTenantMemberships");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Settings",
                table: "Settings");

            migrationBuilder.DropIndex(
                name: "IX_ScriptRequests_TenantId_Status_CreatedAt",
                table: "ScriptRequests");

            migrationBuilder.DropIndex(
                name: "IX_SavedQueries_TenantId_UserId_DatabaseId_Name",
                table: "SavedQueries");

            migrationBuilder.DropIndex(
                name: "IX_RequestStatusHistory_TenantId_RequestType_RequestId",
                table: "RequestStatusHistory");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_TenantId_UserId",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_QueryHistory_TenantId_UserId_ExecutedAt",
                table: "QueryHistory");

            migrationBuilder.DropIndex(
                name: "IX_PermissionGrants_RoleId",
                table: "PermissionGrants");

            migrationBuilder.DropIndex(
                name: "IX_PermissionGrants_TenantId_RoleId_DatabaseId_PermissionCode_~",
                table: "PermissionGrants");

            migrationBuilder.DropIndex(
                name: "IX_PermissionGrants_TenantId_UserId_DatabaseId_PermissionCode_~",
                table: "PermissionGrants");

            migrationBuilder.DropIndex(
                name: "IX_PermissionGrants_UserId",
                table: "PermissionGrants");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_TenantId_UserId_IsRead_CreatedAt",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_ManagedDatabases_TenantId_Name_Environment",
                table: "ManagedDatabases");

            migrationBuilder.DropPrimaryKey(
                name: "PK_EnvironmentDefaultGrants",
                table: "EnvironmentDefaultGrants");

            migrationBuilder.DropIndex(
                name: "IX_EnvironmentDefaultGrants_EnvironmentId",
                table: "EnvironmentDefaultGrants");

            migrationBuilder.DropIndex(
                name: "IX_DatabaseRoleScopeRules_DatabaseId",
                table: "DatabaseRoleScopeRules");

            migrationBuilder.DropIndex(
                name: "IX_DatabaseRoleScopeRules_TenantId_DatabaseId_RoleId",
                table: "DatabaseRoleScopeRules");

            migrationBuilder.DropIndex(
                name: "IX_DatabaseEnvironments_TenantId_Name",
                table: "DatabaseEnvironments");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_TenantId_Timestamp",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_TenantId_UserId_DatabaseId_Timestamp",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AppLogs_TenantId_Timestamp",
                table: "AppLogs");

            migrationBuilder.DropIndex(
                name: "IX_AccessRequests_TenantId_Status_CreatedAt",
                table: "AccessRequests");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ScriptRequests");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "RequestStatusHistory");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "QueryHistory");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "PermissionGrants");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "ManagedDatabases");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "EnvironmentDefaultGrants");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DatabaseRoleScopeRules");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DatabaseEnvironments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AccessRequests");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Settings",
                table: "Settings",
                column: "Key");

            migrationBuilder.AddPrimaryKey(
                name: "PK_EnvironmentDefaultGrants",
                table: "EnvironmentDefaultGrants",
                columns: new[] { "EnvironmentId", "RoleId", "PermissionCode" });

            migrationBuilder.CreateIndex(
                name: "IX_ScriptRequests_Status_CreatedAt",
                table: "ScriptRequests",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedQueries_UserId_DatabaseId_Name",
                table: "SavedQueries",
                columns: new[] { "UserId", "DatabaseId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestStatusHistory_RequestType_RequestId",
                table: "RequestStatusHistory",
                columns: new[] { "RequestType", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryHistory_UserId_ExecutedAt",
                table: "QueryHistory",
                columns: new[] { "UserId", "ExecutedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_RoleId_DatabaseId_PermissionCode_Schema_Ob~",
                table: "PermissionGrants",
                columns: new[] { "RoleId", "DatabaseId", "PermissionCode", "Schema", "ObjectName" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_UserId_DatabaseId_PermissionCode_Schema_Ob~",
                table: "PermissionGrants",
                columns: new[] { "UserId", "DatabaseId", "PermissionCode", "Schema", "ObjectName" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_IsRead_CreatedAt",
                table: "Notifications",
                columns: new[] { "UserId", "IsRead", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedDatabases_Name_Environment",
                table: "ManagedDatabases",
                columns: new[] { "Name", "Environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseRoleScopeRules_DatabaseId_RoleId",
                table: "DatabaseRoleScopeRules",
                columns: new[] { "DatabaseId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseEnvironments_Name",
                table: "DatabaseEnvironments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Timestamp",
                table: "AuditLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_UserId_DatabaseId_Timestamp",
                table: "AuditLogs",
                columns: new[] { "UserId", "DatabaseId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AppLogs_Timestamp",
                table: "AppLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_Status_CreatedAt",
                table: "AccessRequests",
                columns: new[] { "Status", "CreatedAt" });
        }
    }
}
