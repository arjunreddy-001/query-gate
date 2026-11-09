using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbAccessManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddObjectLevelPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PermissionGrants_RoleId_DatabaseId_PermissionCode",
                table: "PermissionGrants");

            migrationBuilder.DropIndex(
                name: "IX_PermissionGrants_UserId_DatabaseId_PermissionCode",
                table: "PermissionGrants");

            migrationBuilder.AddColumn<string>(
                name: "ObjectName",
                table: "PermissionGrants",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Schema",
                table: "PermissionGrants",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ObjectName",
                table: "AccessRequests",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Schema",
                table: "AccessRequests",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_RoleId_DatabaseId_PermissionCode_Schema_ObjectName",
                table: "PermissionGrants",
                columns: new[] { "RoleId", "DatabaseId", "PermissionCode", "Schema", "ObjectName" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_UserId_DatabaseId_PermissionCode_Schema_ObjectName",
                table: "PermissionGrants",
                columns: new[] { "UserId", "DatabaseId", "PermissionCode", "Schema", "ObjectName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PermissionGrants_RoleId_DatabaseId_PermissionCode_Schema_ObjectName",
                table: "PermissionGrants");

            migrationBuilder.DropIndex(
                name: "IX_PermissionGrants_UserId_DatabaseId_PermissionCode_Schema_ObjectName",
                table: "PermissionGrants");

            migrationBuilder.DropColumn(
                name: "ObjectName",
                table: "PermissionGrants");

            migrationBuilder.DropColumn(
                name: "Schema",
                table: "PermissionGrants");

            migrationBuilder.DropColumn(
                name: "ObjectName",
                table: "AccessRequests");

            migrationBuilder.DropColumn(
                name: "Schema",
                table: "AccessRequests");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_RoleId_DatabaseId_PermissionCode",
                table: "PermissionGrants",
                columns: new[] { "RoleId", "DatabaseId", "PermissionCode" });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrants_UserId_DatabaseId_PermissionCode",
                table: "PermissionGrants",
                columns: new[] { "UserId", "DatabaseId", "PermissionCode" });
        }
    }
}
