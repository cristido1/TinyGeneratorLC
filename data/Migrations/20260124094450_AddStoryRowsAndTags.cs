using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.data.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryRowsAndTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "story_rows",
                table: "stories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "story_tags",
                table: "stories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-24T09:44:50.2180354Z", "2026-01-24T09:44:50.2180354Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-24T09:44:50.2180354Z", "2026-01-24T09:44:50.2180354Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-24T09:44:50.2180354Z", "2026-01-24T09:44:50.2180354Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-24T09:44:50.2180354Z", "2026-01-24T09:44:50.2180354Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 5,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-24T09:44:50.2180354Z", "2026-01-24T09:44:50.2180354Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 6,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-24T09:44:50.2180354Z", "2026-01-24T09:44:50.2180354Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 7,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-24T09:44:50.2180354Z", "2026-01-24T09:44:50.2180354Z" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "story_rows",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "story_tags",
                table: "stories");

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-23T13:31:40.2134749Z", "2026-01-23T13:31:40.2134749Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-23T13:31:40.2134749Z", "2026-01-23T13:31:40.2134749Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-23T13:31:40.2134749Z", "2026-01-23T13:31:40.2134749Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-23T13:31:40.2134749Z", "2026-01-23T13:31:40.2134749Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 5,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-23T13:31:40.2134749Z", "2026-01-23T13:31:40.2134749Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 6,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-23T13:31:40.2134749Z", "2026-01-23T13:31:40.2134749Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 7,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-23T13:31:40.2134749Z", "2026-01-23T13:31:40.2134749Z" });
        }
    }
}
