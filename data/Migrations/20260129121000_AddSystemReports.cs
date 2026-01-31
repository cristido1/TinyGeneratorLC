using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    [DbContext(typeof(TinyGenerator.Data.TinyGeneratorDbContext))]
    [Migration("20260129121000_AddSystemReports")]
    public partial class AddSystemReports : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "system_reports",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    severity = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    deleted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    deleted_at = table.Column<string>(type: "TEXT", nullable: true),
                    deleted_by = table.Column<string>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", nullable: true),
                    message = table.Column<string>(type: "TEXT", nullable: true),
                    failure_reason = table.Column<string>(type: "TEXT", nullable: true),
                    agent_name = table.Column<string>(type: "TEXT", nullable: true),
                    agent_role = table.Column<string>(type: "TEXT", nullable: true),
                    model_name = table.Column<string>(type: "TEXT", nullable: true),
                    story_id = table.Column<long>(type: "INTEGER", nullable: true),
                    series_id = table.Column<int>(type: "INTEGER", nullable: true),
                    series_episode = table.Column<int>(type: "INTEGER", nullable: true),
                    operation_type = table.Column<string>(type: "TEXT", nullable: true),
                    execution_time_ms = table.Column<int>(type: "INTEGER", nullable: true),
                    retry_count = table.Column<int>(type: "INTEGER", nullable: true),
                    raw_log_ref = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_reports", x => x.id);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "system_reports");
        }
    }
}
