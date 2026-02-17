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

        public LogsApiController(DatabaseService db, ICustomLogger logger, ICommandDispatcher dispatcher)
        {
            _db = db;
            _logger = logger;
            _dispatcher = dispatcher;
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
    }
}
