using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSerieFieldsToStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "serie_episode",
                table: "stories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "serie_id",
                table: "stories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_stories_serie_id",
                table: "stories",
                column: "serie_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_stories_serie_id",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "serie_episode",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "serie_id",
                table: "stories");
        }
    }
}
