using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbAccessManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurableRolesAndEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseEnvironments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseEnvironments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnvironmentDefaultGrants",
                columns: table => new
                {
                    EnvironmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    PermissionCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentDefaultGrants", x => new { x.EnvironmentId, x.RoleId, x.PermissionCode });
                    table.ForeignKey(
                        name: "FK_EnvironmentDefaultGrants_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnvironmentDefaultGrants_DatabaseEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "DatabaseEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseEnvironments_Name",
                table: "DatabaseEnvironments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentDefaultGrants_RoleId",
                table: "EnvironmentDefaultGrants",
                column: "RoleId");

            migrationBuilder.Sql("""
                INSERT INTO AspNetRoles (Id, Name, NormalizedName, ConcurrencyStamp)
                SELECT CONVERT(nvarchar(36), NEWID()), v.Name, UPPER(v.Name), CONVERT(nvarchar(36), NEWID())
                FROM (VALUES ('Admin'), ('Developer'), ('Tester'), ('Auditor')) v(Name)
                WHERE NOT EXISTS (SELECT 1 FROM AspNetRoles r WHERE r.NormalizedName = UPPER(v.Name));
                INSERT INTO DatabaseEnvironments (Id, Name)
                SELECT NEWID(), Name FROM (
                    SELECT Environment AS Name FROM ManagedDatabases
                    UNION SELECT 'Development' UNION SELECT 'QA' UNION SELECT 'Production'
                ) e;
                INSERT INTO EnvironmentDefaultGrants (EnvironmentId, RoleId, PermissionCode)
                SELECT e.Id, r.Id, v.PermissionCode
                FROM (VALUES
                    ('Development','Developer','SELECT'), ('Development','Developer','INSERT'),
                    ('Development','Developer','UPDATE'), ('Development','Developer','DELETE'),
                    ('Development','Developer','EXECUTE'), ('Development','Developer','SP_HELPTEXT'),
                    ('QA','Developer','SELECT'), ('QA','Tester','SELECT'), ('QA','Tester','SP_HELPTEXT')
                ) v(EnvironmentName, RoleName, PermissionCode)
                JOIN DatabaseEnvironments e ON e.Name = v.EnvironmentName
                JOIN AspNetRoles r ON r.NormalizedName = UPPER(v.RoleName);
                INSERT INTO AspNetRoleClaims (RoleId, ClaimType, ClaimValue)
                SELECT r.Id, 'querygate:capability', 'audit'
                FROM AspNetRoles r
                WHERE r.NormalizedName = 'AUDITOR' AND NOT EXISTS
                    (SELECT 1 FROM AspNetRoleClaims c WHERE c.RoleId = r.Id AND c.ClaimType = 'querygate:capability' AND c.ClaimValue = 'audit');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnvironmentDefaultGrants");

            migrationBuilder.DropTable(
                name: "DatabaseEnvironments");
        }
    }
}
