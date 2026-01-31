using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    [DbContext(typeof(TinyGenerator.Data.TinyGeneratorDbContext))]
    [Migration("20260129120000_AddModelNameToLog")]
    public partial class AddModelNameToLog : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "model_name",
                table: "Log",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "model_name",
                table: "Log");
        }
    }
}
