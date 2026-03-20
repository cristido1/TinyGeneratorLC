using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public class LogAnalysisService
    {
        private readonly DatabaseService _database;
        private readonly ICallCenter _callCenter;
        private readonly ICustomLogger? _logger;

        public LogAnalysisService(
            DatabaseService database,
            ICallCenter callCenter,
            ICustomLogger? logger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _callCenter = callCenter ?? throw new ArgumentNullException(nameof(callCenter));
            _logger = logger;
        }

        public async Task<(bool success, string? message)> AnalyzeThreadAsync(
            string threadId,
            string? overrideScope,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(threadId))
                return (false, "ThreadId mancante");

            var logs = _database.GetLogsByThread(threadId);
            if (logs == null || logs.Count == 0)
                return (false, "Nessun log trovato per il thread specificato");

            var scope = !string.IsNullOrWhiteSpace(overrideScope)
                ? overrideScope
                : logs.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l.ThreadScope))?.ThreadScope
                    ?? $"thread_{threadId}";

            var agent = _database.GetAgentByRole("log_analyzer");
            if (agent == null || !agent.IsActive)
                return (false, "Nessun agente attivo con ruolo log_analyzer");

            if (!agent.ModelId.HasValue)
                return (false, "L'agente log_analyzer non ha un modello associato");

            var modelInfo = _database.GetModelInfoById(agent.ModelId.Value);
            var modelName = modelInfo?.Name;
            if (string.IsNullOrWhiteSpace(modelName))
                return (false, "Impossibile determinare il modello per l'agente log_analyzer");

            var systemMessage = BuildSystemMessage(agent);
            var userPrompt = BuildUserPrompt(scope, logs, agent.UserPrompt);

            // Remove previous analyses before running a new one
            _database.DeleteLogAnalysesByThread(threadId);
            _database.SetLogAnalyzed(threadId, false);

            string textContent;
            try
            {
                var history = new ChatHistory();
                history.AddSystem(systemMessage);
                history.AddUser(userPrompt);

                var options = new CallOptions
                {
                    Operation = "log_analysis",
                    Timeout = TimeSpan.FromSeconds(180),
                    MaxRetries = 1,
                    UseResponseChecker = false,
                    AskFailExplanation = true,
                    AllowFallback = true
                };
                options.DeterministicChecks.Add(new CheckAlwaysSuccess());

                var callResult = await _callCenter.CallAgentAsync(
                    storyId: 0,
                    threadId: threadId.GetHashCode(StringComparison.Ordinal),
                    agent: agent,
                    history: history,
                    options: options,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!callResult.Success)
                {
                    return (false, $"Analisi non riuscita: {callResult.FailureReason ?? "n/a"}");
                }

                textContent = callResult.ResponseText;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LogAnalyzer", $"Chiamata modello fallita: {ex.Message}", ex.ToString());
                return (false, $"Analisi non riuscita: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(textContent))
            {
                return (false, "Il modello non ha fornito una risposta utile");
            }

            var analysis = new LogAnalysis
            {
                ThreadId = threadId,
                // log_analysis.model_id has FK -> models.Id; persist the numeric model id.
                ModelId = agent.ModelId.Value.ToString(),
                RunScope = scope,
                Description = textContent.Trim(),
                Succeeded = true
            };

            _database.InsertLogAnalysis(analysis);
            _database.SetLogAnalyzed(threadId, true);

            return (true, "Analisi completata");
        }

        public async Task<(bool success, string? message)> AnalyzeFailureAsync(string failureContext, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(failureContext))
                return (false, "Failure context mancante");

            var agent = _database.GetAgentByRole("log_analyzer")
                ?? _database.GetAgentByRole("error_analyzer");
            if (agent == null || !agent.IsActive)
                return (false, "Nessun agente attivo con ruolo log_analyzer");

            if (!agent.ModelId.HasValue)
                return (false, "L'agente log_analyzer non ha un modello associato");

            var modelInfo = _database.GetModelInfoById(agent.ModelId.Value);
            var modelName = modelInfo?.Name;
            if (string.IsNullOrWhiteSpace(modelName))
                return (false, "Impossibile determinare il modello per l'agente log_analyzer");

            var systemMessage = BuildFailureSystemPrompt();
            string textContent;
            try
            {
                var history = new ChatHistory();
                history.AddSystem(systemMessage);
                history.AddUser(failureContext);

                var options = new CallOptions
                {
                    Operation = "log_analysis_failure",
                    Timeout = TimeSpan.FromSeconds(180),
                    MaxRetries = 1,
                    UseResponseChecker = false,
                    AskFailExplanation = true,
                    AllowFallback = true
                };
                options.DeterministicChecks.Add(new CheckAlwaysSuccess());

                var callResult = await _callCenter.CallAgentAsync(
                    storyId: 0,
                    threadId: "log_analyzer_failure".GetHashCode(StringComparison.Ordinal),
                    agent: agent,
                    history: history,
                    options: options,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!callResult.Success)
                {
                    return (false, $"Analisi non riuscita: {callResult.FailureReason ?? "n/a"}");
                }

                textContent = callResult.ResponseText;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LogAnalyzer", $"Chiamata modello fallita: {ex.Message}", ex.ToString());
                return (false, $"Analisi non riuscita: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(textContent))
            {
                return (false, "Il modello non ha fornito una risposta utile");
            }

            return (true, textContent.Trim());
        }

        private static string BuildSystemMessage(Agent agent)
        {
            if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
                return agent.SystemPrompt!;

            return "Sei un analista senior che esamina log tecnici. Riassumi l'operazione, evidenzia errori o anomalie e suggerisci azioni correttive. Rispondi in testo libero.";
        }

        private static string BuildUserPrompt(string scope, List<LogEntry> logs, string? agentPrompt)
        {
            var compact = BuildCompactPayload(logs);
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(agentPrompt))
            {
                sb.AppendLine(agentPrompt);
                sb.AppendLine();
            }

            sb.AppendLine($"[COMMAND] {scope}");
            sb.AppendLine();
            sb.AppendLine($"[THREAD_ID] {logs.First().ThreadId ?? 0}");
            sb.AppendLine();
            sb.AppendLine("[ERROR_SUMMARY]");
            if (compact.ErrorSummaryLines.Count == 0)
            {
                sb.AppendLine("none");
            }
            else
            {
                foreach (var line in compact.ErrorSummaryLines)
                {
                    sb.AppendLine(line);
                }
            }
            sb.AppendLine();
            sb.AppendLine("[FINAL_SUCCESS]");
            sb.AppendLine(compact.FinalSuccess ? "true" : "false");
            sb.AppendLine();
            sb.AppendLine("[RETRIES]");
            sb.AppendLine(compact.TotalRetries.ToString());
            sb.AppendLine();
            sb.AppendLine("[REPEATED_ERRORS]");
            sb.AppendLine(compact.RepeatedErrors ? "true" : "false");
            sb.AppendLine();

            if (compact.KeyEvents.Count > 0)
            {
                sb.AppendLine("[KEY_EVENTS]");
                foreach (var evt in compact.KeyEvents)
                {
                    sb.AppendLine(evt);
                }
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(compact.LastValidResponse))
            {
                sb.AppendLine("[LAST_VALID_RESPONSE]");
                sb.AppendLine(compact.LastValidResponse);
                sb.AppendLine();
            }

            sb.AppendLine("[NOTES]");
            sb.AppendLine($"source_logs={logs.Count}, considered_logs={compact.ConsideredLogs}");
            sb.AppendLine("Analizza i dati compatti sopra riportati e rispondi in italiano, senza inventare dettagli.");

            return sb.ToString();
        }

        private sealed class CompactPayload
        {
            public List<string> ErrorSummaryLines { get; } = new();
            public List<string> KeyEvents { get; } = new();
            public bool FinalSuccess { get; set; }
            public int TotalRetries { get; set; }
            public bool RepeatedErrors { get; set; }
            public string? LastValidResponse { get; set; }
            public int ConsideredLogs { get; set; }
        }

        private static CompactPayload BuildCompactPayload(List<LogEntry> logs)
        {
            var payload = new CompactPayload();
            if (logs == null || logs.Count == 0)
                return payload;

            var responseLogs = logs.Where(IsResponseLog).ToList();
            payload.TotalRetries = Math.Max(0, responseLogs.Count - 1);

            var lastResponseWithResult = responseLogs
                .LastOrDefault(l => !string.IsNullOrWhiteSpace(l.Result));
            payload.FinalSuccess = string.Equals(lastResponseWithResult?.Result?.Trim(), "SUCCESS", StringComparison.OrdinalIgnoreCase);

            var lastSuccess = responseLogs.LastOrDefault(l =>
                string.Equals(l.Result?.Trim(), "SUCCESS", StringComparison.OrdinalIgnoreCase));
            if (lastSuccess != null)
            {
                payload.LastValidResponse = NormalizeInlineText(lastSuccess.Message, 180);
            }

            var ruleTagCounts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            var genericCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var keyEventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in logs)
            {
                if (!ShouldIncludeForCompactAnalysis(entry))
                    continue;

                payload.ConsideredLogs++;

                var reasons = EnumerateReasonSources(entry).ToList();
                if (reasons.Count == 0)
                {
                    continue;
                }

                foreach (var reason in reasons)
                {
                    if (TryExtractRuleAndTags(reason, out var ruleKey, out var tags))
                    {
                        if (!ruleTagCounts.TryGetValue(ruleKey, out var tagCounts))
                        {
                            tagCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            ruleTagCounts[ruleKey] = tagCounts;
                        }

                        foreach (var tag in tags)
                        {
                            if (string.IsNullOrWhiteSpace(tag))
                                continue;
                            tagCounts[tag] = tagCounts.TryGetValue(tag, out var curr) ? curr + 1 : 1;
                        }
                        continue;
                    }

                    var genericKey = Truncate(NormalizeInlineText(reason, 120), 80);
                    if (string.IsNullOrWhiteSpace(genericKey))
                        continue;
                    genericCounts[genericKey] = genericCounts.TryGetValue(genericKey, out var currGeneric) ? currGeneric + 1 : 1;
                }
            }

            var orderedRuleKeys = ruleTagCounts.Keys
                .OrderBy(k => RuleSortWeight(k))
                .ThenBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var ruleKey in orderedRuleKeys)
            {
                var topTags = ruleTagCounts[ruleKey]
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .Select(kv => $"{kv.Key} (x{kv.Value})")
                    .ToList();
                if (topTags.Count > 0)
                {
                    payload.ErrorSummaryLines.Add($"{ruleKey}: {string.Join(", ", topTags)}");
                }
            }

            var topGeneric = genericCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(kv => $"{kv.Key} (x{kv.Value})")
                .ToList();
            if (topGeneric.Count > 0)
            {
                payload.ErrorSummaryLines.Add($"GEN: {string.Join("; ", topGeneric)}");
            }

            payload.RepeatedErrors = ruleTagCounts.Values.Any(tags => tags.Values.Any(v => v >= 3))
                || genericCounts.Values.Any(v => v >= 3);

            foreach (var rawEvent in logs
                         .Where(ShouldIncludeForCompactAnalysis)
                         .Select(ComposeKeyEvent)
                         .Where(v => !string.IsNullOrWhiteSpace(v)))
            {
                var key = rawEvent!;
                keyEventCounts[key] = keyEventCounts.TryGetValue(key, out var curr) ? curr + 1 : 1;
            }

            payload.KeyEvents.AddRange(
                keyEventCounts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .Select(kv => $"{kv.Key} (x{kv.Value})"));

            return payload;
        }

        private static bool ShouldIncludeForCompactAnalysis(LogEntry entry)
        {
            if (entry == null)
                return false;
            if (IsRequestLog(entry))
                return false;

            if (!string.IsNullOrWhiteSpace(entry.Exception))
                return true;
            if (!string.IsNullOrWhiteSpace(entry.ResultFailReason))
                return true;

            var result = entry.Result?.Trim();
            if (string.Equals(result, "FAILED", StringComparison.OrdinalIgnoreCase))
                return true;

            var level = entry.Level?.Trim();
            if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(level, "CRITICAL", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static bool IsRequestLog(LogEntry entry)
        {
            var category = entry.Category?.Trim() ?? string.Empty;
            return category.Equals("ModelRequest", StringComparison.OrdinalIgnoreCase)
                || category.Equals("ModelPrompt", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsResponseLog(LogEntry entry)
        {
            var category = entry.Category?.Trim() ?? string.Empty;
            return category.Equals("ModelResponse", StringComparison.OrdinalIgnoreCase)
                || category.Equals("ModelCompletion", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> EnumerateReasonSources(LogEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.ResultFailReason))
            {
                yield return entry.ResultFailReason!;
            }

            if (!string.IsNullOrWhiteSpace(entry.Exception))
            {
                yield return entry.Exception!;
            }

            if (!string.IsNullOrWhiteSpace(entry.Message))
            {
                var message = entry.Message!;
                if (IsPotentialReasonMessage(message))
                {
                    yield return message;
                }
            }
        }

        private static bool IsPotentialReasonMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;
            return message.Contains("reason", StringComparison.OrdinalIgnoreCase)
                || message.Contains("errore", StringComparison.OrdinalIgnoreCase)
                || message.Contains("error", StringComparison.OrdinalIgnoreCase)
                || message.Contains("violat", StringComparison.OrdinalIgnoreCase)
                || message.Contains("invalid", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryExtractRuleAndTags(string reason, out string ruleKey, out List<string> tags)
        {
            ruleKey = string.Empty;
            tags = new List<string>();

            if (string.IsNullOrWhiteSpace(reason))
                return false;

            var normalized = NormalizeInlineText(reason, 400);
            var match = Regex.Match(normalized, @"\b(?:regola|rule|r)\s*[:#-]?\s*(3|5|6)\b", RegexOptions.IgnoreCase);
            if (!match.Success)
                return false;

            ruleKey = $"R{match.Groups[1].Value}";

            var sourceForTags = normalized;
            var colonIdx = normalized.IndexOf(':');
            if (colonIdx >= 0 && colonIdx + 1 < normalized.Length)
            {
                sourceForTags = normalized.Substring(colonIdx + 1);
            }

            var parts = sourceForTags
                .Split(new[] { ',', ';', '|', '/' }, StringSplitOptions.RemoveEmptyEntries);

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in parts)
            {
                var tag = NormalizeTag(raw);
                if (string.IsNullOrWhiteSpace(tag))
                    continue;
                if (unique.Add(tag))
                {
                    tags.Add(tag);
                }
            }

            if (tags.Count == 0)
            {
                tags.Add("generic");
            }

            return true;
        }

        private static string NormalizeTag(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var token = NormalizeInlineText(input, 40).ToLowerInvariant();
            token = token.Replace("presenza di", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("presence of", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("tag", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("tags", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            token = Regex.Replace(token, @"^[\W_]+|[\W_]+$", string.Empty);
            if (token.Length < 2)
                return string.Empty;
            if (token.Length > 24)
                return string.Empty;
            if (!Regex.IsMatch(token, @"^[a-z0-9_-]+$"))
                return string.Empty;
            if (Regex.IsMatch(token, @"^(rule|regola|r3|r5|r6|violazione|invalid)$"))
                return string.Empty;
            return token;
        }

        private static string ComposeKeyEvent(LogEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.ResultFailReason))
                return Truncate($"fail_reason: {NormalizeInlineText(entry.ResultFailReason, 90)}", 100);
            if (!string.IsNullOrWhiteSpace(entry.Exception))
                return Truncate($"exception: {NormalizeInlineText(entry.Exception, 90)}", 100);
            return Truncate($"event: {NormalizeInlineText(entry.Message, 90)}", 100);
        }

        private static int RuleSortWeight(string ruleKey)
        {
            return ruleKey switch
            {
                "R3" => 1,
                "R5" => 2,
                "R6" => 3,
                _ => 99
            };
        }

        private static string NormalizeInlineText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            return Truncate(cleaned, maxLength);
        }

        private static string BuildFailureSystemPrompt()
        {
            return @"You are an AI system specialized in technical failure analysis for AI pipelines.
You will receive either:
- a request and a response that resulted in an error,
- or a failed command with an error message.

Your tasks:
1. Identify the most probable technical reason for the failure
2. Suggest one practical corrective action

Rules:
- Be concise and technical
- Do not rewrite or modify the original content
- Do not invent missing data
- If the cause is uncertain, say so clearly
- The suggested action must be realistic and technical

Output format (plain text only):

Failure reason: <short technical explanation>
Suggested action: <practical technical suggestion>";
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }
    }
}
