# AI Coding Assistant Instructions for TinyGenerator

## Project Overview
TinyGenerator is an ASP.NET Core web application that generates stories using AI agents powered by Microsoft Semantic Kernel (SK). It orchestrates multiple writer agents (using local Ollama models) and evaluator agents to produce coherent, structured narratives. The app uses Razor Pages for UI, SignalR for real-time progress updates, and SQLite for persistence.

## Architecture
- **Core Service**: `FullStoryPipelineCommand` coordinates story generation via all active writer agents (dynamically loaded from DB), evaluates outputs with all active evaluator agents, and selects the best story for production.
- **Kernel Management**: `KernelFactory` creates SK kernels with Ollama/OpenAI connectors and registers custom plugins (skills) like `TextPlugin`, `MemorySkill`, `TtsApiSkill`.
- **Persistence**: `DatabaseService` handles SQLite storage for stories, logs, models, agents, and test results.
- **UI**: Razor Pages (e.g., `Genera.cshtml`) with Bootstrap/DataTables for admin interfaces. SignalR (`ProgressHub`) for live generation updates.
- **Agents**: Defined in DB with JSON configs for skills, prompts, and execution plans. Active agents get dedicated kernels at startup.
- **Skills/Plugins**: Custom classes in `Skills/` with `[KernelFunction]` methods for text manipulation, memory, TTS, etc.

## Key Workflows
- **Build**: `dotnet build` or VS Code task "build".
- **Run/Debug**: `dotnet run` or `dotnet watch run` (task "watch") for hot reload. Debug via VS Code launch settings.
- **Test Models**: Use `Pages/Models.cshtml` to run function-calling tests on Ollama models. Tests execute prompts and verify SK function invocations.
- **Generate Stories**: Via `Pages/Genera.cshtml` - inputs theme, selects a single writer or launches Full Pipeline (all active writers compete, best story goes to production), monitors progress via SignalR.
- **Admin**: Manage agents, models, logs in admin pages using DataTables for CRUD.

## Conventions
- **Semantic Kernel Integration**: All AI interactions must use SK's function-calling mechanism. No parsing model outputs for actions - register skills properly.
- **Agent Prompts**: Keep production prompts separate from test prompts. Avoid "invented" functions in agent instructions.
- **Skill Registration**: Plugins added via `builder.Plugins.AddFromObject(skill, "alias")` in `KernelFactory`. Use `[KernelFunction("name")]` with descriptions.
- **Database**: Use Dapper for queries. Tables: models, agents, stories, calls, test_definitions, etc.
- **UI Patterns**: Razor Pages with Tag Helpers (`asp-for`), Bootstrap 5, DataTables for tables. Centralize JS/CSS in `_Layout.cshtml`.
- **Logging**: Custom `ICustomLogger` to DB. Broadcast logs via `NotificationService` and SignalR.
- **Startup**: Initializes DB schema, seeds models/voices, creates kernels for active agents.
- **Error Handling**: Fail-fast on SK issues to highlight integration problems. Use try-catch for best-effort operations.

## Examples
- **Adding a Skill**: Create class in `Skills/` with methods like `[KernelFunction("calculate")] public double Add(double a, double b) => a + b;`. Register in `KernelFactory` if allowed.
- **Agent Config**: Agents have `Skills` (JSON array e.g. `["text", "memory"]`), `Prompt`, `Instructions`. Parsed at startup to enable plugins.
- **Story Generation**: Writers use models like `phi3:mini-128k`. Evaluators score on JSON format (e.g. `{"score": 8}`). Best story saved if score >= 7.
- **Tests**: `TestService` invokes agents with prompts, checks responses against `ExpectedPromptValue` or `ValidScoreRange`.

## Notes
- Models hardcoded in `StoryGeneratorService` but configurable via DB for agents.
- TTS via external API, voices seeded from service.
- Memory is persistent SQLite via `PersistentMemoryService`.
- Avoid fallbacks that bypass SK - prefer failing tests to expose issues.</content>
<parameter name="filePath">/Users/cristianodonaggio/Documents/ai/TinyGenerator/.github/copilot-instructions.md