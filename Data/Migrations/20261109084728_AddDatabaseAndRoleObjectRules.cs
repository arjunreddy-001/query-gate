using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbAccessManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDatabaseAndRoleObjectRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExcludedObjects",
                table: "ManagedDatabases",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncludedObjects",
                table: "ManagedDatabases",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DatabaseRoleScopeRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DatabaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    IncludedObjects = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ExcludedObjects = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseRoleScopeRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseRoleScopeRules_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DatabaseRoleScopeRules_ManagedDatabases_DatabaseId",
                        column: x => x.DatabaseId,
                        principalTable: "ManagedDatabases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseRoleScopeRules_DatabaseId_RoleId",
                table: "DatabaseRoleScopeRules",
                columns: new[] { "DatabaseId", "RoleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseRoleScopeRules_RoleId",
                table: "DatabaseRoleScopeRules",
                column: "RoleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseRoleScopeRules");

            migrationBuilder.DropColumn(
                name: "ExcludedObjects",
                table: "ManagedDatabases");

            migrationBuilder.DropColumn(
                name: "IncludedObjects",
                table: "ManagedDatabases");
        }
    }
}
