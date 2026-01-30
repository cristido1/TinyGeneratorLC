using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeleteNextItemsToStoriesStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "delete_next_items",
                table: "stories_status",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-27T06:18:05.5620324Z", "2026-01-27T06:18:05.5620324Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-27T06:18:05.5620324Z", "2026-01-27T06:18:05.5620324Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-27T06:18:05.5620324Z", "2026-01-27T06:18:05.5620324Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-27T06:18:05.5620324Z", "2026-01-27T06:18:05.5620324Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 5,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-27T06:18:05.5620324Z", "2026-01-27T06:18:05.5620324Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 6,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-27T06:18:05.5620324Z", "2026-01-27T06:18:05.5620324Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 7,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-01-27T06:18:05.5620324Z", "2026-01-27T06:18:05.5620324Z" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delete_next_items",
                table: "stories_status");

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
    }
}
