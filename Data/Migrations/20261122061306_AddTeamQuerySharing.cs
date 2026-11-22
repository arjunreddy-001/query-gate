using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbAccessManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamQuerySharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "SavedQueries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "SavedQueries",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsShared",
                table: "SavedQueries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_SavedQueries_TenantId_DatabaseId_IsShared",
                table: "SavedQueries",
                columns: new[] { "TenantId", "DatabaseId", "IsShared" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SavedQueries_TenantId_DatabaseId_IsShared",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "SavedQueries");

            migrationBuilder.DropColumn(
                name: "IsShared",
                table: "SavedQueries");
        }
    }
}
