using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TinyGenerator.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    role = table.Column<string>(type: "TEXT", nullable: false),
                    model_id = table.Column<int>(type: "INTEGER", nullable: true),
                    voice_rowid = table.Column<int>(type: "INTEGER", nullable: true),
                    skills = table.Column<string>(type: "TEXT", nullable: true),
                    config = table.Column<string>(type: "TEXT", nullable: true),
                    json_response_format = table.Column<string>(type: "TEXT", nullable: true),
                    prompt = table.Column<string>(type: "TEXT", nullable: true),
                    instructions = table.Column<string>(type: "TEXT", nullable: true),
                    execution_plan = table.Column<string>(type: "TEXT", nullable: true),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    temperature = table.Column<double>(type: "REAL", nullable: true),
                    top_p = table.Column<double>(type: "REAL", nullable: true),
                    multi_step_template_id = table.Column<int>(type: "INTEGER", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "Log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ts = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Exception = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    ThreadId = table.Column<int>(type: "INTEGER", nullable: false),
                    ThreadScope = table.Column<string>(type: "TEXT", nullable: true),
                    AgentName = table.Column<string>(type: "TEXT", nullable: true),
                    Context = table.Column<string>(type: "TEXT", nullable: true),
                    analized = table.Column<bool>(type: "INTEGER", nullable: false),
                    chat_text = table.Column<string>(type: "TEXT", nullable: true),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "models",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: true),
                    IsLocal = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxContext = table.Column<int>(type: "INTEGER", nullable: false),
                    ContextToUse = table.Column<int>(type: "INTEGER", nullable: false),
                    FunctionCallingScore = table.Column<int>(type: "INTEGER", nullable: false),
                    CostInPerToken = table.Column<double>(type: "REAL", nullable: false),
                    CostOutPerToken = table.Column<double>(type: "REAL", nullable: false),
                    LimitTokensDay = table.Column<long>(type: "INTEGER", nullable: false),
                    LimitTokensWeek = table.Column<long>(type: "INTEGER", nullable: false),
                    LimitTokensMonth = table.Column<long>(type: "INTEGER", nullable: false),
                    Metadata = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: true),
                    TestDurationSeconds = table.Column<double>(type: "REAL", nullable: true),
                    NoTools = table.Column<bool>(type: "INTEGER", nullable: false),
                    WriterScore = table.Column<double>(type: "REAL", nullable: false),
                    BaseScore = table.Column<double>(type: "REAL", nullable: false),
                    TextEvalScore = table.Column<double>(type: "REAL", nullable: false),
                    TtsScore = table.Column<double>(type: "REAL", nullable: false),
                    MusicScore = table.Column<double>(type: "REAL", nullable: false),
                    FxScore = table.Column<double>(type: "REAL", nullable: false),
                    AmbientScore = table.Column<double>(type: "REAL", nullable: false),
                    TotalScore = table.Column<double>(type: "REAL", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    LastTestResults = table.Column<string>(type: "TEXT", nullable: true),
                    LastMusicTestFile = table.Column<string>(type: "TEXT", nullable: true),
                    LastSoundTestFile = table.Column<string>(type: "TEXT", nullable: true),
                    LastTtsTestFile = table.Column<string>(type: "TEXT", nullable: true),
                    LastScore_Base = table.Column<int>(type: "INTEGER", nullable: true),
                    LastScore_Tts = table.Column<int>(type: "INTEGER", nullable: true),
                    LastScore_Music = table.Column<int>(type: "INTEGER", nullable: true),
                    LastScore_Write = table.Column<int>(type: "INTEGER", nullable: true),
                    LastResults_BaseJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastResults_TtsJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastResults_MusicJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastResults_WriteJson = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "step_templates",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    task_type = table.Column<string>(type: "TEXT", nullable: false),
                    step_prompt = table.Column<string>(type: "TEXT", nullable: false),
                    instructions = table.Column<string>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: false),
                    updated_at = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_step_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stories",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    generation_id = table.Column<string>(type: "TEXT", nullable: false),
                    memory_key = table.Column<string>(type: "TEXT", nullable: false),
                    ts = table.Column<string>(type: "TEXT", nullable: false),
                    prompt = table.Column<string>(type: "TEXT", nullable: false),
                    story = table.Column<string>(type: "TEXT", nullable: false),
                    char_count = table.Column<int>(type: "INTEGER", nullable: false),
                    eval = table.Column<string>(type: "TEXT", nullable: false),
                    score = table.Column<double>(type: "REAL", nullable: false),
                    approved = table.Column<bool>(type: "INTEGER", nullable: false),
                    status_id = table.Column<int>(type: "INTEGER", nullable: true),
                    folder = table.Column<string>(type: "TEXT", nullable: true),
                    model_id = table.Column<int>(type: "INTEGER", nullable: true),
                    agent_id = table.Column<int>(type: "INTEGER", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stories_evaluations",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    story_id = table.Column<long>(type: "INTEGER", nullable: false),
                    narrative_coherence_score = table.Column<int>(type: "INTEGER", nullable: false),
                    narrative_coherence_defects = table.Column<string>(type: "TEXT", nullable: false),
                    originality_score = table.Column<int>(type: "INTEGER", nullable: false),
                    originality_defects = table.Column<string>(type: "TEXT", nullable: false),
                    emotional_impact_score = table.Column<int>(type: "INTEGER", nullable: false),
                    emotional_impact_defects = table.Column<string>(type: "TEXT", nullable: false),
                    action_score = table.Column<int>(type: "INTEGER", nullable: false),
                    action_defects = table.Column<string>(type: "TEXT", nullable: false),
                    total_score = table.Column<double>(type: "REAL", nullable: false),
                    raw_json = table.Column<string>(type: "TEXT", nullable: false),
                    model_id = table.Column<long>(type: "INTEGER", nullable: true),
                    agent_id = table.Column<int>(type: "INTEGER", nullable: true),
                    ts = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stories_evaluations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "stories_status",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    code = table.Column<string>(type: "TEXT", nullable: true),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    step = table.Column<int>(type: "INTEGER", nullable: false),
                    color = table.Column<string>(type: "TEXT", nullable: true),
                    operation_type = table.Column<string>(type: "TEXT", nullable: true),
                    agent_type = table.Column<string>(type: "TEXT", nullable: true),
                    function_name = table.Column<string>(type: "TEXT", nullable: true),
                    caption_to_execute = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stories_status", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "task_types",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    code = table.Column<string>(type: "TEXT", nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    default_executor_role = table.Column<string>(type: "TEXT", nullable: false),
                    default_checker_role = table.Column<string>(type: "TEXT", nullable: false),
                    output_merge_strategy = table.Column<string>(type: "TEXT", nullable: false),
                    validation_criteria = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "test_definitions",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    test_group = table.Column<string>(type: "TEXT", nullable: true),
                    library = table.Column<string>(type: "TEXT", nullable: true),
                    allowed_plugins = table.Column<string>(type: "TEXT", nullable: true),
                    function_name = table.Column<string>(type: "TEXT", nullable: true),
                    prompt = table.Column<string>(type: "TEXT", nullable: true),
                    expected_behavior = table.Column<string>(type: "TEXT", nullable: true),
                    expected_asset = table.Column<string>(type: "TEXT", nullable: true),
                    test_type = table.Column<string>(type: "TEXT", nullable: true),
                    expected_prompt_value = table.Column<string>(type: "TEXT", nullable: true),
                    valid_score_range = table.Column<string>(type: "TEXT", nullable: true),
                    timeout_secs = table.Column<int>(type: "INTEGER", nullable: false),
                    priority = table.Column<int>(type: "INTEGER", nullable: false),
                    execution_plan = table.Column<string>(type: "TEXT", nullable: true),
                    active = table.Column<bool>(type: "INTEGER", nullable: false),
                    json_response_format = table.Column<string>(type: "TEXT", nullable: true),
                    files_to_copy = table.Column<string>(type: "TEXT", nullable: true),
                    temperature = table.Column<double>(type: "REAL", nullable: true),
                    top_p = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tts_voices",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    voice_id = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", nullable: true),
                    language = table.Column<string>(type: "TEXT", nullable: true),
                    gender = table.Column<string>(type: "TEXT", nullable: true),
                    age = table.Column<string>(type: "TEXT", nullable: true),
                    confidence = table.Column<double>(type: "REAL", nullable: true),
                    score = table.Column<double>(type: "REAL", nullable: true),
                    tags = table.Column<string>(type: "TEXT", nullable: true),
                    template_wav = table.Column<string>(type: "TEXT", nullable: true),
                    archetype = table.Column<string>(type: "TEXT", nullable: true),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<string>(type: "TEXT", nullable: true),
                    updated_at = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tts_voices", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "Log");

            migrationBuilder.DropTable(
                name: "models");

            migrationBuilder.DropTable(
                name: "step_templates");

            migrationBuilder.DropTable(
                name: "stories");

            migrationBuilder.DropTable(
                name: "stories_evaluations");

            migrationBuilder.DropTable(
                name: "stories_status");

            migrationBuilder.DropTable(
                name: "task_types");

            migrationBuilder.DropTable(
                name: "test_definitions");

            migrationBuilder.DropTable(
                name: "tts_voices");
        }
    }
}
