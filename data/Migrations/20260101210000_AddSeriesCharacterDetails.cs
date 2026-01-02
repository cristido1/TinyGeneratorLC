using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesCharacterDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "conflitto_interno",
                table: "series_characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "eta",
                table: "series_characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "formazione",
                table: "series_characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "profilo",
                table: "series_characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "specializzazione",
                table: "series_characters",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "conflitto_interno",
                table: "series_characters");

            migrationBuilder.DropColumn(
                name: "eta",
                table: "series_characters");

            migrationBuilder.DropColumn(
                name: "formazione",
                table: "series_characters");

            migrationBuilder.DropColumn(
                name: "profilo",
                table: "series_characters");

            migrationBuilder.DropColumn(
                name: "specializzazione",
                table: "series_characters");
        }
    }
}
