using Microsoft.AspNetCore.Mvc;
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

        public CommandsApiController(
            ICommandDispatcher dispatcher,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            StoriesService storiesService,
            ICustomLogger logger)
        {
            _dispatcher = dispatcher;
            _database = database;
            _kernelFactory = kernelFactory;
            _storiesService = storiesService;
            _logger = logger;
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
            var cmd = new TransformStoryRawToTaggedCommand(
                storyId,
                _database,
                _kernelFactory,
                _storiesService,
                _logger);

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
    }
}
