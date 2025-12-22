using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDisabledToTtsVoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "disabled",
                table: "tts_voices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // 'title' column already exists in 'stories' table; skip adding it here.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "disabled",
                table: "tts_voices");

            migrationBuilder.DropColumn(
                name: "title",
                table: "stories");
        }
    }
}
