using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace TinyGenerator.data.Migrations
{
    /// <inheritdoc />
    public partial class AddRolesAndModelRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ruolo = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    comando_collegato = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: true),
                    updated_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "model_roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    model_id = table.Column<int>(type: "INTEGER", nullable: false),
                    role_id = table.Column<int>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    use_count = table.Column<int>(type: "INTEGER", nullable: false),
                    use_successed = table.Column<int>(type: "INTEGER", nullable: false),
                    use_failed = table.Column<int>(type: "INTEGER", nullable: false),
                    last_use = table.Column<string>(type: "TEXT", nullable: true),
                    instructions = table.Column<string>(type: "TEXT", nullable: true),
                    top_p = table.Column<double>(type: "REAL", nullable: true),
                    top_k = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: true),
                    updated_at = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_model_roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_model_roles_models_model_id",
                        column: x => x.model_id,
                        principalTable: "models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_model_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "id", "comando_collegato", "created_at", "ruolo", "updated_at" },
                values: new object[,]
                {
                    { 1, "FullStoryPipelineCommand", "2026-01-23T13:31:40.2134749Z", "writer", "2026-01-23T13:31:40.2134749Z" },
                    { 2, "AddVoiceTagsToStoryCommand", "2026-01-23T13:31:40.2134749Z", "formatter", "2026-01-23T13:31:40.2134749Z" },
                    { 3, "StoryEvaluation", "2026-01-23T13:31:40.2134749Z", "evaluator", "2026-01-23T13:31:40.2134749Z" },
                    { 4, "TtsGeneration", "2026-01-23T13:31:40.2134749Z", "tts_expert", "2026-01-23T13:31:40.2134749Z" },
                    { 5, "MusicGeneration", "2026-01-23T13:31:40.2134749Z", "music_expert", "2026-01-23T13:31:40.2134749Z" },
                    { 6, "FxGeneration", "2026-01-23T13:31:40.2134749Z", "fx_expert", "2026-01-23T13:31:40.2134749Z" },
                    { 7, "SummarizeStory", "2026-01-23T13:31:40.2134749Z", "summarizer", "2026-01-23T13:31:40.2134749Z" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_model_roles_model_id",
                table: "model_roles",
                column: "model_id");

            migrationBuilder.CreateIndex(
                name: "IX_model_roles_role_id",
                table: "model_roles",
                column: "role_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "model_roles");

            migrationBuilder.DropTable(
                name: "roles");
        }
    }
}

