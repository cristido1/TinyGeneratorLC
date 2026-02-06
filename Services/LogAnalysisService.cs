using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public class LogAnalysisService
    {
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly ICustomLogger? _logger;

        public LogAnalysisService(
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            ICustomLogger? logger = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
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

            LangChainChatBridge bridge;
            try
            {
                bridge = _kernelFactory.CreateChatBridge(
                    modelName,
                    agent.Temperature,
                    agent.TopP,
                    agent.RepeatPenalty,
                    agent.TopK,
                    agent.RepeatLastN,
                    agent.NumPredict,
                    useMaxTokens: true);
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LogAnalyzer", $"Errore creazione chat bridge: {ex.Message}");
                return (false, $"Errore creazione bridge: {ex.Message}");
            }

            var systemMessage = BuildSystemMessage(agent);
            var userPrompt = BuildUserPrompt(scope, logs, agent.Prompt);

            // Remove previous analyses before running a new one
            _database.DeleteLogAnalysesByThread(threadId);
            _database.SetLogAnalyzed(threadId, false);

            var messages = new List<ConversationMessage>
            {
                new ConversationMessage { Role = "system", Content = systemMessage },
                new ConversationMessage { Role = "user", Content = userPrompt }
            };

            string responseJson;
            try
            {
                using var analyzerScope = LogScope.Push(scope, null, null, null, agent.Name, agentRole: "log_analyzer");
                responseJson = await bridge.CallModelWithToolsAsync(
                    messages,
                    new List<Dictionary<string, object>>(),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LogAnalyzer", $"Chiamata modello fallita: {ex.Message}", ex.ToString());
                return (false, $"Analisi non riuscita: {ex.Message}");
            }

            var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
            if (string.IsNullOrWhiteSpace(textContent))
            {
                return (false, "Il modello non ha fornito una risposta utile");
            }

            var analysis = new LogAnalysis
            {
                ThreadId = threadId,
                ModelId = modelName,
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

            LangChainChatBridge bridge;
            try
            {
                bridge = _kernelFactory.CreateChatBridge(
                    modelName,
                    agent.Temperature,
                    agent.TopP,
                    agent.RepeatPenalty,
                    agent.TopK,
                    agent.RepeatLastN,
                    agent.NumPredict,
                    useMaxTokens: true);
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LogAnalyzer", $"Errore creazione chat bridge: {ex.Message}");
                return (false, $"Errore creazione bridge: {ex.Message}");
            }

            var systemMessage = BuildFailureSystemPrompt();
            var messages = new List<ConversationMessage>
            {
                new ConversationMessage { Role = "system", Content = systemMessage },
                new ConversationMessage { Role = "user", Content = failureContext }
            };

            string responseJson;
            try
            {
                using var analyzerScope = LogScope.Push("log_analyzer_failure", null, null, null, agent.Name, agentRole: "log_analyzer");
                responseJson = await bridge.CallModelWithToolsAsync(
                    messages,
                    new List<Dictionary<string, object>>(),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LogAnalyzer", $"Chiamata modello fallita: {ex.Message}", ex.ToString());
                return (false, $"Analisi non riuscita: {ex.Message}");
            }

            var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
            if (string.IsNullOrWhiteSpace(textContent))
            {
                return (false, "Il modello non ha fornito una risposta utile");
            }

            return (true, textContent.Trim());
        }

        private static string BuildSystemMessage(Agent agent)
        {
            if (!string.IsNullOrWhiteSpace(agent.Instructions))
                return agent.Instructions!;

            return "Sei un analista senior che esamina log tecnici. Riassumi l'operazione, evidenzia errori o anomalie e suggerisci azioni correttive. Rispondi in testo libero.";
        }

        private static string BuildUserPrompt(string scope, List<LogEntry> logs, string? agentPrompt)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(agentPrompt))
            {
                sb.AppendLine(agentPrompt);
                sb.AppendLine();
            }

            sb.AppendLine($"[THREAD_SCOPE] {scope}");
            sb.AppendLine($"[THREAD_ID] {logs.First().ThreadId ?? 0}");
            sb.AppendLine("Analizza i log seguenti e fornisci una sintesi in italiano con eventuali errori critici e azioni consigliate.");
            sb.AppendLine();
            sb.AppendLine("=== LOGS ===");

            int index = 1;
            foreach (var entry in logs.Take(200))
            {
                var message = Truncate(entry.Message ?? string.Empty, 600);
                sb.AppendLine($"{index:000}. {entry.Timestamp:yyyy-MM-dd HH:mm:ss} | {entry.Level} | {entry.Category} | {message}");
                if (!string.IsNullOrWhiteSpace(entry.Exception))
                {
                    sb.AppendLine($"     EXCEPTION: {Truncate(entry.Exception!, 400)}");
                }
                index++;
            }

            if (logs.Count > 200)
            {
                sb.AppendLine($"[... ulteriori {logs.Count - 200} log troncati ...]");
            }

            sb.AppendLine("=== END LOGS ===");
            sb.AppendLine("Fornisci la tua analisi in testo continuo (nessun JSON).");

            return sb.ToString();
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
