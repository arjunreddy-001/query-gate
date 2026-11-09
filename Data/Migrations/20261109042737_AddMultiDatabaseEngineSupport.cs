using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbAccessManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiDatabaseEngineSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Engine",
                table: "ManagedDatabases",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "SqlServer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Engine",
                table: "ManagedDatabases");
        }
    }
}
