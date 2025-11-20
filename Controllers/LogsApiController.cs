using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
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

        public LogsApiController(DatabaseService db, ICustomLogger logger)
        {
            _db = db;
            _logger = logger;
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
        public IActionResult ClearLogs()
        {
            try
            {
                _db.ClearLogs();
                _logger.Log("Information", "LogsApi", "Logs cleared");
                return Ok(new { message = "Logs cleared" });
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
