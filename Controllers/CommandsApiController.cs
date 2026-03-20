using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Controllers
{
    [ApiController]
    [Route("api/commands")]
    public class CommandsApiController : ControllerBase
    {
        private static readonly IReadOnlyList<Type> _commandTypes = DiscoverCommandTypes();
        private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly ICommandDispatcher _dispatcher;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly StoriesService _storiesService;
        private readonly ICustomLogger _logger;
        private readonly CommandTuningOptions _tuning;
        private readonly IServiceProvider _serviceProvider;
        private readonly TextValidationService _textValidationService;

        public CommandsApiController(
            ICommandDispatcher dispatcher,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            StoriesService storiesService,
            ICustomLogger logger,
            IOptions<CommandTuningOptions> tuningOptions,
            IServiceProvider serviceProvider,
            TextValidationService textValidationService)
        {
            _dispatcher = dispatcher;
            _database = database;
            _kernelFactory = kernelFactory;
            _storiesService = storiesService;
            _logger = logger;
            _tuning = tuningOptions.Value ?? new CommandTuningOptions();
            _serviceProvider = serviceProvider;
            _textValidationService = textValidationService;
        }

        [HttpGet]
        public IActionResult GetActiveCommands()
        {
            var commands = _dispatcher.GetActiveCommands();
            return Ok(commands);
        }

        /// <summary>
        /// POST /api/commands/cancel/{runId}
        /// Annulla un comando in coda o in esecuzione.
        /// </summary>
        [HttpPost("cancel/{runId}")]
        public IActionResult CancelCommand(string runId)
        {
            var cancelled = _dispatcher.CancelCommand(runId);
            if (!cancelled)
            {
                return NotFound(new { error = $"Comando {runId} non trovato o gia completato." });
            }

            return Ok(new { runId, message = "Cancel requested" });
        }

        /// <summary>
        /// POST /api/commands/clear-completed
        /// Rimuove dal popup i comandi terminati (completed/failed/cancelled).
        /// </summary>
        [HttpPost("clear-completed")]
        public IActionResult ClearCompletedCommands()
        {
            var removed = _dispatcher.ClearCompletedCommands();
            return Ok(new { removed, message = "Completed commands cleared" });
        }

        /// <summary>
        /// POST /api/commands/summarize?storyId=123
        /// Genera il riassunto di una storia usando l'agente Story Summarizer.
        /// </summary>
        [HttpPost("summarize")]
        public IActionResult SummarizeStory([FromQuery] long storyId)
        {
            // Verifica che la storia esista
            var story = _database.GetStoryById(storyId);
            if (story == null)
            {
                return NotFound(new { error = $"Storia {storyId} non trovata." });
            }

            if (string.IsNullOrWhiteSpace(story.StoryRaw))
            {
                return BadRequest(new { error = $"Storia {storyId} non ha contenuto da riassumere." });
            }

            var runId = Guid.NewGuid().ToString();

            var cmd = new SummarizeStoryCommand(
                storyId,
                _database,
                _kernelFactory,
                _logger,
                scopeFactory: _serviceProvider.GetService<IServiceScopeFactory>()
            );

            _dispatcher.Enqueue(
                "SummarizeStory",
                async ctx => {
                    bool success = await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId);
                    var message = success
                        ? "Summary generated"
                        : (string.IsNullOrWhiteSpace(cmd.LastError) ? "Failed to generate summary" : cmd.LastError);
                    return new CommandResult(success, message);
                },
                runId: runId,
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = storyId.ToString(),
                    ["storyTitle"] = story.Title ?? "Untitled"
                },
                priority: 3);  // Priorità media-bassa per riassunti

            return Ok(new { runId, storyId, message = "Summarization enqueued" });
        }

        /// <summary>
        /// POST /api/commands/format-story?storyId=123
        /// Trasforma story_raw in story_tagged usando l'agente formatter.
        /// </summary>
        [HttpPost("format-story")]
        public IActionResult FormatStory([FromQuery] long storyId)
        {
            var story = _database.GetStoryById(storyId);
            if (story == null)
            {
                return NotFound(new { error = $"Storia {storyId} non trovata." });
            }

            if (string.IsNullOrWhiteSpace(story.StoryRaw))
            {
                return BadRequest(new { error = $"Storia {storyId} non ha testo da formattare." });
            }

            var runId = Guid.NewGuid().ToString();

            var cmd = new AddVoiceTagsToStoryCommand(
                storyId,
                _database,
                _kernelFactory,
                _storiesService,
                _logger,
                _tuning);

            _dispatcher.Enqueue(
                "TransformStoryRawToTagged",
                async ctx => await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId),
                runId: runId,
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = storyId.ToString(),
                    ["operation"] = "format_story"
                },
                priority: 2);

            return Ok(new { runId, storyId, message = "Formatter enqueued" });
        }

        /// <summary>
        /// POST /api/commands/batch-summarize?minScore=60
        /// Accoda riassunti per tutte le storie con valutazione >= minScore.
        /// Ritorna immediatamente dopo aver accodato i comandi.
        /// </summary>
        [HttpPost("batch-summarize")]
        public IActionResult BatchSummarizeStories([FromQuery] int minScore = 60)
        {
            var runId = Guid.NewGuid().ToString();

            var cmd = new BatchSummarizeStoriesEnqueuerCommand(
                _database,
                _kernelFactory,
                _dispatcher,
                _logger,
                scopeFactory: _serviceProvider.GetService<IServiceScopeFactory>(),
                minScore: minScore
            );

            // Accoda il comando batch che a sua volta accoderà i comandi individuali
            _dispatcher.Enqueue(
                "BatchSummarizeStoriesEnqueuer",
                async ctx => await cmd.ExecuteAsync(ctx.CancellationToken),
                runId: runId,
                metadata: new Dictionary<string, string>
                {
                    ["minScore"] = minScore.ToString(),
                    ["agentName"] = "batch_orchestrator",
                    ["operation"] = "batch_summarize"
                },
                priority: 2);  // Priorità normale per il batch orchestrator

            return Ok(new { runId, minScore, message = "Batch summarization started" });
        }

        /// <summary>
        /// POST /api/commands/validate-agent-json-examples?includeInactive=false&maxExamplesPerAgent=10
        /// Avvia un controllo deterministico degli esempi JSON presenti nelle instructions degli agenti
        /// che hanno json_response_format valorizzato. Esito dettagliato salvato in system_reports.
        /// </summary>
        [HttpPost("validate-agent-json-examples")]
        public IActionResult ValidateAgentJsonExamples(
            [FromQuery] bool includeInactive = false,
            [FromQuery] int maxExamplesPerAgent = 10)
        {
            var runId = Guid.NewGuid().ToString();
            var cmd = new ValidateAgentJsonInstructionExamplesCommand(
                _database,
                _logger,
                includeInactive: includeInactive,
                maxExamplesPerAgent: Math.Max(1, maxExamplesPerAgent));

            _dispatcher.Enqueue(
                cmd,
                runId: runId,
                metadata: new Dictionary<string, string>
                {
                    ["operation"] = "validate_agent_json_examples",
                    ["includeInactive"] = includeInactive.ToString(),
                    ["maxExamplesPerAgent"] = Math.Max(1, maxExamplesPerAgent).ToString()
                },
                priority: cmd.Priority);

            return Ok(new
            {
                runId,
                includeInactive,
                maxExamplesPerAgent = Math.Max(1, maxExamplesPerAgent),
                message = "Validation command enqueued"
            });
        }

        /// <summary>
        /// POST /api/commands/state-story/start
        /// Creates a new state-driven story with a NarrativeProfile and activates it (single active story enforced).
        /// </summary>
        [HttpPost("state-story/start")]
        public async Task<IActionResult> StartStateDrivenStory([FromBody] StartStateDrivenStoryRequest request)
        {
            if (request == null) return BadRequest(new { error = "Missing request body" });
            if (string.IsNullOrWhiteSpace(request.Theme)) return BadRequest(new { error = "Theme is required" });
            if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest(new { error = "Title is required" });
            if (request.NarrativeProfileId <= 0) return BadRequest(new { error = "NarrativeProfileId is required" });

            var cmd = new StartStateDrivenStoryCommand(_database);
            var (success, storyId, error) = await cmd.ExecuteAsync(
                theme: request.Theme,
                title: request.Title,
                narrativeProfileId: request.NarrativeProfileId,
                serieId: request.SeriesId,
                serieEpisode: request.EpisodeId,
                plannerMode: request.PlannerMode,
                ct: HttpContext.RequestAborted);

            if (!success)
            {
                return BadRequest(new { error = error ?? "Failed to start story" });
            }

            return Ok(new { storyId, message = "State-driven story created and activated" });
        }

        /// <summary>
        /// POST /api/commands/state-story/next-chunk
        /// Enqueues a GenerateNextChunkCommand for the active runtime story.
        /// </summary>
        [HttpPost("state-story/next-chunk")]
        public IActionResult GenerateNextChunk([FromBody] GenerateNextChunkRequest request)
        {
            if (request == null) return BadRequest(new { error = "Missing request body" });
            if (request.StoryId <= 0) return BadRequest(new { error = "StoryId is required" });
            if (request.WriterAgentId <= 0) return BadRequest(new { error = "WriterAgentId is required" });

            var snap = _database.GetStateDrivenStorySnapshot(request.StoryId);
            if (snap == null) return NotFound(new { error = $"Story {request.StoryId} not found or not state-driven" });
            if (!snap.IsActive) return BadRequest(new { error = "Story runtime is not active" });

            var runId = Guid.NewGuid().ToString();
            var scopeFactory = _serviceProvider.GetService<IServiceScopeFactory>();
            var cmd = new GenerateNextChunkCommand(
                storyId: request.StoryId,
                writerAgentId: request.WriterAgentId,
                database: _database,
                kernelFactory: _kernelFactory,
                textValidationService: _textValidationService,
                logger: _logger,
                tuning: _tuning,
                scopeFactory: scopeFactory);

            _dispatcher.Enqueue(
                "GenerateNextChunk",
                async ctx => await cmd.ExecuteAsync(ctx.CancellationToken),
                runId: runId,
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = request.StoryId.ToString(),
                    ["agentId"] = request.WriterAgentId.ToString(),
                    ["operation"] = "state_driven_next_chunk"
                },
                priority: 2);

            return Ok(new { runId, storyId = request.StoryId, message = "Next chunk enqueued" });
        }

        /// <summary>
        /// GET /api/commands/available
        /// Restituisce la lista di tutti i comandi ICommand disponibili con i relativi parametri del costruttore.
        /// </summary>
        [HttpGet("available")]
        public IActionResult GetAvailableCommands()
        {
            var result = _commandTypes.Select(t =>
            {
                var commandName = ComputeCommandName(t);
                var ctor = t.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length)
                    .FirstOrDefault();
                var parameters = ctor?.GetParameters()
                    .Select(p => new
                    {
                        name = p.Name!,
                        type = p.ParameterType.Name,
                        required = !p.HasDefaultValue
                    })
                    .ToList() ?? [];
                return new { commandName, typeName = t.Name, parameters };
            }).ToList();

            return Ok(result);
        }

        /// <summary>
        /// POST /api/commands/run/{commandName}
        /// Accoda un comando ICommand identificato dal suo CommandName (snake_case) o dal nome del tipo (senza suffisso "Command").
        /// I parametri non risolvibili da DI devono essere forniti nel body come oggetto JSON (chiave = nome parametro).
        /// </summary>
        [HttpPost("run/{commandName}")]
        public IActionResult RunCommand(
            string commandName,
            [FromBody] Dictionary<string, JsonElement>? parameters)
        {
            var commandType = FindCommandTypeByName(commandName);
            if (commandType == null)
                return NotFound(new { error = $"Comando '{commandName}' non trovato." });

            ICommand command;
            try
            {
                command = CreateCommandInstance(commandType, parameters ?? []);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = $"Impossibile istanziare il comando: {ex.Message}" });
            }

            var runId = Guid.NewGuid().ToString();
            _dispatcher.Enqueue(
                command,
                runId: runId,
                metadata: new Dictionary<string, string>
                {
                    ["commandName"] = command.CommandName,
                    ["source"] = "generic_api"
                },
                priority: command.Priority);

            return Ok(new { runId, commandName = command.CommandName });
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        private static IReadOnlyList<Type> DiscoverCommandTypes()
        {
            return typeof(ICommand).Assembly
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(ICommand).IsAssignableFrom(t))
                .OrderBy(t => t.Name)
                .ToList();
        }

        private static string ComputeCommandName(Type commandType)
        {
            var name = commandType.Name.EndsWith("Command", StringComparison.OrdinalIgnoreCase)
                ? commandType.Name[..^7]
                : commandType.Name;
            return CommandExecutionFunction.ToSnakeCase(name);
        }

        private static Type? FindCommandTypeByName(string commandName)
        {
            return _commandTypes.FirstOrDefault(t =>
                string.Equals(ComputeCommandName(t), commandName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, commandName + "Command", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, commandName, StringComparison.OrdinalIgnoreCase));
        }

        private ICommand CreateCommandInstance(Type commandType, Dictionary<string, JsonElement> parameters)
        {
            var ctors = commandType.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .ToList();

            var errors = new List<string>();

            foreach (var ctor in ctors)
            {
                var ctorParams = ctor.GetParameters();
                var args = new object?[ctorParams.Length];
                bool canInstantiate = true;
                var missing = new List<string>();

                for (int i = 0; i < ctorParams.Length; i++)
                {
                    var param = ctorParams[i];

                    // 1. Try to resolve from DI
                    var svc = _serviceProvider.GetService(param.ParameterType);
                    if (svc != null)
                    {
                        args[i] = svc;
                        continue;
                    }

                    // 2. Try to match from body parameters (case-insensitive)
                    var bodyKey = parameters.Keys.FirstOrDefault(k =>
                        string.Equals(k, param.Name, StringComparison.OrdinalIgnoreCase));
                    if (bodyKey != null)
                    {
                        try
                        {
                            args[i] = JsonSerializer.Deserialize(
                                parameters[bodyKey].GetRawText(),
                                param.ParameterType,
                                _jsonOptions);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            missing.Add($"{param.Name} (errore conversione: {ex.Message})");
                            canInstantiate = false;
                            break;
                        }
                    }

                    // 3. Use default value if available
                    if (param.HasDefaultValue)
                    {
                        args[i] = param.DefaultValue;
                        continue;
                    }

                    missing.Add($"{param.ParameterType.Name} {param.Name}");
                    canInstantiate = false;
                    break;
                }

                if (canInstantiate)
                    return (ICommand)ctor.Invoke(args);

                errors.Add($"[{ctorParams.Length} params] mancano: {string.Join(", ", missing)}");
            }

            throw new InvalidOperationException(
                $"Nessun costruttore applicabile per {commandType.Name}. {string.Join("; ", errors)}");
        }
    }

    public sealed class StartStateDrivenStoryRequest
    {
        public string Theme { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int NarrativeProfileId { get; set; }
        public int? SeriesId { get; set; }
        public int? EpisodeId { get; set; }
        public string? PlannerMode { get; set; }
    }

    public sealed class GenerateNextChunkRequest
    {
        public long StoryId { get; set; }
        public int WriterAgentId { get; set; }
    }
}

