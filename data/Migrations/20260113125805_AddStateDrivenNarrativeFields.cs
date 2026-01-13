using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStateDrivenNarrativeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "planner_mode",
                table: "stories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "default_planner_mode",
                table: "series",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pov_list_json",
                table: "narrative_profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "narrative_profiles",
                keyColumn: "id",
                keyValue: 1,
                column: "pov_list_json",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "planner_mode",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "default_planner_mode",
                table: "series");

            migrationBuilder.DropColumn(
                name: "pov_list_json",
                table: "narrative_profiles");
        }
    }
}
