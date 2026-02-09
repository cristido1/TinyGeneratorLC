using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public sealed class SystemReportService
    {
        private readonly DatabaseService _database;
        private readonly LogAnalysisService _logAnalysis;
        private readonly ICustomLogger? _logger;

        public SystemReportService(DatabaseService database, LogAnalysisService logAnalysis, ICustomLogger? logger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _logAnalysis = logAnalysis ?? throw new ArgumentNullException(nameof(logAnalysis));
            _logger = logger;
        }

        public sealed record FailureContext(
            string OperationName,
            string? OperationType,
            string? Message,
            string? Exception,
            int? ThreadId,
            long? StoryCorrelationId,
            string? AgentName,
            string? ModelName,
            int? RetryCount,
            int? ExecutionTimeMs,
            string? RawLogRef);

        public async Task ReportFailureAsync(FailureContext ctx, CancellationToken ct = default)
        {
            if (ctx == null) return;

            try
            {
                var agentName = string.IsNullOrWhiteSpace(ctx.AgentName) ? null : ctx.AgentName!.Trim();
                var modelName = string.IsNullOrWhiteSpace(ctx.ModelName) ? null : ctx.ModelName!.Trim();
                string? agentRole = null;

                if (!string.IsNullOrWhiteSpace(agentName))
                {
                    var agent = _database.GetAgentByName(agentName!);
                    agentRole = agent?.Role;
                    if (string.IsNullOrWhiteSpace(modelName) && agent?.ModelId != null)
                    {
                        var modelInfo = _database.GetModelInfoById(agent.ModelId.Value);
                        modelName = modelInfo?.Name ?? modelName;
                    }
                }

                string? requestJson = null;
                string? responseJson = null;
                if (ctx.ThreadId.HasValue && ctx.ThreadId.Value > 0)
                {
                    var logs = _database.GetLogsByThreadId(ctx.ThreadId.Value);
                    requestJson = ExtractLatestPayload(logs, "ModelRequest", "REQUEST_JSON:");
                    responseJson = ExtractLatestPayload(logs, "ModelResponse", "RESPONSE_JSON:")
                        ?? ExtractLatestPayload(logs, "ModelCompletion", "RESPONSE:");

                    if (string.IsNullOrWhiteSpace(modelName))
                    {
                        modelName = ExtractLatestModelName(logs);
                    }

                    if (string.IsNullOrWhiteSpace(agentName))
                    {
                        agentName = ExtractLatestAgentName(logs);
                        if (!string.IsNullOrWhiteSpace(agentName))
                        {
                            var agent = _database.GetAgentByName(agentName);
                            agentRole = agent?.Role ?? agentRole;
                            if (string.IsNullOrWhiteSpace(modelName) && agent?.ModelId != null)
                            {
                                var modelInfo = _database.GetModelInfoById(agent.ModelId.Value);
                                modelName = modelInfo?.Name ?? modelName;
                            }
                        }
                    }
                }

                long? storyDbId = null;
                int? seriesId = null;
                int? seriesEpisode = null;
                if (ctx.StoryCorrelationId.HasValue && ctx.StoryCorrelationId.Value > 0)
                {
                    var story = _database.GetStoryByCorrelationId(ctx.StoryCorrelationId.Value);
                    if (story != null)
                    {
                        storyDbId = story.Id;
                        seriesId = story.SerieId;
                        seriesEpisode = story.SerieEpisode;
                    }
                }

                var analysisInput = BuildFailureAnalysisInput(
                    operation: ctx.OperationName,
                    message: ctx.Message,
                    exception: ctx.Exception,
                    requestJson: requestJson,
                    responseJson: responseJson);

                string? analysisText = null;
                var analysisResult = await _logAnalysis.AnalyzeFailureAsync(analysisInput, ct).ConfigureAwait(false);
                if (analysisResult.success)
                {
                    analysisText = analysisResult.message;
                }

                var report = new SystemReport
                {
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    Severity = "error",
                    Status = "new",
                    Deleted = false,
                    Title = $"{ctx.OperationName} failed",
                    Message = ctx.Message ?? ctx.Exception ?? string.Empty,
                    FailureReason = analysisText,
                    AgentName = agentName,
                    AgentRole = agentRole,
                    ModelName = modelName,
                    StoryId = storyDbId ?? ctx.StoryCorrelationId,
                    SeriesId = seriesId,
                    SeriesEpisode = seriesEpisode,
                    OperationType = string.IsNullOrWhiteSpace(ctx.OperationType) ? ctx.OperationName : ctx.OperationType,
                    ExecutionTimeMs = ctx.ExecutionTimeMs,
                    RetryCount = ctx.RetryCount,
                    RawLogRef = ctx.RawLogRef
                };

                _database.InsertSystemReport(report);
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "SystemReport", $"Failed to write system report: {ex.Message}", ex.ToString());
            }
        }

        private static string BuildFailureAnalysisInput(
            string operation,
            string? message,
            string? exception,
            string? requestJson,
            string? responseJson)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Operation: {operation}");
            if (!string.IsNullOrWhiteSpace(message))
            {
                sb.AppendLine("Error message:");
                sb.AppendLine(message);
            }
            if (!string.IsNullOrWhiteSpace(exception))
            {
                sb.AppendLine("Exception:");
                sb.AppendLine(exception);
            }

            if (!string.IsNullOrWhiteSpace(requestJson) || !string.IsNullOrWhiteSpace(responseJson))
            {
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(requestJson))
                {
                    sb.AppendLine("Model request:");
                    sb.AppendLine(requestJson);
                }
                if (!string.IsNullOrWhiteSpace(responseJson))
                {
                    sb.AppendLine("Model response:");
                    sb.AppendLine(responseJson);
                }
            }

            return sb.ToString();
        }

        private static string? ExtractLatestPayload(IEnumerable<LogEntry> logs, string category, string marker)
        {
            var entry = logs.FirstOrDefault(l => string.Equals(l.Category, category, StringComparison.OrdinalIgnoreCase));
            if (entry == null || string.IsNullOrWhiteSpace(entry.Message)) return null;
            var msg = entry.Message;
            var idx = msg.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return msg;
            return msg.Substring(idx + marker.Length).Trim();
        }

        private static string? ExtractLatestModelName(IEnumerable<LogEntry> logs)
        {
            var entry = logs.FirstOrDefault(l =>
                (string.Equals(l.Category, "ModelRequest", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(l.Category, "ModelResponse", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(l.Category, "ModelCompletion", StringComparison.OrdinalIgnoreCase)) &&
                (!string.IsNullOrWhiteSpace(l.ModelName) || !string.IsNullOrWhiteSpace(l.Message)));

            if (entry == null) return null;
            if (!string.IsNullOrWhiteSpace(entry.ModelName)) return entry.ModelName;

            var msg = entry.Message ?? string.Empty;
            var start = msg.IndexOf('[');
            if (start < 0) return null;
            var end = msg.IndexOf(']', start + 1);
            if (end <= start + 1) return null;
            return msg.Substring(start + 1, end - start - 1).Trim();
        }

        private static string? ExtractLatestAgentName(IEnumerable<LogEntry> logs)
        {
            var entry = logs.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.AgentName));
            return entry?.AgentName;
        }
    }
}
