using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNarrativeEngineTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing tables: add columns only (non-destructive)
            migrationBuilder.AddColumn<int>(
                name: "priority",
                table: "agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "allowed_profiles",
                table: "agents",
                type: "TEXT",
                nullable: true);

            // Note: ModelInfo uses default column names (PascalCase) because it does not specify [Column] attributes.
            migrationBuilder.AddColumn<bool>(
                name: "IsNarrativeCompatible",
                table: "models",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MaxContextTokens",
                table: "models",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "InstructionFollowingScore",
                table: "models",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "default_narrative_profile_id",
                table: "series",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "narrative_consistency_level",
                table: "series",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "narrative_profile_id",
                table: "stories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "runtime_state_id",
                table: "stories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "narrative_status",
                table: "stories",
                type: "TEXT",
                nullable: true);

            // New Narrative Engine tables
            migrationBuilder.CreateTable(
                name: "narrative_profiles",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    base_system_prompt = table.Column<string>(type: "TEXT", nullable: true),
                    style_prompt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_narrative_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "consequence_rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    narrative_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consequence_rules", x => x.id);
                    table.ForeignKey(
                        name: "FK_consequence_rules_narrative_profiles_narrative_profile_id",
                        column: x => x.narrative_profile_id,
                        principalTable: "narrative_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "failure_rules",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    narrative_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    trigger_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_failure_rules", x => x.id);
                    table.ForeignKey(
                        name: "FK_failure_rules_narrative_profiles_narrative_profile_id",
                        column: x => x.narrative_profile_id,
                        principalTable: "narrative_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "micro_objectives",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    narrative_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    code = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    difficulty = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_micro_objectives", x => x.id);
                    table.ForeignKey(
                        name: "FK_micro_objectives_narrative_profiles_narrative_profile_id",
                        column: x => x.narrative_profile_id,
                        principalTable: "narrative_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "narrative_resources",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    narrative_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    initial_value = table.Column<int>(type: "INTEGER", nullable: false),
                    min_value = table.Column<int>(type: "INTEGER", nullable: false),
                    max_value = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_narrative_resources", x => x.id);
                    table.ForeignKey(
                        name: "FK_narrative_resources_narrative_profiles_narrative_profile_id",
                        column: x => x.narrative_profile_id,
                        principalTable: "narrative_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "story_runtime_states",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    story_id = table.Column<long>(type: "INTEGER", nullable: false),
                    narrative_profile_id = table.Column<int>(type: "INTEGER", nullable: false),
                    current_chunk_index = table.Column<int>(type: "INTEGER", nullable: false),
                    current_phase = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    current_pov = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    failure_count = table.Column<int>(type: "INTEGER", nullable: false),
                    last_context = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_story_runtime_states", x => x.id);
                    table.ForeignKey(
                        name: "FK_story_runtime_states_narrative_profiles_narrative_profile_id",
                        column: x => x.narrative_profile_id,
                        principalTable: "narrative_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_story_runtime_states_stories_story_id",
                        column: x => x.story_id,
                        principalTable: "stories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "consequence_impacts",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    consequence_rule_id = table.Column<int>(type: "INTEGER", nullable: false),
                    resource_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    delta_value = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consequence_impacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_consequence_impacts_consequence_rules_consequence_rule_id",
                        column: x => x.consequence_rule_id,
                        principalTable: "consequence_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "story_resource_states",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    story_runtime_state_id = table.Column<long>(type: "INTEGER", nullable: false),
                    resource_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    current_value = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_story_resource_states", x => x.id);
                    table.ForeignKey(
                        name: "FK_story_resource_states_story_runtime_states_story_runtime_state_id",
                        column: x => x.story_runtime_state_id,
                        principalTable: "story_runtime_states",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Indexes
            migrationBuilder.CreateIndex(
                name: "IX_consequence_impacts_consequence_rule_id",
                table: "consequence_impacts",
                column: "consequence_rule_id");

            migrationBuilder.CreateIndex(
                name: "IX_consequence_rules_narrative_profile_id",
                table: "consequence_rules",
                column: "narrative_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_failure_rules_narrative_profile_id",
                table: "failure_rules",
                column: "narrative_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_micro_objectives_narrative_profile_id",
                table: "micro_objectives",
                column: "narrative_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_narrative_resources_narrative_profile_id",
                table: "narrative_resources",
                column: "narrative_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_story_resource_states_story_runtime_state_id",
                table: "story_resource_states",
                column: "story_runtime_state_id");

            migrationBuilder.CreateIndex(
                name: "IX_story_runtime_states_narrative_profile_id",
                table: "story_runtime_states",
                column: "narrative_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_story_runtime_states_story_id",
                table: "story_runtime_states",
                column: "story_id");

            // Seed data (from data/TinyGeneratorDbContext.cs)
            migrationBuilder.InsertData(
                table: "narrative_profiles",
                columns: new[] { "id", "name", "description", "base_system_prompt", "style_prompt" },
                values: new object[]
                {
                    1,
                    "SciFi Militare",
                    "Conflitto armato ad alta tensione",
                    "Scrivi narrativa a chunk continuo senza conclusioni.",
                    "Tono tecnico, militare, concreto."
                });

            migrationBuilder.InsertData(
                table: "narrative_resources",
                columns: new[] { "id", "narrative_profile_id", "name", "initial_value", "min_value", "max_value" },
                values: new object[,]
                {
                    { 1, 1, "Energia", 100, 0, 100 },
                    { 2, 1, "Integrità", 100, 0, 100 },
                    { 3, 1, "Uomini", 100, 0, 100 }
                });

            migrationBuilder.InsertData(
                table: "micro_objectives",
                columns: new[] { "id", "narrative_profile_id", "code", "description", "difficulty" },
                values: new object[,]
                {
                    { 1, 1, "DEFEND", "Difendere un settore critico", 2 },
                    { 2, 1, "DELAY", "Guadagnare tempo sotto pressione", 3 }
                });

            migrationBuilder.InsertData(
                table: "failure_rules",
                columns: new[] { "id", "narrative_profile_id", "description", "trigger_type" },
                values: new object[,]
                {
                    { 1, 1, "Decisione affrettata sotto pressione", "RandomUnderPressure" },
                    { 2, 1, "Risorsa critica sotto soglia", "ResourceBelowThreshold" }
                });

            migrationBuilder.InsertData(
                table: "consequence_rules",
                columns: new[] { "id", "narrative_profile_id", "description" },
                values: new object[,]
                {
                    { 1, 1, "Perdita di uomini" },
                    { 2, 1, "Danni strutturali" }
                });

            migrationBuilder.InsertData(
                table: "consequence_impacts",
                columns: new[] { "id", "consequence_rule_id", "resource_name", "delta_value" },
                values: new object[,]
                {
                    { 1, 1, "Uomini", -10 },
                    { 2, 2, "Integrità", -15 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop new tables first (reverse dependency order)
            migrationBuilder.DropTable(name: "consequence_impacts");
            migrationBuilder.DropTable(name: "failure_rules");
            migrationBuilder.DropTable(name: "micro_objectives");
            migrationBuilder.DropTable(name: "narrative_resources");
            migrationBuilder.DropTable(name: "story_resource_states");
            migrationBuilder.DropTable(name: "consequence_rules");
            migrationBuilder.DropTable(name: "story_runtime_states");
            migrationBuilder.DropTable(name: "narrative_profiles");

            // Remove added columns
            migrationBuilder.DropColumn(name: "priority", table: "agents");
            migrationBuilder.DropColumn(name: "allowed_profiles", table: "agents");

            migrationBuilder.DropColumn(name: "IsNarrativeCompatible", table: "models");
            migrationBuilder.DropColumn(name: "MaxContextTokens", table: "models");
            migrationBuilder.DropColumn(name: "InstructionFollowingScore", table: "models");

            migrationBuilder.DropColumn(name: "default_narrative_profile_id", table: "series");
            migrationBuilder.DropColumn(name: "narrative_consistency_level", table: "series");

            migrationBuilder.DropColumn(name: "narrative_profile_id", table: "stories");
            migrationBuilder.DropColumn(name: "runtime_state_id", table: "stories");
            migrationBuilder.DropColumn(name: "narrative_status", table: "stories");
        }
    }
}
