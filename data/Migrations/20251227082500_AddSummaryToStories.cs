using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSummaryToStories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "summary",
                table: "stories",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "summary",
                table: "stories");
        }
    }
}
