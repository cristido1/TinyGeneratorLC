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
        private readonly ICustomLogger _logger;

        public CommandsApiController(
            ICommandDispatcher dispatcher,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            ICustomLogger logger)
        {
            _dispatcher = dispatcher;
            _database = database;
            _kernelFactory = kernelFactory;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult GetActiveCommands()
        {
            var commands = _dispatcher.GetActiveCommands();
            return Ok(commands);
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

            if (string.IsNullOrWhiteSpace(story.Story))
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
