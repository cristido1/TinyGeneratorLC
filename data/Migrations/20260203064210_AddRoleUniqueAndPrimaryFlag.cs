using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRoleUniqueAndPrimaryFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "auto_tts_fail_count",
                table: "stories",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "auto_tts_failed",
                table: "stories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "auto_tts_failed_at",
                table: "stories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "auto_tts_last_error",
                table: "stories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "deleted",
                table: "stories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "canon_events",
                table: "series_episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delta_json",
                table: "series_episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "open_threads_out",
                table: "series_episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recap_text",
                table: "series_episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state_in_json",
                table: "series_episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state_out_json",
                table: "series_episodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "alleanza_relazione",
                table: "series_characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "arco_personale",
                table: "series_characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "last_seen_episode_number",
                table: "series_characters",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ruolo_narrativo",
                table: "series_characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stato_attuale",
                table: "series_characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stato_attuale_json",
                table: "series_characters",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cosa_non_deve_mai_succedere",
                table: "series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_major_event",
                table: "series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "livello_tecnologico_medio",
                table: "series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "serie_state_summary",
                table: "series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "temi_obbligatori",
                table: "series",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "world_rules_locked",
                table: "series",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_primary",
                table: "model_roles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<int>(
                name: "ThreadId",
                table: "Log",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<bool>(
                name: "Examined",
                table: "Log",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ResultFailReason",
                table: "Log",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "series_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    serie_id = table.Column<int>(type: "INTEGER", nullable: false),
                    is_current = table.Column<bool>(type: "INTEGER", nullable: false),
                    state_version = table.Column<int>(type: "INTEGER", nullable: false),
                    state_summary = table.Column<string>(type: "TEXT", nullable: true),
                    world_state_json = table.Column<string>(type: "TEXT", nullable: true),
                    open_threads_json = table.Column<string>(type: "TEXT", nullable: true),
                    last_major_event = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: true),
                    created_by = table.Column<string>(type: "TEXT", nullable: true),
                    source_episode_id = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_series_state", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stats_models",
                columns: table => new
                {
                    model_name = table.Column<string>(type: "TEXT", nullable: false),
                    operation = table.Column<string>(type: "TEXT", nullable: false),
                    count_used = table.Column<int>(type: "INTEGER", nullable: true),
                    count_successed = table.Column<int>(type: "INTEGER", nullable: true),
                    count_failed = table.Column<int>(type: "INTEGER", nullable: true),
                    total_success_time_secs = table.Column<double>(type: "REAL", nullable: true),
                    total_fail_time_secs = table.Column<double>(type: "REAL", nullable: true),
                    last_operation_date = table.Column<string>(type: "TEXT", nullable: true),
                    first_operation_date = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stats_models", x => new { x.model_name, x.operation });
                });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 1,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-02-03T06:42:10.4480547Z", "2026-02-03T06:42:10.4480547Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 2,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-02-03T06:42:10.4480547Z", "2026-02-03T06:42:10.4480547Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 3,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-02-03T06:42:10.4480547Z", "2026-02-03T06:42:10.4480547Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 4,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-02-03T06:42:10.4480547Z", "2026-02-03T06:42:10.4480547Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 5,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-02-03T06:42:10.4480547Z", "2026-02-03T06:42:10.4480547Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 6,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-02-03T06:42:10.4480547Z", "2026-02-03T06:42:10.4480547Z" });

            migrationBuilder.UpdateData(
                table: "roles",
                keyColumn: "id",
                keyValue: 7,
                columns: new[] { "created_at", "updated_at" },
                values: new object[] { "2026-02-03T06:42:10.4480547Z", "2026-02-03T06:42:10.4480547Z" });

            // Ensure default long-story roles exist without assuming specific IDs.
            // Some databases may already contain these roles created by application seeding.
            migrationBuilder.Sql(@"
INSERT INTO roles (ruolo, comando_collegato, created_at, updated_at)
SELECT 'canon_extractor', 'CanonExtractor', '2026-02-03T06:42:10.4480547Z', '2026-02-03T06:42:10.4480547Z'
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE ruolo = 'canon_extractor');

INSERT INTO roles (ruolo, comando_collegato, created_at, updated_at)
SELECT 'state_delta_builder', 'StateDeltaBuilder', '2026-02-03T06:42:10.4480547Z', '2026-02-03T06:42:10.4480547Z'
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE ruolo = 'state_delta_builder');

INSERT INTO roles (ruolo, comando_collegato, created_at, updated_at)
SELECT 'continuity_validator', 'ContinuityValidator', '2026-02-03T06:42:10.4480547Z', '2026-02-03T06:42:10.4480547Z'
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE ruolo = 'continuity_validator');

