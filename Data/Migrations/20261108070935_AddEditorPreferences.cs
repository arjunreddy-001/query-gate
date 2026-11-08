using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DbAccessManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEditorPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EditorAutomaticSuggestions",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "EditorFontSize",
                table: "AspNetUsers",
                type: "int",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<bool>(
                name: "EditorLiveDiagnostics",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditorAutomaticSuggestions",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EditorFontSize",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "EditorLiveDiagnostics",
                table: "AspNetUsers");
        }
    }
}
