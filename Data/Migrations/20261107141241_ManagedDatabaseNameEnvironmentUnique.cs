using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbAccessManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagedDatabaseNameEnvironmentUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ManagedDatabases_Name",
                table: "ManagedDatabases");

            migrationBuilder.CreateIndex(
                name: "IX_ManagedDatabases_Name_Environment",
                table: "ManagedDatabases",
                columns: new[] { "Name", "Environment" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ManagedDatabases_Name_Environment",
                table: "ManagedDatabases");

            migrationBuilder.CreateIndex(
                name: "IX_ManagedDatabases_Name",
                table: "ManagedDatabases",
                column: "Name",
                unique: true);
        }
    }
}
