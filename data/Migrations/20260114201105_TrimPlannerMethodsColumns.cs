using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.data.Migrations
{
    /// <inheritdoc />
    public partial class TrimPlannerMethodsColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ensure we don't fail if the index already exists (it may come from an earlier raw-SQL migration)
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_planner_methods_code;");

            migrationBuilder.DropColumn(
                name: "beat_count",
                table: "planner_methods");

            migrationBuilder.DropColumn(
                name: "category",
                table: "planner_methods");

            migrationBuilder.DropColumn(
                name: "name",
                table: "planner_methods");

            migrationBuilder.DropColumn(
                name: "planner_prompt",
                table: "planner_methods");

            migrationBuilder.DropColumn(
                name: "recommended_genres",
                table: "planner_methods");

            migrationBuilder.DropColumn(
                name: "strengths",
                table: "planner_methods");

            migrationBuilder.DropColumn(
                name: "structure_schema",
                table: "planner_methods");

            migrationBuilder.DropColumn(
                name: "supports_series",
                table: "planner_methods");

            migrationBuilder.DropColumn(
                name: "validation_rules",
                table: "planner_methods");

            migrationBuilder.DropColumn(
                name: "weaknesses",
                table: "planner_methods");

            migrationBuilder.CreateIndex(
                name: "IX_planner_methods_code",
                table: "planner_methods",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_planner_methods_code",
                table: "planner_methods");

            migrationBuilder.AddColumn<int>(
                name: "beat_count",
                table: "planner_methods",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "planner_methods",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "planner_methods",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "planner_prompt",
                table: "planner_methods",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recommended_genres",
                table: "planner_methods",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "strengths",
                table: "planner_methods",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "structure_schema",
                table: "planner_methods",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "supports_series",
                table: "planner_methods",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "validation_rules",
                table: "planner_methods",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "weaknesses",
                table: "planner_methods",
                type: "TEXT",
                nullable: true);
        }
    }
}
