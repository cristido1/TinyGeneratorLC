using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services;
using TinyGenerator.Models;

namespace TinyGenerator.Controllers
{
    [ApiController]
    [Route("api/logs")]
    public class LogsApiController : ControllerBase
    {
        private readonly DatabaseService _db;
        private readonly ICustomLogger _logger;
        private readonly ICommandDispatcher _dispatcher;
        private readonly NumeratorService _numerator;
        private readonly LogAnalysisService _analysisService;

        public LogsApiController(
            DatabaseService db,
            ICustomLogger logger,
            ICommandDispatcher dispatcher,
            NumeratorService numerator,
            LogAnalysisService analysisService)
        {
            _db = db;
            _logger = logger;
            _dispatcher = dispatcher;
            _numerator = numerator;
            _analysisService = analysisService;
        }

        /// <summary>
        /// Get recent logs with optional filtering
        /// </summary>
        [HttpGet("recent")]
        public IActionResult GetRecentLogs([FromQuery] int limit = 200, [FromQuery] int offset = 0, [FromQuery] string? level = null, [FromQuery] string? category = null)
        {
            try
            {
                var logs = _db.GetRecentLogs(limit: limit, offset: offset, level: level, category: category);
                return Ok(logs);
            }
            catch (System.Exception ex)
            {
                _logger.Log("Error", "LogsApi", "Failed to get recent logs", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get logs for a specific ThreadId.
        /// </summary>
        [HttpGet("thread/{threadId:int}")]
        public IActionResult GetLogsByThreadId([FromRoute] int threadId, [FromQuery] int limit = 500)
        {
            try
            {
                if (limit <= 0) limit = 500;
                if (limit > 5000) limit = 5000;

                var logs = _db.GetLogsByThreadId(threadId)
                    ?? new List<LogEntry>();

                if (logs.Count > limit)
                {
                    logs = logs.GetRange(0, limit);
                }

                return Ok(logs);
            }
            catch (System.Exception ex)
            {
                _logger.Log("Error", "LogsApi", $"Failed to get logs for threadId={threadId}", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get log count with optional filtering
        /// </summary>
        [HttpGet("count")]
        public IActionResult GetLogCount([FromQuery] string? level = null)
        {
            try
            {
                var count = _db.GetLogCount(level);
                return Ok(new { count });
            }
            catch (System.Exception ex)
            {
                _logger.Log("Error", "LogsApi", "Failed to get log count", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Clear all logs
        /// </summary>
        [HttpPost("clear")]
        public async Task<IActionResult> ClearLogs()
        {
            try
            {
                var runId = $"update_model_stats_pre_clear_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                var handle = _dispatcher.Enqueue(
                    "update_model_stats",
                    ctx =>
                    {
                        _db.RefreshModelStatsFromAllLogs();
                        return Task.FromResult(new CommandResult(true, "Model stats refreshed from all logs"));
                    },
                    runId: runId,
                    threadScope: "system/model_stats",
                    metadata: new Dictionary<string, string>
                    {
                        ["operation"] = "update_model_stats",
                        ["trigger"] = "clear_logs"
                    },
                    priority: 1,
                    batch: true);

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                var statsResult = await _dispatcher.WaitForCompletionAsync(handle.RunId, cts.Token);
                if (!statsResult.Success)
                {
                    return StatusCode(409, new
                    {
                        message = "ClearLogs bloccato: aggiornamento stats_models fallito.",
                        statsRunId = handle.RunId,
                        statsError = statsResult.Message
                    });
                }

                _db.ClearLogs();
                _numerator.ResetThreadIds();
                _logger.Log("Information", "LogsApi", $"Logs cleared (statsRunId={handle.RunId})");
                return Ok(new
                {
                    message = "Logs cleared",
                    statsRunId = handle.RunId,
                    statsMessage = statsResult.Message
                });
            }
            catch (System.Exception ex)
            {
                _logger.Log("Error", "LogsApi", "Failed to clear logs", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get logs grouped by category with stats
        /// </summary>
        [HttpGet("stats")]
        public IActionResult GetLogStats()
        {
            try
            {
                var logs = _db.GetRecentLogs(limit: 500);
                var stats = new Dictionary<string, object>();
                var byLevel = new Dictionary<string, int>();
                var byCategory = new Dictionary<string, int>();

                foreach (var log in logs)
                {
                    if (!byLevel.ContainsKey(log.Level)) byLevel[log.Level] = 0;
                    byLevel[log.Level]++;

                    var cat = log.Category ?? "Unknown";
                    if (!byCategory.ContainsKey(cat)) byCategory[cat] = 0;
                    byCategory[cat]++;
                }

                stats["byLevel"] = byLevel;
                stats["byCategory"] = byCategory;
                stats["total"] = logs.Count;

                return Ok(stats);
            }
            catch (System.Exception ex)
            {
                _logger.Log("Error", "LogsApi", "Failed to get log stats", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get paged logs for the grid (server-side lazy loading)
        /// </summary>
        [HttpGet("paged")]
        public IActionResult GetPagedLogs(
            [FromQuery] int start = 0,
            [FromQuery] int length = 25,
            [FromQuery] string? search = null,
            [FromQuery] string? sortBy = "timestamp",
            [FromQuery] bool sortDesc = true,
            [FromQuery] bool onlyResult = false,
            [FromQuery] bool onlyModel = true)
        {
            try
            {
                var page = _db.GetPagedLogsForGrid(
                    start: start,
                    length: length,
                    search: search,
                    sortBy: sortBy,
                    sortDesc: sortDesc,
                    onlyResult: onlyResult,
                    onlyModel: onlyModel);

                var items = page.Items.Select(log => new
                {
                    id = log.Id ?? 0,
                    timestamp = log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    level = log.Level ?? string.Empty,
                    source = log.Source ?? string.Empty,
                    result = log.Result ?? string.Empty,
                    failReason = log.ResultFailReason ?? string.Empty,
                    operation = DatabaseService.NormalizeOperationForDisplay(log.ThreadScope),
                    duration = log.DurationSecs.HasValue && log.DurationSecs.Value > 0 ? log.DurationSecs.Value : 1,
                    threadId = log.ThreadId.HasValue && log.ThreadId.Value > 0 ? log.ThreadId.Value.ToString() : "-",
                    storyId = log.StoryId.HasValue && log.StoryId.Value > 0 ? log.StoryId.Value.ToString() : "-",
                    agent = string.IsNullOrWhiteSpace(log.AgentName) ? "-" : log.AgentName,
                    model = string.IsNullOrWhiteSpace(log.ModelName) ? "-" : log.ModelName,
                    threadIdRaw = log.ThreadId ?? 0,
                    threadScope = log.ThreadScope ?? string.Empty,
                    analized = log.Analized
                });

                return Ok(new
                {
                    items = items.ToList(),
                    total = page.TotalRecords,
                    filtered = page.FilteredRecords
                });
            }
            catch (System.Exception ex)
            {
                _logger.Log("Error", "LogsApi", "Failed to get paged logs", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get full message content for a single log entry
        /// </summary>
        [HttpGet("message/{id:long}")]
        public IActionResult GetMessage([FromRoute] long id)
        {
            try
            {
                var log = _db.GetLogById(id);
                if (log == null) return NotFound();

                return Ok(new
                {
                    id = log.Id,
                    message = log.Message ?? string.Empty,
                    chatText = log.ChatText ?? string.Empty,
                    exception = log.Exception ?? string.Empty
                });
            }
            catch (System.Exception ex)
            {
                _logger.Log("Error", "LogsApi", $"Failed to get message for log {id}", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Export logs as CSV (all or filtered by threadId)
        /// </summary>
        [HttpGet("export")]
        public IActionResult ExportLogs([FromQuery] int? threadId = null)
        {
            try
            {
                var logs = threadId.HasValue && threadId.Value > 0
                    ? _db.GetLogsByThreadId(threadId.Value)
                    : _db.GetRecentLogs(limit: 10000);

                var csv = new System.Text.StringBuilder();
                csv.AppendLine("Timestamp,Level,Operation,ThreadId,StoryId,AgentName,Message,Source");

                foreach (var log in logs)
                {
                    var storyIdValue = log.StoryId.HasValue ? log.StoryId.Value.ToString() : "";
                    var threadIdValue = log.ThreadId.HasValue ? log.ThreadId.Value : 0;
                    csv.AppendLine($"\"{log.Timestamp:yyyy-MM-dd HH:mm:ss}\",\"{log.Level}\",\"{log.ThreadScope}\",{threadIdValue},{storyIdValue},\"{log.AgentName}\",\"{EscapeCsv(log.Message)}\",\"{log.Source}\"");
                }

                var fileName = threadId.HasValue && threadId.Value > 0
                    ? $"logs_thread_{threadId.Value}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv"
                    : $"logs_all_{DateTime.UtcNow:yyyyMMddHHmmss}.csv";

                return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", fileName);
            }
            catch (System.Exception ex)
            {
                _logger.Log("Error", "LogsApi", "Failed to export logs", ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        public class AnalyzeRequest
        {
            public string? ThreadId { get; set; }
            public string? ThreadScope { get; set; }
        }

        /// <summary>
        /// Analyze a specific thread's logs
        /// </summary>
        [HttpPost("analyze")]
        public IActionResult AnalyzeThread([FromBody] AnalyzeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.ThreadId))
                return BadRequest(new { error = "ThreadId non valido." });

            var threadId = request.ThreadId;
            var safeScope = string.IsNullOrWhiteSpace(request.ThreadScope)
                ? $"thread_{threadId}"
                : request.ThreadScope;
            var sanitizedScope = SanitizeIdentifier(safeScope);

            var runId = $"logan_{threadId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            _dispatcher.Enqueue(
                "log_analyzer",
                async ctx =>
                {
                    try
                    {
                        var (success, message) = await _analysisService.AnalyzeThreadAsync(threadId, safeScope, ctx.CancellationToken);
                        return new CommandResult(success, message ?? (success ? "Analisi completata" : "Analisi fallita"));
                    }
                    catch (Exception ex)
                    {
                        _logger.Log("Error", "LogsApi", $"Analisi log fallita per thread {threadId}", ex.Message);
                        return new CommandResult(false, ex.Message);
                    }
                },
                runId: runId,
                threadScope: $"log/{sanitizedScope}",
                metadata: new Dictionary<string, string>
                {
                    ["threadId"] = threadId,
                    ["threadScope"] = safeScope,
                    ["agentName"] = "log_analyzer",
                    ["modelName"] = "analysis_engine"
                });

            return Ok(new { message = $"Analisi avviata per il thread {threadId}." });
        }

        /// <summary>
        /// Analyze all threads pending analysis
        /// </summary>
        [HttpPost("analyze-missing")]
        public IActionResult AnalyzeMissing()
        {
            var pending = _db.ListThreadsPendingAnalysis(500);
            if (pending.Count == 0)
                return Ok(new { message = "Nessun thread in attesa di analisi.", count = 0 });

            foreach (var tid in pending)
            {
                var threadId = tid.ToString();
                var runId = $"logan_{threadId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                var sanitizedScope = SanitizeIdentifier($"thread_{threadId}");

                _dispatcher.Enqueue(
                    "log_analyzer",
                    async ctx =>
                    {
                        try
                        {
                            var (success, message) = await _analysisService.AnalyzeThreadAsync(threadId, null, ctx.CancellationToken);
                            return new CommandResult(success, message ?? (success ? "Analisi completata" : "Analisi fallita"));
                        }
                        catch (Exception ex)
                        {
                            _logger.Log("Error", "LogsApi", $"Analisi log fallita per thread {threadId}", ex.Message);
                            return new CommandResult(false, ex.Message);
                        }
                    },
                    runId: runId,
                    threadScope: $"log/{sanitizedScope}",
                    metadata: new Dictionary<string, string>
                    {
                        ["agentName"] = "log_analyzer",
                        ["modelName"] = "analysis_engine",
                        ["threadId"] = threadId,
                        ["threadScope"] = sanitizedScope
                    });
            }

            return Ok(new { message = $"Analisi avviata per {pending.Count} thread.", count = pending.Count });
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", "");
        }

        private static string SanitizeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "operation";
            var chars = value.Trim();
            var buffer = new char[chars.Length];
            int idx = 0;
            foreach (var ch in chars)
            {
                buffer[idx++] = char.IsLetterOrDigit(ch) ? ch : '_';
            }
            var result = new string(buffer).Trim('_');
            return string.IsNullOrWhiteSpace(result) ? "operation" : result;
        }
    }
}
