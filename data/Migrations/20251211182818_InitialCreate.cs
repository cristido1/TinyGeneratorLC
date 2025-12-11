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
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<int>(type: "INTEGER", nullable: true),
                    VoiceId = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelName = table.Column<string>(type: "TEXT", nullable: true),
                    Skills = table.Column<string>(type: "TEXT", nullable: true),
                    Config = table.Column<string>(type: "TEXT", nullable: true),
                    JsonResponseFormat = table.Column<string>(type: "TEXT", nullable: true),
                    Prompt = table.Column<string>(type: "TEXT", nullable: true),
                    Instructions = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutionPlan = table.Column<string>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Temperature = table.Column<double>(type: "REAL", nullable: true),
                    TopP = table.Column<double>(type: "REAL", nullable: true),
                    MultiStepTemplateId = table.Column<int>(type: "INTEGER", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "evaluations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StoryId = table.Column<long>(type: "INTEGER", nullable: false),
                    NarrativeCoherenceScore = table.Column<int>(type: "INTEGER", nullable: false),
                    NarrativeCoherenceDefects = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalityScore = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalityDefects = table.Column<string>(type: "TEXT", nullable: false),
                    EmotionalImpactScore = table.Column<int>(type: "INTEGER", nullable: false),
                    EmotionalImpactDefects = table.Column<string>(type: "TEXT", nullable: false),
                    ActionScore = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionDefects = table.Column<string>(type: "TEXT", nullable: false),
                    TotalScore = table.Column<double>(type: "REAL", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    ModelId = table.Column<long>(type: "INTEGER", nullable: true),
                    AgentId = table.Column<int>(type: "INTEGER", nullable: true),
                    AgentName = table.Column<string>(type: "TEXT", nullable: false),
                    AgentModel = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<string>(type: "TEXT", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evaluations", x => x.Id);
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
                    Analized = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChatText = table.Column<string>(type: "TEXT", nullable: true),
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
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    StepPrompt = table.Column<string>(type: "TEXT", nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GenerationId = table.Column<string>(type: "TEXT", nullable: false),
                    MemoryKey = table.Column<string>(type: "TEXT", nullable: false),
                    Timestamp = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    Story = table.Column<string>(type: "TEXT", nullable: false),
                    CharCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Agent = table.Column<string>(type: "TEXT", nullable: false),
                    Eval = table.Column<string>(type: "TEXT", nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: false),
                    Approved = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StatusId = table.Column<int>(type: "INTEGER", nullable: true),
                    StatusDescription = table.Column<string>(type: "TEXT", nullable: true),
                    StatusColor = table.Column<string>(type: "TEXT", nullable: true),
                    StatusOperationType = table.Column<string>(type: "TEXT", nullable: true),
                    StatusAgentType = table.Column<string>(type: "TEXT", nullable: true),
                    StatusFunctionName = table.Column<string>(type: "TEXT", nullable: true),
                    StatusStep = table.Column<int>(type: "INTEGER", nullable: true),
                    Folder = table.Column<string>(type: "TEXT", nullable: true),
                    HasVoiceSource = table.Column<bool>(type: "INTEGER", nullable: false),
                    TestRunId = table.Column<int>(type: "INTEGER", nullable: true),
                    TestStepId = table.Column<int>(type: "INTEGER", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stories_status",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Step = table.Column<int>(type: "INTEGER", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: true),
                    OperationType = table.Column<string>(type: "TEXT", nullable: true),
                    AgentType = table.Column<string>(type: "TEXT", nullable: true),
                    FunctionName = table.Column<string>(type: "TEXT", nullable: true),
                    CaptionToExecute = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stories_status", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "task_types",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultExecutorRole = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultCheckerRole = table.Column<string>(type: "TEXT", nullable: false),
                    OutputMergeStrategy = table.Column<string>(type: "TEXT", nullable: false),
                    ValidationCriteria = table.Column<string>(type: "TEXT", nullable: true),
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
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupName = table.Column<string>(type: "TEXT", nullable: true),
                    Library = table.Column<string>(type: "TEXT", nullable: true),
                    AllowedPlugins = table.Column<string>(type: "TEXT", nullable: true),
                    FunctionName = table.Column<string>(type: "TEXT", nullable: true),
                    Prompt = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectedBehavior = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectedAsset = table.Column<string>(type: "TEXT", nullable: true),
                    TestType = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectedPromptValue = table.Column<string>(type: "TEXT", nullable: true),
                    ValidScoreRange = table.Column<string>(type: "TEXT", nullable: true),
                    TimeoutSecs = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutionPlan = table.Column<string>(type: "TEXT", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    JsonResponseFormat = table.Column<string>(type: "TEXT", nullable: true),
                    FilesToCopy = table.Column<string>(type: "TEXT", nullable: true),
                    Temperature = table.Column<double>(type: "REAL", nullable: true),
                    TopP = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_test_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tts_voices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VoiceId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Gender = table.Column<string>(type: "TEXT", nullable: true),
                    Age = table.Column<string>(type: "TEXT", nullable: true),
                    Confidence = table.Column<double>(type: "REAL", nullable: true),
                    Score = table.Column<double>(type: "REAL", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: true),
                    TemplateWav = table.Column<string>(type: "TEXT", nullable: true),
                    Archetype = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "BLOB", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tts_voices", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "evaluations");

            migrationBuilder.DropTable(
                name: "Log");

            migrationBuilder.DropTable(
                name: "models");

            migrationBuilder.DropTable(
                name: "step_templates");

            migrationBuilder.DropTable(
                name: "stories");

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
