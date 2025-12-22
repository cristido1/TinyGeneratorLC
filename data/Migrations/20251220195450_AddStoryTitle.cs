using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.data.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "stories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "title",
                table: "stories");
        }
    }
}
