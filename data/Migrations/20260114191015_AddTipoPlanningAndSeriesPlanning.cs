using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.data.Migrations
{
    /// <inheritdoc />
    public partial class AddTipoPlanningAndSeriesPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "tipo_planning_id",
                table: "series_episodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "default_tipo_planning_id",
                table: "series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "planner_method_id",
                table: "series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tipo_planning",
                columns: table => new
                {
                    id_tipo_planning = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    codice = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    nome = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    descrizione = table.Column<string>(type: "TEXT", nullable: true),
                    successione_stati = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tipo_planning", x => x.id_tipo_planning);
                });

            migrationBuilder.CreateIndex(
                name: "IX_series_episodes_tipo_planning_id",
                table: "series_episodes",
                column: "tipo_planning_id");

            migrationBuilder.CreateIndex(
                name: "IX_series_default_tipo_planning_id",
                table: "series",
                column: "default_tipo_planning_id");

            migrationBuilder.CreateIndex(
                name: "IX_series_planner_method_id",
                table: "series",
                column: "planner_method_id");

            migrationBuilder.CreateIndex(
                name: "IX_tipo_planning_codice",
                table: "tipo_planning",
                column: "codice",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_series_planner_methods_planner_method_id",
                table: "series",
                column: "planner_method_id",
                principalTable: "planner_methods",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_series_tipo_planning_default_tipo_planning_id",
                table: "series",
                column: "default_tipo_planning_id",
                principalTable: "tipo_planning",
                principalColumn: "id_tipo_planning");

            migrationBuilder.AddForeignKey(
                name: "FK_series_episodes_tipo_planning_tipo_planning_id",
                table: "series_episodes",
                column: "tipo_planning_id",
                principalTable: "tipo_planning",
                principalColumn: "id_tipo_planning");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_series_planner_methods_planner_method_id",
                table: "series");

            migrationBuilder.DropForeignKey(
                name: "FK_series_tipo_planning_default_tipo_planning_id",
                table: "series");

            migrationBuilder.DropForeignKey(
                name: "FK_series_episodes_tipo_planning_tipo_planning_id",
                table: "series_episodes");

            migrationBuilder.DropIndex(
                name: "IX_tipo_planning_codice",
                table: "tipo_planning");

            migrationBuilder.DropTable(
                name: "tipo_planning");

            migrationBuilder.DropIndex(
                name: "IX_series_episodes_tipo_planning_id",
                table: "series_episodes");

            migrationBuilder.DropIndex(
                name: "IX_series_default_tipo_planning_id",
                table: "series");

            migrationBuilder.DropIndex(
                name: "IX_series_planner_method_id",
                table: "series");

            migrationBuilder.DropColumn(
                name: "tipo_planning_id",
                table: "series_episodes");

            migrationBuilder.DropColumn(
                name: "default_tipo_planning_id",
                table: "series");

            migrationBuilder.DropColumn(
                name: "planner_method_id",
                table: "series");
        }
    }
}
