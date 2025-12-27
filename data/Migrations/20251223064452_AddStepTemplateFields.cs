using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepTemplateFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "full_story_step",
                table: "step_templates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "min_chars_story",
                table: "step_templates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "min_chars_trama",
                table: "step_templates",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "full_story_step",
                table: "step_templates");

            migrationBuilder.DropColumn(
                name: "min_chars_story",
                table: "step_templates");

            migrationBuilder.DropColumn(
                name: "min_chars_trama",
                table: "step_templates");
        }
    }
}
