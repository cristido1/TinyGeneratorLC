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
                _logger
            );

            _dispatcher.Enqueue(
                "SummarizeStory",
                async ctx => {
                    bool success = await cmd.ExecuteAsync(ctx.CancellationToken);
                    return new CommandResult(success, success ? "Summary generated" : "Failed to generate summary");
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

            var cmd = new BatchSummarizeStoriesCommand(
                _database,
                _kernelFactory,
                _dispatcher,
                _logger,
                minScore
            );

            // Accoda il comando batch che a sua volta accoderà i comandi individuali
            _dispatcher.Enqueue(
                "BatchSummarizeStories",
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

