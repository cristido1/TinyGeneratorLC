using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStepNumberToLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Only add StepNumber and MaxStep columns to Log table
            // Other tables already exist (created by Dapper) and are skipped
            migrationBuilder.AddColumn<int>(
                name: "MaxStep",
                table: "Log",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StepNumber",
                table: "Log",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxStep",
                table: "Log");

            migrationBuilder.DropColumn(
                name: "StepNumber",
                table: "Log");
        }
    }
}
