using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameStoryToStoryRawAddTaggedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "story",
                table: "stories",
                newName: "story_raw");

            migrationBuilder.AddColumn<string>(
                name: "story_tagged",
                table: "stories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "story_tagged_version",
                table: "stories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "formatter_model",
                table: "stories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "formatter_prompt_hash",
                table: "stories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "story_tagged",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "story_tagged_version",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "formatter_model",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "formatter_prompt_hash",
                table: "stories");

            migrationBuilder.RenameColumn(
                name: "story_raw",
                table: "stories",
                newName: "story");
        }
    }
}
