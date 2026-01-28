## AI Coding Assistant — TinyGenerator (concise)

This file gives the minimal, actionable facts an AI coding assistant needs to be productive in this repo.

**Big picture**
- **What**: ASP.NET Core Razor Pages app that orchestrates multiple AI writer/evaluator agents to generate stories via a LangChain-style ReAct loop.
- **Where to look first**: [Program.cs](Program.cs), [Controllers/StoriesApiController.cs](Controllers/StoriesApiController.cs#L1), [Pages/Genera.cshtml](Pages/Genera.cshtml#L1).

**Core components & flow**
- **Orchestrators**: `LangChainKernelFactory` (in `Services/`) creates `HybridLangChainOrchestrator` instances and registers tools.
- **Pipeline**: `FullStoryPipelineCommand` runs writer agents, then `LangChainTestService`/evaluators score outputs; `MultiStepOrchestrationService` handles step templates and retries.
- **Tools**: Implement tools in `Skills/` by inheriting `BaseLangChainTool` and exposing OpenAI-compatible function schemas (use `CreateFunctionSchema()`).
- **Persistence & memory**: SQLite via `TinyGeneratorDbContext` (`data/`), persistent memory service for vector embeddings.
- **Realtime & logging**: SignalR hub in [Hubs/ProgressHub.cs](Hubs/ProgressHub.cs#L1); logs written to `app_events` via `ICustomLogger`.

**Common workflows (commands & VS Code tasks)**
- Build: `dotnet build` (VS Code task `build` exists and runs `dotnet build TinyGenerator.csproj`).
- Run dev: `dotnet run` or `dotnet watch run` (task `watch`) for hot reload.
- Tests: `dotnet test` (task `test`). Unit/integration tests live under `Tests/`.
- Start local model services: `ollama_start.bat` / `start_ollama.sh`; TTS/Audiocraft helpers: `start_audiocraft.ps1`.

**Repo-specific conventions & gotchas**
- Razor Pages first: prefer page model handlers (`Pages/*.cshtml.cs`) over heavy client-side JS. See `Pages/Genera.cshtml` for generation flow.
- **Index pages (list/grid)**: when creating or modifying a Razor Pages *Index* page (list/grid), follow the standard in [docs/index_page_rules.txt](docs/index_page_rules.txt).
- **Data grids**: use DataTables.net (NOT Ag Grid) with Bootstrap 5 for all list/grid pages. Follow the complete standard in [usage_index_standard.txt](usage_index_standard.txt) for layout, DOM structure, client-side processing, ColVis button, state persistence, and child row expansion.
- Tool function-calling is enforced by tests — tools must expose valid JSON function schemas and be registered in `LangChainToolFactory`.
- Pipelines fail-fast on tool invocation errors; retry logic covers recoverable failures but do not silently swallow tool errors.

**How to add/modify things (short examples)**
- Add a tool: create `Skills/MyTool.cs` inheriting `BaseLangChainTool`, implement `GetFunctionSchemas()` and `ExecuteAsync()`, then register in `LangChainToolFactory`.
- Add/update an agent: seed/update a DB row in `agents` with `model_name`, `Skills` (JSON array), `Prompt`, and optional `MultiStepTemplateId`.
- Edit multi-step flow: update `step_templates` entries (each step has prompt, tools, validators used by `MultiStepOrchestrationService`).

**External integrations**
- Ollama/OpenAI-compatible models (local Ollama recommended). Default TTS endpoint: `http://127.0.0.1:8004`. AudioCraft default: `http://localhost:8003`.

**File map (inspect these first)**
- `Program.cs` — DI, startup and orchestrator registration.
- `Services/` — core orchestration and pipeline: search for `FullStoryPipelineCommand`, `LangChainKernelFactory`, `MultiStepOrchestrationService`.
- `Skills/` — all tool implementations.
- `Hubs/ProgressHub.cs` — SignalR progress/log streaming.
- `Pages/Genera.cshtml`, `Pages/Models.cshtml` — UI generation and model-test flows.

If you want, I can expand any section with concrete code snippets (example `BaseLangChainTool` implementation, sample agent JSON row, or DB migration commands). Reply with which section to expand.
# AI Coding Assistant Instructions for TinyGenerator

## Project Overview
TinyGenerator is an ASP.NET Core web application that generates stories using AI agents powered by LangChain C#. It orchestrates multiple writer agents (using local Ollama models) and evaluator agents to produce coherent, structured narratives through a custom ReAct loop implementation. The app uses Razor Pages for UI, SignalR for real-time progress updates, and SQLite for persistence.

## Architecture
- **Core Service**: `FullStoryPipelineCommand` coordinates story generation via all active writer agents (dynamically loaded from DB), evaluates outputs with all active evaluator agents, and selects the best story for production.
- **Orchestrator Management**: `LangChainKernelFactory` creates `HybridLangChainOrchestrator` instances with Ollama/OpenAI clients and registers tools (Text, Math, Memory, HTTP, TTS, etc.).
- **Multi-Step Execution**: `MultiStepOrchestrationService` manages step-by-step story generation with validation, retry logic, and progress tracking.
- **Persistence**: `DatabaseService` handles SQLite storage (EF Core + Dapper) for stories, logs, models, agents, test results, and vector embeddings.
- **UI**: Razor Pages (e.g., `Genera.cshtml`) with Bootstrap/DataTables for admin interfaces. SignalR (`ProgressHub`) for live generation updates.
- **Agents**: Defined in DB with JSON configs for tools, prompts, and multi-step templates. Active agents get dedicated orchestrators at startup.
- **Tools**: Custom classes in `Skills/` inheriting `BaseLangChainTool` with OpenAI-compatible function schemas for text manipulation, memory, filesystem, HTTP, TTS, AudioCraft, etc.

## Key Workflows
- **Build**: `dotnet build` or VS Code task "build".
- **Run/Debug**: `dotnet run` or `dotnet watch run` (task "watch") for hot reload. Debug via VS Code launch settings.
- **Test Models**: Use `Pages/Models.cshtml` to run function-calling tests on Ollama models. Tests execute prompts via `LangChainTestService` and verify tool invocations with retry logic.
- **Generate Stories**: Via `Pages/Genera.cshtml` - inputs theme, selects a single writer or launches Full Pipeline (all active writers compete via `CommandDispatcher`, best story with score >= 7 goes to production), monitors progress via SignalR.
- **Multi-Step Pipeline**: Stories generated chunk-by-chunk through `step_templates` with automatic validation, retry, and coherence checks.
- **Admin**: Manage agents, models, logs, tests, step templates in admin pages using Ag Grid for CRUD operations (single filter, persistent columns, pagination).

## Conventions
- **LangChain Integration**: All AI interactions use OpenAI-compatible tool schemas. Tools must inherit `BaseLangChainTool` and implement `GetSchema()` + `ExecuteAsync()`.
- **Agent Prompts**: Keep production prompts separate from test prompts. Avoid "invented" functions in agent instructions - only reference registered tools.
- **Tool Registration**: Tools registered in `LangChainToolFactory.CreateOrchestratorWithTools()`. Each tool exposes JSON schemas via `GetFunctionSchemas()`.
- **ReAct Loop**: Custom implementation in `ReActLoopOrchestrator` with explicit control flow, tool call parsing, retry logic, and timeout handling.
- **Database**: EF Core for entities (stories, agents, models), Dapper for embeddings/raw queries. Tables: stories, agents, models, task_executions, step_templates, test_definitions, etc.
- **UI Patterns**: **This is a Razor Pages project** - prefer Razor Pages native features (form handlers, page models, postback, callbacks) over JavaScript solutions. Use JavaScript only when significantly more advantageous. All data grids must use **Ag Grid** with single column filter, persistent visible column selection, and pagination. Use Tag Helpers (`asp-for`, `asp-page-handler`), Bootstrap 5 for styling. Centralize JS/CSS in `_Layout.cshtml`.
- **Logging**: Custom `ICustomLogger` writes to `app_events` table. Broadcast logs via SignalR `ProgressHub`.
- **Command Queue**: `CommandDispatcher` manages background tasks with priority queue and configurable parallelism (default 3).
- **Startup**: Initializes DB schema, seeds models/voices, creates orchestrators for active agents with multi-step templates.
- **Error Handling**: Fail-fast on tool invocation errors to highlight integration problems. Use retry logic for recoverable failures.

## Examples
- **Adding a Tool**: Create class in `Skills/` inheriting `BaseLangChainTool`. Implement `GetSchema()` returning OpenAI function schema and `ExecuteAsync(string input)` parsing JSON args. Register in `LangChainToolFactory` allowed tools list.
- **Agent Config**: Agents have `Skills` (JSON array e.g. `["text", "memory"]`), `Prompt`, `Instructions`, and optional `MultiStepTemplateId`. Parsed at startup to enable tools.
- **Multi-Step Generation**: Writers execute step-by-step via `step_templates` with automatic chunking, validation, and retry. Each step has its own tools, prompt, and success criteria.
- **Story Pipeline**: Writers use models like `phi3:mini-128k`, `llama3.1:8b`, `qwen2.5:7b`. Evaluators score stories on JSON format (e.g. `{"score": 8}`). Best story with score >= 7 goes to production.
- **Tests**: `LangChainTestService` invokes agents via ReAct loop, validates tool calls, checks responses against `ExpectedPromptValue` or `ValidScoreRange`, tracks retry attempts.

## Notes
- Models configured in DB `models` table. Agents reference models via `model_name` field.
- TTS via external HTTP API (default `http://127.0.0.1:8004`), voices seeded from `/voices` endpoint at startup.
- AudioCraft integration for music/ambience generation (optional, default `http://localhost:8003`).
- Memory is persistent SQLite via `PersistentMemoryService` with vector embeddings (Ollama `nomic-embed-text`).
- Tool schemas must be OpenAI-compatible. Use `CreateFunctionSchema()` helper in `BaseLangChainTool`.
- Avoid fallbacks that bypass tool calling - prefer failing tests to expose integration issues.
- Priority system: 1 = highest (TTS/audio), 2 = normal (generation), 3+ = low priority background tasks.

## Additional Best Practices
- **LangChain First**: Prefer LangChain for new features; Semantic Kernel is maintained for backward compatibility.
- **Concise Prompts**: Keep prompts minimal and leverage function/tool calling for complex operations.
- **Scripts**: Use `start_ollama.sh` or `ollama_start.bat` for quick Ollama setup. Populate data with `populate_*.sql` scripts.
- **TTS Schema**: Ensure `tts_schema.json` is generated with proper validation and saved under `data/tts/`.
- **UI Standards**: Razor Pages must follow the structure outlined in `interfaccia_ag_grid.txt`. Use AG Grid in pure JavaScript mode with Bootstrap 5.
- **Testing**: Validate ReAct loops and tool schemas using xUnit tests. Ensure all tools are registered and functional.