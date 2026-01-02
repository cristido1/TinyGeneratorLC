using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesCharacters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "series_characters",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    serie_id = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    gender = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    voice_id = table.Column<int>(type: "INTEGER", nullable: true),
                    episode_in = table.Column<int>(type: "INTEGER", nullable: true),
                    episode_out = table.Column<int>(type: "INTEGER", nullable: true),
                    image = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_series_characters", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_series_characters_serie_id",
                table: "series_characters",
                column: "serie_id");

            migrationBuilder.CreateIndex(
                name: "IX_series_characters_voice_id",
                table: "series_characters",
                column: "voice_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "series_characters");
        }
    }
}
