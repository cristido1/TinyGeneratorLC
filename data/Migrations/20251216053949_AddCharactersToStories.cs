using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.data.Migrations
{
    /// <inheritdoc />
    public partial class AddCharactersToStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "characters",
                table: "stories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "characters",
                table: "stories");
        }
    }
}