INSERT INTO roles (ruolo, comando_collegato, created_at, updated_at)
SELECT 'state_updater', 'StateUpdater', '2026-02-03T06:42:10.4480547Z', '2026-02-03T06:42:10.4480547Z'
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE ruolo = 'state_updater');

INSERT INTO roles (ruolo, comando_collegato, created_at, updated_at)
SELECT 'state_compressor', 'StateCompressor', '2026-02-03T06:42:10.4480547Z', '2026-02-03T06:42:10.4480547Z'
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE ruolo = 'state_compressor');

INSERT INTO roles (ruolo, comando_collegato, created_at, updated_at)
SELECT 'recap_builder', 'RecapBuilder', '2026-02-03T06:42:10.4480547Z', '2026-02-03T06:42:10.4480547Z'
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE ruolo = 'recap_builder');
");

            // Deduplicate roles before creating the unique index.
            // Keep the smallest id per ruolo, update foreign keys in model_roles, then delete extras.
            migrationBuilder.Sql(@"
CREATE TEMP TABLE IF NOT EXISTS __roles_dedup_keep AS
SELECT ruolo, MIN(id) AS keep_id
FROM roles
GROUP BY ruolo
HAVING COUNT(*) > 1;

CREATE TEMP TABLE IF NOT EXISTS __roles_dedup_map AS
SELECT r.id AS old_id, k.keep_id AS keep_id
FROM roles r
JOIN __roles_dedup_keep k ON k.ruolo = r.ruolo
WHERE r.id <> k.keep_id;

UPDATE model_roles
SET role_id = (SELECT keep_id FROM __roles_dedup_map WHERE old_id = model_roles.role_id)
WHERE role_id IN (SELECT old_id FROM __roles_dedup_map);

DELETE FROM roles
WHERE id IN (SELECT old_id FROM __roles_dedup_map);

DROP TABLE __roles_dedup_map;
DROP TABLE __roles_dedup_keep;
");

            migrationBuilder.CreateIndex(
                name: "IX_roles_ruolo",
                table: "roles",
                column: "ruolo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_series_state_serie_id",
                table: "series_state",
                column: "serie_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "series_state");

            migrationBuilder.DropTable(
                name: "stats_models");

            migrationBuilder.DropIndex(
                name: "IX_roles_ruolo",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "auto_tts_fail_count",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "auto_tts_failed",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "auto_tts_failed_at",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "auto_tts_last_error",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "deleted",
                table: "stories");

            migrationBuilder.DropColumn(
                name: "canon_events",
                table: "series_episodes");

            migrationBuilder.DropColumn(
                name: "delta_json",
                table: "series_episodes");

            migrationBuilder.DropColumn(
                name: "open_threads_out",
                table: "series_episodes");

            migrationBuilder.DropColumn(
                name: "recap_text",
                table: "series_episodes");

            migrationBuilder.DropColumn(
                name: "state_in_json",
                table: "series_episodes");

            migrationBuilder.DropColumn(
                name: "state_out_json",
                table: "series_episodes");

            migrationBuilder.DropColumn(
                name: "alleanza_relazione",
                table: "series_characters");

            migrationBuilder.DropColumn(
                name: "arco_personale",
                table: "series_characters");

            migrationBuilder.DropColumn(
                name: "last_seen_episode_number",
                table: "series_characters");

            migrationBuilder.DropColumn(
                name: "ruolo_narrativo",
                table: "series_characters");

            migrationBuilder.DropColumn(
                name: "stato_attuale",
                table: "series_characters");

            migrationBuilder.DropColumn(
                name: "stato_attuale_json",
                table: "series_characters");

            migrationBuilder.DropColumn(
                name: "cosa_non_deve_mai_succedere",
                table: "series");

            migrationBuilder.DropColumn(
                name: "last_major_event",
                table: "series");

            migrationBuilder.DropColumn(
                name: "livello_tecnologico_medio",
                table: "series");

            migrationBuilder.DropColumn(
                name: "serie_state_summary",
                table: "series");

            migrationBuilder.DropColumn(
                name: "temi_obbligatori",
                table: "series");

            migrationBuilder.DropColumn(
                name: "world_rules_locked",
                table: "series");

            migrationBuilder.DropColumn(
                name: "is_primary",
                table: "model_roles");

            migrationBuilder.DropColumn(
                name: "Examined",
                table: "Log");

            migrationBuilder.DropColumn(
                name: "ResultFailReason",
                table: "Log");

            migrationBuilder.AlterColumn<int>(
                name: "ThreadId",
                table: "Log",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

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
    }
}
