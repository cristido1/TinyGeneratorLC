using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesCharacterAspect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "aspect",
                table: "series_characters",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "aspect",
                table: "series_characters");
        }
    }
}
