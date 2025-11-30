using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Pages.Logs
{
    public class LogsModel : PageModel
    {
        private readonly DatabaseService _db;
        private readonly LogAnalysisService _analysisService;
        private readonly ICommandDispatcher _dispatcher;
        private readonly ILogger<LogsModel>? _logger;

        public List<LogEntry> LogEntries { get; private set; } = new();

        [TempData]
        public string? StatusMessage { get; set; }

        public LogsModel(
            DatabaseService db,
            LogAnalysisService analysisService,
            ICommandDispatcher dispatcher,
            ILogger<LogsModel>? logger = null)
        {
            _db = db;
            _analysisService = analysisService;
            _dispatcher = dispatcher;
            _logger = logger;
        }

        public void OnGet()
        {
            LogEntries = _db.GetRecentLogs(limit: 1000);
        }

        public IActionResult OnPostAnalyze(string threadId, string? threadScope)
        {
            if (string.IsNullOrWhiteSpace(threadId))
            {
                StatusMessage = "ThreadId non valido.";
                return RedirectToPage();
            }

            var safeScope = string.IsNullOrWhiteSpace(threadScope) ? $"thread_{threadId}" : threadScope;
            var sanitizedScope = SanitizeIdentifier(safeScope);

            var runId = $"logan_{threadId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            _dispatcher.Enqueue(
                $"log_analyzer",
                async ctx =>
                {
                    try
                    {
                        var (success, message) = await _analysisService.AnalyzeThreadAsync(threadId, safeScope, ctx.CancellationToken);
                        return new CommandResult(success, message ?? (success ? "Analisi completata" : "Analisi fallita"));
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Analisi log fallita per thread {Thread}", threadId);
                        return new CommandResult(false, ex.Message);
                    }
                },
                runId: runId,
                threadScope: $"log/{sanitizedScope}",
                metadata: new Dictionary<string, string>
                {
                    ["threadId"] = threadId,
                    ["threadScope"] = safeScope
                });

            StatusMessage = $"Analisi avviata per il thread {threadId}.";
            return RedirectToPage();
        }

        public IActionResult OnPostClearAllLogs()
        {
            try
            {
                _db.ClearLogs();
                StatusMessage = "Tutti i log e le analisi sono stati cancellati con successo.";
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Errore durante la cancellazione dei log");
                StatusMessage = $"Errore durante la cancellazione: {ex.Message}";
            }
            return RedirectToPage();
        }

        public IActionResult OnGetExport(int? threadId)
        {
            try
            {
                var logs = threadId.HasValue && threadId.Value > 0
                    ? _db.GetLogsByThreadId(threadId.Value)
                    : _db.GetRecentLogs(limit: 10000);

                var csv = new System.Text.StringBuilder();
                csv.AppendLine("Timestamp,Level,Operation,ThreadId,AgentName,Message,Source");

                foreach (var log in logs)
                {
                    csv.AppendLine($"\"{log.Timestamp:yyyy-MM-dd HH:mm:ss}\",\"{log.Level}\",\"{log.ThreadScope}\",{log.ThreadId},\"{log.AgentName}\",\"{EscapeCsv(log.Message)}\",\"{log.Source}\"");
                }

                var fileName = threadId.HasValue && threadId.Value > 0
                    ? $"logs_thread_{threadId.Value}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv"
                    : $"logs_all_{DateTime.UtcNow:yyyyMMddHHmmss}.csv";

                return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", fileName);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Errore durante l'export dei log");
                StatusMessage = $"Errore durante l'export: {ex.Message}";
                return RedirectToPage();
            }
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
