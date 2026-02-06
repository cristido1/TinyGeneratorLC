using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Bridge between LangChain ChatModel and ReActLoop.
    /// Handles model communication via OpenAI-compatible API (works with Ollama, OpenAI, Azure).
    /// 
    /// This is a placeholder that prepares the infrastructure. Full integration requires:
    /// - HttpClient calls to OpenAI-compatible endpoints
    /// - Proper message serialization with tool_choice="auto"
    /// - Function calling response parsing
    /// </summary>
    
    public class LangChainChatBridge
    {
        private Uri _modelEndpoint;
        private string _modelId;
        private string _apiKey;
        private readonly HttpClient _httpClient;
        private readonly ICustomLogger? _logger;
        private bool? _forceOllama;
        private Func<CancellationToken, Task>? _beforeCallAsync;
        private Func<CancellationToken, Task>? _afterCallAsync;
        private bool _logRequestsAsLlama;
        private readonly IServiceProvider? _services;
        public double Temperature { get; set; } = 0.7;
        public double TopP { get; set; } = 1.0;
        public double? RepeatPenalty { get; set; }
        public int? TopK { get; set; }
        public int? RepeatLastN { get; set; }
        public int? NumPredict { get; set; }
        // If null, do not send any explicit max tokens parameter to the model.
        // This avoids forcing an unsafe default such as 8000. Set when required.
        public int? MaxResponseTokens { get; set; } = null;
        // OpenAI-style response_format (usato per forzare JSON schema)
        public object? ResponseFormat { get; set; }

        private readonly HashSet<string> _noTemperatureModels;
        private readonly HashSet<string> _noRepeatPenaltyModels;
        private readonly HashSet<string> _noTopPModels;
        private readonly HashSet<string> _noFrequencyPenaltyModels;
        private readonly HashSet<string> _noMaxTokensModels;
        private readonly HashSet<string> _noTopKModels;
        private readonly HashSet<string> _noRepeatLastNModels;
        private readonly HashSet<string> _noNumPredictModels;

        public LangChainChatBridge(
            string modelEndpoint,
            string modelId,
            string apiKey,
            HttpClient? httpClient = null,
            ICustomLogger? logger = null,
            bool? forceOllama = null,
            Func<CancellationToken, Task>? beforeCallAsync = null,
            Func<CancellationToken, Task>? afterCallAsync = null,
            bool logRequestsAsLlama = false,
            IEnumerable<string>? noTemperatureModels = null,
            IEnumerable<string>? noRepeatPenaltyModels = null,
            IEnumerable<string>? noTopPModels = null,
            IEnumerable<string>? noFrequencyPenaltyModels = null,
            IEnumerable<string>? noMaxTokensModels = null,
            IEnumerable<string>? noTopKModels = null,
            IEnumerable<string>? noRepeatLastNModels = null,
            IEnumerable<string>? noNumPredictModels = null,
            IServiceProvider? services = null)
        {
            // Normalize endpoint - don't add /v1 suffix, just use endpoint as-is
            _modelEndpoint = new Uri(modelEndpoint.TrimEnd('/'));
            _modelId = modelId;
            _apiKey = apiKey;
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _logger = logger;
            _forceOllama = forceOllama;
            _beforeCallAsync = beforeCallAsync;
            _afterCallAsync = afterCallAsync;
            _logRequestsAsLlama = logRequestsAsLlama;
            _services = services;
            _noTemperatureModels = noTemperatureModels != null ? new HashSet<string>(noTemperatureModels, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
            _noRepeatPenaltyModels = noRepeatPenaltyModels != null ? new HashSet<string>(noRepeatPenaltyModels, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
            _noTopPModels = noTopPModels != null ? new HashSet<string>(noTopPModels, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
            _noFrequencyPenaltyModels = noFrequencyPenaltyModels != null ? new HashSet<string>(noFrequencyPenaltyModels, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
            _noMaxTokensModels = noMaxTokensModels != null ? new HashSet<string>(noMaxTokensModels, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
            _noTopKModels = noTopKModels != null ? new HashSet<string>(noTopKModels, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
            _noRepeatLastNModels = noRepeatLastNModels != null ? new HashSet<string>(noRepeatLastNModels, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
            _noNumPredictModels = noNumPredictModels != null ? new HashSet<string>(noNumPredictModels, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        }

        /// <summary>
        /// Send chat request to model with tools and get response.
        /// For Ollama: sends with format=json when no tools, tools when available
        /// For OpenAI: sends with tool_choice="auto"
        /// </summary>
        public async Task<string> CallModelWithToolsAsync(
            List<ConversationMessage> messages,
            List<Dictionary<string, object>> tools,
            CancellationToken ct = default)
        {
            var options = TryGetResponseValidationOptions();
            if (options == null || !options.Enabled)
            {
                return await CallModelWithToolsOnceAsync(messages, tools, ct).ConfigureAwait(false);
            }

            var operationKey = NormalizeOperationKeyForResponseValidation(LogScope.Current ?? string.Empty);
            var agentRole = TryResolveAgentRole();
            if (ShouldSkipValidation(options, agentRole))
            {
                return await CallModelWithToolsOnceAsync(messages, tools, ct).ConfigureAwait(false);
            }

            var policy = TryGetPolicyForOperation(options, operationKey);
            var enableChecker = IsCheckerEnabledForOperation(options, operationKey);
            if (policy?.EnableChecker.HasValue == true)
            {
                enableChecker = policy.EnableChecker.Value;
            }

            var maxRetries = policy?.MaxRetries ?? options.MaxRetries;
            var askFailureReason = policy?.AskFailureReasonOnFinalFailure ?? options.AskFailureReasonOnFinalFailure;

            var rules = (IReadOnlyList<ResponseValidationRule>)(options.Rules ?? new List<ResponseValidationRule>());
            if (policy?.RuleIds != null && policy.RuleIds.Count > 0)
            {
                var allowed = new HashSet<int>(policy.RuleIds);
                rules = rules.Where(r => allowed.Contains(r.Id)).ToList();
            }

            var lastValidationError = "";
            string? lastResponseJson = null;
            long? lastPrimaryResponseLogId = null;

            for (int attempt = 0; attempt <= Math.Max(0, maxRetries); attempt++)
            {
                ct.ThrowIfCancellationRequested();

                lastResponseJson = await CallModelWithToolsOnceAsync(messages, tools, ct).ConfigureAwait(false);

                // Ensure the primary model response log row is persisted before we attempt to
                // capture its Id and before invoking response_checker (which produces its own log rows).
                try
                {
                    if (_logger != null)
                    {
                        await _logger.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch
                {
                    // best-effort: continue even if flush fails
                }

                lastPrimaryResponseLogId = TryGetLatestPrimaryModelResponseLogId();
                var validation = await ValidateResponseJsonAsync(
                    messages,
                    tools,
                    lastResponseJson,
                    options,
                    enableChecker,
                    rules,
                    lastPrimaryResponseLogId,
                    ct).ConfigureAwait(false);

                var responseValidation = BuildResponseValidation(lastPrimaryResponseLogId, validation);
                ApplyResponseValidation(responseValidation);

                if (validation.IsValid)
                {
                    // Track primary model success (best-effort)
                    try
                    {
                        if (_services != null && !string.IsNullOrWhiteSpace(agentRole))
                        {
                            using var scope = _services.CreateScope();
                            var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
                            var db = scope.ServiceProvider.GetService<DatabaseService>();
                            if (fallbackService != null && db != null)
                            {
                                var m = db.ListModels().FirstOrDefault(x => string.Equals(x.Name, _modelId, StringComparison.OrdinalIgnoreCase));
                                if (m != null)
                                {
                                    fallbackService.RecordPrimaryModelUsage(agentRole!, Convert.ToInt32(m.Id), success: true);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // ignore tracking errors
                    }

                    return lastResponseJson;
                }

                lastValidationError = string.IsNullOrWhiteSpace(validation.Reason) ? "Validation failed" : validation.Reason;

                if (!validation.NeedsRetry)
                {
                    // Track primary model failure (best-effort)
                    try
                    {
                        if (_services != null && !string.IsNullOrWhiteSpace(agentRole))
                        {
                            using var scope = _services.CreateScope();
                            var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
                            var db = scope.ServiceProvider.GetService<DatabaseService>();
                            if (fallbackService != null && db != null)
                            {
                                var m = db.ListModels().FirstOrDefault(x => string.Equals(x.Name, _modelId, StringComparison.OrdinalIgnoreCase));
                                if (m != null)
                                {
                                    fallbackService.RecordPrimaryModelUsage(agentRole!, Convert.ToInt32(m.Id), success: false);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // ignore tracking errors
                    }

                    return lastResponseJson;
                }

                if (attempt < Math.Max(0, maxRetries))
                {
                    InjectValidationFeedback(messages, validation, attempt + 1);
                    continue;
                }

                // Retries exhausted: ask for diagnosis (best-effort)
                if (askFailureReason)
                {
                    await DiagnoseFailureAsync(messages, lastValidationError, ct).ConfigureAwait(false);
                }

                // Track primary model failure (best-effort)
                try
                {
                    if (_services != null && !string.IsNullOrWhiteSpace(agentRole))
                    {
                        using var scope = _services.CreateScope();
                        var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
                        var db = scope.ServiceProvider.GetService<DatabaseService>();
                        if (fallbackService != null && db != null)
                        {
                            var m = db.ListModels().FirstOrDefault(x => string.Equals(x.Name, _modelId, StringComparison.OrdinalIgnoreCase));
                            if (m != null)
                            {
                                fallbackService.RecordPrimaryModelUsage(agentRole!, Convert.ToInt32(m.Id), success: false);
                            }
                        }
                    }
                }
                catch
                {
                    // ignore tracking errors
                }

                // After diagnosis, try fallback models (if enabled + role known)
                if (options.EnableFallback && !string.IsNullOrWhiteSpace(agentRole))
                {
                    var fallbackResponse = await TryFallbackAsync(
                        agentRole!,
                        messages,
                        tools,
                        options,
                        enableChecker,
                        rules,
                        maxRetries,
                        ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(fallbackResponse))
                    {
                        return fallbackResponse;
                    }
                }

                // No fallback (or failed): return last response
                return lastResponseJson;
            }

            // Should be unreachable
            return lastResponseJson ?? string.Empty;
        }

        private static string NormalizeOperationKeyForResponseValidation(string operationKey)
        {
            if (string.IsNullOrWhiteSpace(operationKey)) return string.Empty;

            var scope = operationKey.Trim();

            // CommandPolicies are keyed by LogScope.Current.
            // For test commands we commonly use a threadScope like: tests/<group>/<model>
            // but we want per-command config keys like: test_<group> (e.g. test_base).
            if (scope.StartsWith("tests/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    return $"test_{SanitizePolicyIdentifier(parts[1])}";
                }
            }

            return scope;
        }

        private static string SanitizePolicyIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "test";
            var chars = value.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray();
            var sanitized = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(sanitized) ? "test" : sanitized;
        }

        private static int? TryGetEffectiveModelTrafficThreadId()
        {
            try
            {
                var opId = LogScope.CurrentOperationId;
                if (opId.HasValue && opId.Value > 0 && opId.Value <= int.MaxValue)
                {
                    return (int)opId.Value;
                }

                var threadId = LogScope.CurrentThreadId;
                if (threadId.HasValue && threadId.Value > 0)
                {
                    return threadId.Value;
                }

                var managed = Environment.CurrentManagedThreadId;
                return managed > 0 ? managed : null;
            }
            catch
            {
                return null;
            }
        }

        private long? TryGetLatestPrimaryModelResponseLogId()
        {
            return TryGetLatestPrimaryModelResponseLogId(_modelId);
        }

        private long? TryGetLatestPrimaryModelResponseLogId(string modelId)
        {
            try
            {
                if (_services == null) return null;

                var threadId = TryGetEffectiveModelTrafficThreadId();
                if (threadId is null || threadId.Value <= 0) return null;

                // Exclude internal checker agent by construction: this is called immediately after the primary model call,
                // before invoking response_checker (which produces its own ModelResponse rows).
                var db = _services.GetService<DatabaseService>();
                if (db == null) return null;

                var agentName = LogScope.CurrentAgentName;
                if (!string.IsNullOrWhiteSpace(agentName))
                {
                    return db.TryGetLatestModelResponseLogId(threadId.Value, agentName: agentName, modelName: modelId);
                }

                // Some commands do not set CurrentAgentName in scope: fall back to a thread+model lookup.
                return db.TryGetLatestModelResponseLogId(threadId.Value, agentName: null, modelName: modelId);
            }
            catch
            {
                return null;
            }
        }

        private static ResponseValidation BuildResponseValidation(long? logId, ValidationResult validation)
        {
            if (logId is null || logId.Value <= 0)
            {
                throw new InvalidOperationException("ResponseValidation requires a persisted response log id. Ensure response logging is enabled and logs are flushed before validation.");
            }

            var errors = new List<string>();
            if (!validation.IsValid)
            {
                if (!string.IsNullOrWhiteSpace(validation.Reason))
                {
                    errors.Add(validation.Reason.Trim());
                }

                if (validation.ViolatedRules != null && validation.ViolatedRules.Count > 0)
                {
                    errors.Add($"Regole violate: {string.Join(", ", validation.ViolatedRules)}");
                }
            }

            if (!validation.IsValid && errors.Count == 0)
            {
                errors.Add("Validation failed");
            }

            return new ResponseValidation(logId.Value, validation.IsValid, errors);
        }

        private void ApplyResponseValidation(ResponseValidation validation)
        {
            if (_services == null)
            {
                throw new InvalidOperationException("ResponseValidation requires IServiceProvider to update logs.");
            }

            var db = _services.GetService<DatabaseService>();
            if (db == null)
            {
                throw new InvalidOperationException("ResponseValidation requires DatabaseService to update logs.");
            }

            var result = validation.Successed ? "SUCCESS" : "FAILED";
            var failReason = validation.Successed
                ? null
                : (validation.ErrorMessages.Count > 0 ? string.Join(" | ", validation.ErrorMessages) : "Validation failed");

            db.UpdateModelResponseResultById(validation.LogId, result, failReason, examined: true);
        }

        private ResponseValidationOptions? TryGetResponseValidationOptions()
        {
            try
            {
                return _services?.GetService<IOptions<ResponseValidationOptions>>()?.Value;
            }
            catch
            {
                return null;
            }
        }

        private static bool ShouldSkipValidation(ResponseValidationOptions options, string? agentRole)
        {
            if (options.SkipRoles == null || options.SkipRoles.Count == 0) return false;
            if (string.IsNullOrWhiteSpace(agentRole)) return false;
            return options.SkipRoles.Any(r => string.Equals(r?.Trim(), agentRole.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsCheckerEnabledForOperation(ResponseValidationOptions options, string operationKey)
        {
            if (!string.IsNullOrWhiteSpace(operationKey) && options.CommandPolicies != null &&
                options.CommandPolicies.TryGetValue(operationKey, out var policy) &&
                policy != null && policy.EnableChecker.HasValue)
            {
                return policy.EnableChecker.Value;
            }

            return options.EnableCheckerByDefault;
        }

        private static ResponseValidationCommandPolicy? TryGetPolicyForOperation(ResponseValidationOptions options, string operationKey)
        {
            if (string.IsNullOrWhiteSpace(operationKey)) return null;
            if (options.CommandPolicies == null) return null;
            return options.CommandPolicies.TryGetValue(operationKey, out var policy) ? policy : null;
        }

        private string? TryResolveAgentRole()
        {
            var role = LogScope.CurrentAgentRole;
            if (!string.IsNullOrWhiteSpace(role)) return role;

            // Heuristic for internal checker scopes
            var scope = LogScope.Current ?? string.Empty;
            if (scope.Contains("response_checker", StringComparison.OrdinalIgnoreCase)) return "response_checker";

            var agentName = LogScope.CurrentAgentName;
            if (string.Equals(agentName, "Response Checker", StringComparison.OrdinalIgnoreCase)) return "response_checker";

            try
            {
                var db = _services?.GetService<DatabaseService>();
                if (db == null || string.IsNullOrWhiteSpace(agentName)) return null;
                var agent = db.ListAgents().FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name) &&
                    string.Equals(a.Name.Trim(), agentName.Trim(), StringComparison.OrdinalIgnoreCase));
                var agentRole = agent?.Role;
                return string.IsNullOrWhiteSpace(agentRole) ? null : agentRole.Trim();
            }
            catch
            {
                return null;
            }
        }

        private async Task<ValidationResult> ValidateResponseJsonAsync(
            List<ConversationMessage> messages,
            List<Dictionary<string, object>> tools,
            string responseJson,
            ResponseValidationOptions options,
            bool enableChecker,
            IReadOnlyList<ResponseValidationRule> rules,
            long? primaryResponseLogId,
            CancellationToken ct)
        {
            _ = primaryResponseLogId;
            var parsed = ParseChatResponseWithFinishReason(responseJson);
            var hasToolCalls = parsed.ToolCalls != null && parsed.ToolCalls.Count > 0;
            var textContent = parsed.TextContent;

            var scope = LogScope.Current ?? string.Empty;
            var isAddVoiceTags = IsAddVoiceTagsScope(scope);
            var requiredDialogueLineIds = isAddVoiceTags
                ? ExtractRequestedDialogueLineIdsForAddVoiceTags(messages)
                : new HashSet<int>();

            // Deterministic: accept tool calls as a valid model step
            if (hasToolCalls)
            {
                return new ValidationResult { IsValid = true, NeedsRetry = false, Reason = "Tool calls present" };
            }

            // Deterministic: reject empty content
            if (string.IsNullOrWhiteSpace(textContent))
            {
                // For add_voice_tags_to_story, an empty mapping is allowed when there are no dialogue lines to tag.
                if (isAddVoiceTags && requiredDialogueLineIds.Count == 0)
                {
                    return new ValidationResult
                    {
                        IsValid = true,
                        NeedsRetry = false,
                        Reason = "No dialogue lines requested; empty mapping allowed"
                    };
                }

                return new ValidationResult
                {
                    IsValid = false,
                    NeedsRetry = true,
                    Reason = "Risposta vuota o non parsabile"
                };
            }

            // Deterministic, operation-specific checks for add_voice_tags_to_story:
            // 1) format must be parseable as ID -> tags mapping
            // 2) all requested dialogue IDs must be tagged (PERSONAGGIO + EMOZIONE)
            if (isAddVoiceTags && requiredDialogueLineIds.Count > 0)
            {
                var idToTags = FormatterV2.ParseIdToTagsMapping(textContent);
                if (idToTags.Count == 0)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        NeedsRetry = true,
                        Reason = "Formato mapping non riconosciuto (atteso: ID + [PERSONAGGIO: ...] + [EMOZIONE: ...])",
                        SystemMessageOverride = "VALIDAZIONE FALLITA: il formato della risposta non è corretto. Restituisci SOLO righe nel formato: 123 [PERSONAGGIO: Nome] [EMOZIONE: emozione]."
                    };
                }

                var missing = requiredDialogueLineIds.Where(id => !idToTags.ContainsKey(id)).OrderBy(id => id).ToList();
                if (missing.Count > 0)
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        NeedsRetry = true,
                        Reason = $"Mancano tag per {missing.Count} righe richieste: {string.Join(", ", missing.Select(i => i.ToString("000")))}",
                        SystemMessageOverride = $"VALIDAZIONE FALLITA: devi taggare TUTTE le righe richieste. Mancano: {string.Join(", ", missing.Select(i => i.ToString("000")))}. Rispondi SOLO con il mapping per ogni ID richiesto, nel formato: 123 [PERSONAGGIO: Nome] [EMOZIONE: emozione]."
                    };
                }

                foreach (var id in requiredDialogueLineIds)
                {
                    var tags = idToTags[id] ?? string.Empty;
                    if (tags.IndexOf("[PERSONAGGIO:", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return new ValidationResult
                        {
                            IsValid = false,
                            NeedsRetry = true,
                            Reason = $"ID {id:000} senza tag PERSONAGGIO",
                            SystemMessageOverride = $"VALIDAZIONE FALLITA: per l'ID {id:000} manca [PERSONAGGIO: ...]. Rispondi SOLO con righe nel formato: 123 [PERSONAGGIO: Nome] [EMOZIONE: emozione]."
                        };
                    }

                    if (tags.IndexOf("[EMOZIONE:", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return new ValidationResult
                        {
                            IsValid = false,
                            NeedsRetry = true,
                            Reason = $"ID {id:000} senza tag EMOZIONE",
                            SystemMessageOverride = $"VALIDAZIONE FALLITA: per l'ID {id:000} manca [EMOZIONE: ...]. Rispondi SOLO con righe nel formato: 123 [PERSONAGGIO: Nome] [EMOZIONE: emozione]."
                        };
                    }
                }
            }

            if (!enableChecker)
            {
                _logger?.Log("Information", "ResponseValidation", $"Checker disabled for scope '{LogScope.Current ?? ""}' (role={TryResolveAgentRole() ?? "(unknown)"})");
                return new ValidationResult { IsValid = true, NeedsRetry = false, Reason = "Deterministic checks passed" };
            }

            try
            {
                var checker = _services?.GetService<ResponseCheckerService>();
                if (checker == null)
                {
                    return new ValidationResult { IsValid = true, NeedsRetry = false, Reason = "No checker service; skipping" };
                }

                var instruction = ExtractInstructionFromMessages(messages);

                _logger?.Log(
                    "Information",
                    "ResponseValidation",
                    $"Invoking response_checker (scope='{LogScope.Current ?? ""}', rules={rules.Count}, agentRole={TryResolveAgentRole() ?? "(unknown)"}, model={_modelId})");

                var result = await checker.ValidateGenericResponseAsync(
                    instruction,
                    textContent,
                    rules,
                    agentName: LogScope.CurrentAgentName,
                    modelName: _modelId,
                    ct: ct).ConfigureAwait(false);

                _logger?.Log(
                    "Information",
                    "ResponseValidation",
                    $"response_checker result: isValid={result.IsValid} needsRetry={result.NeedsRetry} violatedRules={(result.ViolatedRules != null && result.ViolatedRules.Count > 0 ? string.Join(",", result.ViolatedRules) : "")}; reason={result.Reason}");

                // If invalid, prefer system injection to avoid contaminating user prompt
                if (!result.IsValid && string.IsNullOrWhiteSpace(result.SystemMessageOverride))
                {
                    var violated = result.ViolatedRules != null && result.ViolatedRules.Count > 0
                        ? string.Join(", ", result.ViolatedRules.Select(r => $"REGOLA {r}"))
                        : "(regole non specificate)";
                    result.SystemMessageOverride = $"VALIDAZIONE FALLITA: {result.Reason}. Regole violate: {violated}. Correggi la risposta rispettando le REGOLE.";
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.Log("Warning", "ResponseValidation", $"Checker validation failed: {ex.Message}");
                // Best-effort: do not block the pipeline if checker fails
                return new ValidationResult { IsValid = true, NeedsRetry = false, Reason = "Checker error; skipping" };
            }
        }

        private static bool IsAddVoiceTagsScope(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope)) return false;
            return string.Equals(scope.Trim(), "story/add_voice_tags_to_story", StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<int> ExtractRequestedDialogueLineIdsForAddVoiceTags(List<ConversationMessage> messages)
        {
            var set = new HashSet<int>();
            try
            {
                if (messages == null || messages.Count == 0) return set;
                var lastUser = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content))?.Content;
                if (string.IsNullOrWhiteSpace(lastUser)) return set;

                var content = lastUser;
                var startMarker = "RIGHE DI DIALOGO";
                var endMarker = "TESTO COMPLETO";

                var start = content.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return set;
                var end = content.IndexOf(endMarker, start, StringComparison.OrdinalIgnoreCase);
                if (end < 0) return set;

                var section = content.Substring(start, end - start);
                section = section.Replace("\r\n", "\n").Replace('\r', '\n');
                foreach (var rawLine in section.Split('\n'))
                {
                    var line = (rawLine ?? string.Empty).TrimStart();
                    if (line.Length == 0) continue;

                    int i = 0;
                    while (i < line.Length && char.IsDigit(line[i])) i++;
                    if (i == 0) continue;
                    if (!int.TryParse(line.Substring(0, i), out var id)) continue;
                    if (id <= 0) continue;
                    set.Add(id);
                }

                return set;
            }
            catch
            {
                return set;
            }
        }

        private static string ExtractInstructionFromMessages(List<ConversationMessage> messages)
        {
            if (messages == null || messages.Count == 0) return string.Empty;

            // For validation we need the instruction (system prompt) + the user input context,
            // otherwise the checker can't assess format/constraints.
            var systemParts = messages
                .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Content)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!.Trim())
                .ToList();

            var lastUser = messages
                .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Content))
                ?.Content;

            if (systemParts.Count == 0)
            {
                return lastUser?.Trim() ?? string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== SYSTEM INSTRUCTIONS ===");
            sb.AppendLine(string.Join("\n\n", systemParts));

            if (!string.IsNullOrWhiteSpace(lastUser))
            {
                sb.AppendLine();
                sb.AppendLine("=== USER INPUT ===");
                sb.AppendLine(lastUser.Trim());
            }

            return sb.ToString().Trim();
        }

        private static void InjectValidationFeedback(List<ConversationMessage> messages, ValidationResult validation, int attemptNumber)
        {
            if (!string.IsNullOrWhiteSpace(validation.SystemMessageOverride))
            {
                messages.Add(new ConversationMessage { Role = "system", Content = validation.SystemMessageOverride });
                return;
            }

            var msg = $"Tentativo {attemptNumber}: la risposta non rispetta i vincoli. Motivo: {validation.Reason}. Correggi e riprova.";
            messages.Add(new ConversationMessage { Role = "system", Content = msg });
        }

        private async Task DiagnoseFailureAsync(List<ConversationMessage> messages, string lastValidationError, CancellationToken ct)
        {
            try
            {
                var diagMessages = new List<ConversationMessage>();
                if (messages != null && messages.Count > 0)
                {
                    diagMessages.AddRange(messages.Take(Math.Min(messages.Count, 12)));
                }
                diagMessages.Add(new ConversationMessage
                {
                    Role = "system",
                    Content = "Spiega in 3-6 righe perché la tua risposta precedente ha fallito la validazione e cosa farai per correggerla. Non riscrivere la risposta finale."
                });
                diagMessages.Add(new ConversationMessage
                {
                    Role = "user",
                    Content = $"Errore di validazione: {lastValidationError}"
                });

                var diagJson = await CallModelWithToolsOnceAsync(diagMessages, new List<Dictionary<string, object>>(), ct).ConfigureAwait(false);
                var (diagText, _) = ParseChatResponse(diagJson);
                if (!string.IsNullOrWhiteSpace(diagText))
                {
                    _logger?.Log("Information", "ResponseValidation", $"Self-diagnosis: {diagText.Trim()}");
                }
            }
            catch (Exception ex)
            {
                _logger?.Log("Warning", "ResponseValidation", $"Self-diagnosis failed: {ex.Message}");
            }
        }

        private async Task<string?> TryFallbackAsync(
            string agentRole,
            List<ConversationMessage> messages,
            List<Dictionary<string, object>> tools,
            ResponseValidationOptions options,
            bool enableChecker,
            IReadOnlyList<ResponseValidationRule> rules,
            int maxRetries,
            CancellationToken ct)
        {
            if (_services == null) return null;

            // ModelFallbackService is scoped: resolve in a scope
            using var scope = _services.CreateScope();
            var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
            var kernelFactory = scope.ServiceProvider.GetService<ILangChainKernelFactory>();
            var db = scope.ServiceProvider.GetService<DatabaseService>();
            if (fallbackService == null || kernelFactory == null || db == null) return null;

            int? primaryModelPk = null;
            try
            {
                var m = db.ListModels().FirstOrDefault(x => string.Equals(x.Name, _modelId, StringComparison.OrdinalIgnoreCase));
                if (m != null)
                {
                    primaryModelPk = Convert.ToInt32(m.Id);
                }
            }
            catch
            {
                primaryModelPk = null;
            }

            var fallbackTuple = await fallbackService.ExecuteWithFallbackAsync<string>(
                agentRole,
                primaryModelPk,
                async mr =>
                {
                    if (mr.Model == null || string.IsNullOrWhiteSpace(mr.Model.Name))
                        throw new InvalidOperationException("Fallback ModelRole has no Model");

                    var candidateBridge = kernelFactory.CreateChatBridge(mr.Model.Name);
                    // preserve sampling settings
                    candidateBridge.Temperature = Temperature;
                    candidateBridge.TopP = TopP;
                    candidateBridge.RepeatPenalty = RepeatPenalty;
                    candidateBridge.TopK = TopK;
                    candidateBridge.RepeatLastN = RepeatLastN;
                    candidateBridge.NumPredict = NumPredict;
                    candidateBridge.MaxResponseTokens = MaxResponseTokens;

                    // Each fallback model must have the same number of attempts as the primary model.
                    // IMPORTANT: do not mutate the shared messages list across different fallback models.
                    static List<ConversationMessage> CloneMessages(List<ConversationMessage> src)
                    {
                        var copy = new List<ConversationMessage>(src?.Count ?? 0);
                        if (src == null) return copy;

                        foreach (var m in src)
                        {
                            copy.Add(new ConversationMessage
                            {
                                Role = m.Role,
                                Content = m.Content,
                                ToolCalls = m.ToolCalls,
                                ToolCallId = m.ToolCallId,
                            });
                        }

                        return copy;
                    }

                    var attemptMessages = CloneMessages(messages);
                    ValidationResult? lastValidation = null;
                    string? lastResponseJson = null;

                    for (int attempt = 0; attempt <= Math.Max(0, maxRetries); attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        lastResponseJson = await candidateBridge.CallModelWithToolsOnceAsync(attemptMessages, tools, ct).ConfigureAwait(false);

                        try
                        {
                            if (_logger != null)
                            {
                                await _logger.FlushAsync().ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            // best-effort: continue even if flush fails
                        }

                        var fallbackLogId = TryGetLatestPrimaryModelResponseLogId(candidateBridge._modelId);

                        lastValidation = await ValidateResponseJsonAsync(
                            attemptMessages,
                            tools,
                            lastResponseJson,
                            options,
                            enableChecker,
                            rules,
                            primaryResponseLogId: fallbackLogId,
                            ct).ConfigureAwait(false);

                        var responseValidation = BuildResponseValidation(fallbackLogId, lastValidation);
                        ApplyResponseValidation(responseValidation);

                        if (lastValidation.IsValid)
                        {
                            return lastResponseJson;
                        }

                        if (!lastValidation.NeedsRetry)
                        {
                            break;
                        }

                        if (attempt < Math.Max(0, maxRetries))
                        {
                            InjectValidationFeedback(attemptMessages, lastValidation, attempt + 1);
                        }
                    }

                    var reason = lastValidation?.Reason;
                    throw new InvalidOperationException($"Fallback produced invalid response after retries: {reason}");
                }).ConfigureAwait(false);

            var fallbackResult = fallbackTuple.result;
            var successfulModelRole = fallbackTuple.successfulModelRole;

            if (string.IsNullOrWhiteSpace(fallbackResult) || successfulModelRole?.Model == null)
            {
                return null;
            }

            try
            {
                // Adopt fallback model for subsequent calls (continue with same agent)
                var adopted = kernelFactory.CreateChatBridge(successfulModelRole.Model.Name);
                AdoptBridgeSettingsFrom(adopted);
                _logger?.Log("Information", "ResponseValidation", $"Adopted fallback model '{successfulModelRole.Model.Name}' for role '{agentRole}'.");
            }
            catch (Exception ex)
            {
                _logger?.Log("Warning", "ResponseValidation", $"Failed to adopt fallback model: {ex.Message}");
            }

            return fallbackResult;
        }

        private void AdoptBridgeSettingsFrom(LangChainChatBridge other)
        {
            _modelEndpoint = other._modelEndpoint;
            _modelId = other._modelId;
            _apiKey = other._apiKey;
            _forceOllama = other._forceOllama;
            _beforeCallAsync = other._beforeCallAsync;
            _afterCallAsync = other._afterCallAsync;
            _logRequestsAsLlama = other._logRequestsAsLlama;
        }

        /// <summary>
        /// One-shot model call (no centralized validation/retry/fallback). Used internally.
        /// </summary>
        private async Task<string> CallModelWithToolsOnceAsync(
            List<ConversationMessage> messages,
            List<Dictionary<string, object>> tools,
            CancellationToken ct = default)
        {
            // Capture threadId at the start of the call for consistent logging
            int? currentThreadId = TryGetEffectiveModelTrafficThreadId();

            if (_logger != null)
            {
                await _logger.ModelRequestStartedAsync(_modelId).ConfigureAwait(false);
            }

            // Prepare lightweight aggregation for llama.cpp logs: we will emit a
            // single concise SUCCESS message on success or a detailed error body
            // (request + response + exception) only when something goes wrong.
            StringBuilder? llamaLog = null;
            string? requestJsonForLlama = null;
            string? responseContentForLlama = null;
            int? responseStatusForLlama = null;
            bool encounteredError = false;
            if (_logRequestsAsLlama && _logger != null)
            {
                llamaLog = new StringBuilder();
            }

            string? requestUrlForLlama = null;
            try
            {
                if (_beforeCallAsync != null)
                {
                    if (llamaLog != null) llamaLog.AppendLine($"Pre-call: Running pre-call hook for {_modelId}");
                    await _beforeCallAsync(ct).ConfigureAwait(false);
                }

                var isOllama = _forceOllama ?? _modelEndpoint.ToString().Contains("11434", StringComparison.OrdinalIgnoreCase);
                
                // For Ollama, create request with format or tools
                // For OpenAI, include tools with tool_choice="auto"
                object request;
                string fullUrl;

                if (isOllama)
                {
                    _logger?.Log("Info", "LangChainBridge", $"Using Ollama endpoint for {_modelId} with {tools.Count} tools");
                    
                    var requestBody = new Dictionary<string, object>
                    {
                        { "model", _modelId },
                        { "messages", messages.Select(m => new { role = m.Role, content = m.Content }).ToList() },
                        { "stream", false },
                        { "temperature", Temperature },
                        { "top_p", TopP }
                    };
                    if (!_noRepeatPenaltyModels.Contains(_modelId) && RepeatPenalty.HasValue) requestBody["repeat_penalty"] = RepeatPenalty.Value;
                    if (!_noTopKModels.Contains(_modelId) && TopK.HasValue) requestBody["top_k"] = TopK.Value;
                    if (!_noRepeatLastNModels.Contains(_modelId) && RepeatLastN.HasValue) requestBody["repeat_last_n"] = RepeatLastN.Value;
                    if (!_noNumPredictModels.Contains(_modelId) && NumPredict.HasValue) requestBody["num_predict"] = NumPredict.Value;

                    if (ResponseFormat != null)
                    {
                        requestBody["format"] = ResponseFormat;
                    }

                    // If we have tools, pass them
                    if (tools.Any())
                    {
                        requestBody["tools"] = tools;
                    }
                    // Note: No longer forcing JSON format when no tools are present
                    // This allows natural text responses in chat mode

                    request = requestBody;
                    fullUrl = new Uri(_modelEndpoint, "/api/chat").ToString();
                    requestUrlForLlama = fullUrl;
                }
                else
                {
                    _logger?.Log("Info", "LangChainBridge", $"Using OpenAI-compatible endpoint for {_modelId}");
                    
                    // Determine if model uses new parameter name (o1, gpt-4o series)
                    bool usesNewTokenParam = _modelId.Contains("o1", StringComparison.OrdinalIgnoreCase) ||
                                             _modelId.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase) ||
                                             _modelId.Contains("gpt-5", StringComparison.OrdinalIgnoreCase);
                    
                    // Serialize messages properly for OpenAI format
                    var serializedMessages = messages.Select(m =>
                    {
                        var msgDict = new Dictionary<string, object>
                        {
                            { "role", m.Role },
                            { "content", m.Content ?? string.Empty }
                        };
                        
                        // Add tool_calls for assistant messages that have them
                        if (m.Role == "assistant" && m.ToolCalls != null && m.ToolCalls.Any())
                        {
                            msgDict["tool_calls"] = m.ToolCalls.Select(tc => new Dictionary<string, object>
                            {
                                { "id", tc.Id },
                                { "type", "function" },
                                { "function", new Dictionary<string, object>
                                    {
                                        { "name", tc.ToolName },
                                        { "arguments", tc.Arguments }
                                    }
                                }
                            }).ToList();
                        }
                        
                        // Add tool_call_id for tool messages
                        if (m.Role == "tool" && !string.IsNullOrEmpty(m.ToolCallId))
                        {
                            msgDict["tool_call_id"] = m.ToolCallId;
                        }
                        
                        return msgDict;
                    }).ToList();
                    
                    var requestDict = new Dictionary<string, object>
                    {
                        { "model", _modelId },
                        { "messages", serializedMessages },
                        { "tools", tools }
                    };
                    if (!_noTemperatureModels.Contains(_modelId))
                        requestDict["temperature"] = Temperature;
                    if (!_noTopPModels.Contains(_modelId))
                        requestDict["top_p"] = TopP;
                    if (!_noRepeatPenaltyModels.Contains(_modelId) && RepeatPenalty.HasValue)
                        requestDict["repeat_penalty"] = RepeatPenalty.Value;
                    if (!_noTopKModels.Contains(_modelId) && TopK.HasValue) requestDict["top_k"] = TopK.Value;
                    if (!_noRepeatLastNModels.Contains(_modelId) && RepeatLastN.HasValue) requestDict["repeat_last_n"] = RepeatLastN.Value;
                    if (!_noNumPredictModels.Contains(_modelId) && NumPredict.HasValue) requestDict["num_predict"] = NumPredict.Value;
                    if (!_noFrequencyPenaltyModels.Contains(_modelId))
                        requestDict["frequency_penalty"] = 0.0; // Replace with actual value if used
                    if (ResponseFormat != null)
                        requestDict["response_format"] = ResponseFormat;
                    if (tools.Any())
                        requestDict["tools"] = tools;
                    // Add correct token limit parameter based on model
                    if (MaxResponseTokens.HasValue)
                    {
                        if (!_noMaxTokensModels.Contains(_modelId))
                        {
                            if (usesNewTokenParam)
                                requestDict["max_completion_tokens"] = MaxResponseTokens.Value;
                            else
                                requestDict["max_tokens"] = MaxResponseTokens.Value;
                        }
                    }
                    
                    request = requestDict;
                    fullUrl = new Uri(_modelEndpoint, "/v1/chat/completions").ToString();
                    requestUrlForLlama = fullUrl;
                }

                // Diagnostic: log which model-specific exclusions are active for this model id
                    try
                    {
                        _logger?.Log("Debug", "LangChainBridge",
                            $"Model={_modelId} ExcludeFlags: noTemperature={_noTemperatureModels.Contains(_modelId)}, noTopP={_noTopPModels.Contains(_modelId)}, noTopK={_noTopKModels.Contains(_modelId)}, noRepeatPenalty={_noRepeatPenaltyModels.Contains(_modelId)}, noFrequencyPenalty={_noFrequencyPenaltyModels.Contains(_modelId)}, noMaxTokens={_noMaxTokensModels.Contains(_modelId)}, noRepeatLastN={_noRepeatLastNModels.Contains(_modelId)}, noNumPredict={_noNumPredictModels.Contains(_modelId)}");
                    }
                    catch { }

                var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = false });
                
                var jsonContent = new StringContent(
                    requestJson,
                    System.Text.Encoding.UTF8,
                    "application/json");

                    _logger?.Log("Info", "LangChainBridge", $"Calling model {_modelId} at {fullUrl}");
                    _logger?.LogRequestJson(_modelId, requestJson, currentThreadId, LogScope.CurrentAgentName);
                    // store request for possible error diagnostics only; do not attach it to
                    // the log unless an error occurs
                    requestJsonForLlama = requestJson;

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, fullUrl)
                {
                    Content = jsonContent
                };

                // Add auth header if not Ollama (Ollama doesn't require it)
                if (!isOllama && !_apiKey.Contains("ollama", StringComparison.OrdinalIgnoreCase))
                {
                    httpRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");
                }

                var response = await _httpClient.SendAsync(httpRequest, ct);
                
                var responseContent = await response.Content.ReadAsStringAsync(ct);
                
                _logger?.Log("Info", "LangChainBridge", $"Model responded (status={response.StatusCode})");
                _logger?.LogResponseJson(_modelId, responseContent, currentThreadId, LogScope.CurrentAgentName);
                // capture response for possible error diagnostics only
                responseContentForLlama = responseContent;
                responseStatusForLlama = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    encounteredError = true;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.Log("Error", "LangChainBridge", 
                        $"Model request failed with status {response.StatusCode}: {responseContent}");

                    try
                    {
                        if (_logger != null)
                        {
                            await _logger.FlushAsync().ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // best-effort
                    }

                    var failureValidation = new ValidationResult
                    {
                        IsValid = false,
                        NeedsRetry = false,
                        Reason = $"HTTP {(int)response.StatusCode}: {responseContent}"
                    };
                    var failureLogId = TryGetLatestPrimaryModelResponseLogId();
                    var responseValidation = BuildResponseValidation(failureLogId, failureValidation);
                    ApplyResponseValidation(responseValidation);
                    
                    // Check if the error indicates the model doesn't support tools
                    if (responseContent.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ModelNoToolsSupportException(
                            _modelId,
                            $"Model request failed with status {response.StatusCode}: {responseContent}");
                    }
                    
                    throw new HttpRequestException(
                        $"Model request failed with status {response.StatusCode}: {responseContent}");
                }

                return responseContent;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainBridge", $"Model call failed: {ex.Message}", ex.ToString());
                throw;
            }
            finally
            {
                if (_afterCallAsync != null)
                {
                    try
                    {
                        await _afterCallAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // treat post-call failures as errors to surface in llama.cpp logs
                        encounteredError = true;
                        if (llamaLog != null)
                        {
                            llamaLog.AppendLine($"Post-call hook failed: {ex.Message}");
                        }
                        _logger?.Log("Warning", "LangChainBridge", $"Post-call hook failed: {ex.Message}");
                    }
                }

                // Emit a single aggregated llama.cpp log entry: concise success or detailed error
                try
                {
                    if (llamaLog != null)
                    {
                        if (encounteredError)
                        {
                            // Provide request + response for diagnostics when error occurred
                            var sb = llamaLog;
                            sb.AppendLine($"Model: {_modelId}");
                            if (!string.IsNullOrEmpty(requestJsonForLlama))
                                sb.AppendLine($"Request -> {requestUrlForLlama}\n{requestJsonForLlama}");
                            if (!string.IsNullOrEmpty(responseContentForLlama))
                                sb.AppendLine($"Response <- {requestUrlForLlama} (status={responseStatusForLlama})\n{responseContentForLlama}");
                            _logger?.Log("Information", "llama.cpp", sb.ToString());
                        }
                        else
                        {
                            // Only log a short success message to reduce noise
                            _logger?.Log("Information", "llama.cpp", $"Model call succeeded: {_modelId} (status={responseStatusForLlama})");
                        }
                    }
                }
                catch { }

                if (_logger != null)
                {
                    await _logger.ModelRequestFinishedAsync(_modelId).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Parse chat completion response.
        /// Handles both OpenAI format (with choices/tool_calls) and Ollama format (with message).
        /// Step 1: Try structured deserialization via ApiResponse
        /// Step 2: Fallback to manual parsing if deserialization fails
        /// </summary>
        public static (string? textContent, List<ToolCallFromModel> toolCalls) ParseChatResponse(string? jsonResponse)
        {
            var parsed = ParseChatResponseWithFinishReason(jsonResponse);
            return (parsed.TextContent, parsed.ToolCalls);
        }

        public sealed class ParsedChatResponse
        {
            public string? TextContent { get; set; }
            public List<ToolCallFromModel> ToolCalls { get; set; } = new();
            public string? FinishReason { get; set; }
        }

        /// <summary>
        /// Parse chat completion response including finish reason.
        /// finishReason is typically "stop" or "length" (OpenAI-compatible), or done_reason for Ollama.
        /// </summary>
        public static ParsedChatResponse ParseChatResponseWithFinishReason(string? jsonResponse)
        {
            var toolCalls = new List<ToolCallFromModel>();
            string? textContent = null;
            string? finishReason = null;

            if (string.IsNullOrWhiteSpace(jsonResponse))
            {
                return new ParsedChatResponse
                {
                    TextContent = textContent,
                    ToolCalls = toolCalls,
                    FinishReason = finishReason
                };
            }

            // Step 1: Try structured deserialization to ApiResponse
            try
            {
                var apiResponse = JsonSerializer.Deserialize<ApiResponse>(jsonResponse);
                
                if (apiResponse?.Message != null)
                {
                    textContent = apiResponse.Message.Content;
                    finishReason = apiResponse.DoneReason;

                    // Extract tool_calls from structured response
                    if (apiResponse.Message.ToolCalls != null && apiResponse.Message.ToolCalls.Count > 0)
                    {
                        foreach (var tc in apiResponse.Message.ToolCalls)
                        {
                            if (tc.Function == null) continue;
                            
                            var argsJson = "{}";
                            if (tc.Function.Arguments != null)
                            {
                                // Arguments can be object or already serialized string
                                if (tc.Function.Arguments is JsonElement jsonElem)
                                {
                                    argsJson = jsonElem.GetRawText();
                                }
                                else if (tc.Function.Arguments is string str)
                                {
                                    argsJson = str;
                                }
                                else
                                {
                                    argsJson = JsonSerializer.Serialize(tc.Function.Arguments);
                                }
                            }

                            toolCalls.Add(new ToolCallFromModel
                            {
                                Id = tc.Id ?? Guid.NewGuid().ToString(),
                                ToolName = tc.Function.Name ?? "unknown",
                                Arguments = argsJson
                            });
                        }
                    }

                    // Success with ApiResponse deserialization
                    return new ParsedChatResponse
                    {
                        TextContent = textContent,
                        ToolCalls = toolCalls,
                        FinishReason = finishReason
                    };
                }
            }
            catch (JsonException)
            {
                // ApiResponse deserialization failed, proceed to fallback parsing
            }

            // Step 2: Fallback manual parsing
            try
            {
                var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;

                // Try OpenAI format first (has "choices" array)
                if (root.TryGetProperty("choices", out var choicesElement))
                {
                    var choices = choicesElement.EnumerateArray();

                    foreach (var choice in choices)
                    {
                        if (string.IsNullOrWhiteSpace(finishReason)
                            && choice.TryGetProperty("finish_reason", out var finish)
                            && finish.ValueKind == JsonValueKind.String)
                        {
                            finishReason = finish.GetString();
                        }

                        if (choice.TryGetProperty("message", out var message))
                        {
                            // Extract text content
                            if (message.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                            {
                                textContent = content.GetString();
                            }

                            // Extract tool calls
                            if (message.TryGetProperty("tool_calls", out var calls))
                            {
                                foreach (var call in calls.EnumerateArray())
                                {
                                    if (call.TryGetProperty("function", out var func))
                                    {
                                        toolCalls.Add(new ToolCallFromModel
                                        {
                                            Id = (call.TryGetProperty("id", out var id) ? id.GetString() : null) ?? Guid.NewGuid().ToString(),
                                            ToolName = (func.TryGetProperty("name", out var name) ? name.GetString() : null) ?? "unknown",
                                            Arguments = (func.TryGetProperty("arguments", out var args) ? args.GetString() : null) ?? "{}"
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
                // Try Ollama format (has "message" object directly)
                else if (root.TryGetProperty("message", out var ollamaMessage))
                {
                    // Some servers may include done_reason at root
                    if (root.TryGetProperty("done_reason", out var doneReason) && doneReason.ValueKind == JsonValueKind.String)
                    {
                        finishReason = doneReason.GetString();
                    }

                    if (ollamaMessage.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
                    {
                        textContent = content.GetString();

                        // Some models return tool_calls serialized as JSON inside content; try to parse them
                        if (!string.IsNullOrWhiteSpace(textContent) && toolCalls.Count == 0)
                        {
                            TryParseEmbeddedToolCalls(textContent, toolCalls);
                        }
                    }

                    // Extract tool calls from Ollama format
                    if (ollamaMessage.TryGetProperty("tool_calls", out var ollamaCalls))
                    {
                        foreach (var call in ollamaCalls.EnumerateArray())
                        {
                            if (call.TryGetProperty("function", out var func))
                            {
                                var argsJson = "{}";
                                if (func.TryGetProperty("arguments", out var argsElement))
                                {
                                    // Arguments can be JSON object or string
                                    if (argsElement.ValueKind == JsonValueKind.Object)
                                    {
                                        argsJson = argsElement.GetRawText();
                                    }
                                    else if (argsElement.ValueKind == JsonValueKind.String)
                                    {
                                        argsJson = argsElement.GetString() ?? "{}";
                                    }
                                }

                                toolCalls.Add(new ToolCallFromModel
                                {
                                    Id = (call.TryGetProperty("id", out var id) ? id.GetString() : null) ?? Guid.NewGuid().ToString(),
                                    ToolName = (func.TryGetProperty("name", out var name) ? name.GetString() : null) ?? "unknown",
                                    Arguments = argsJson
                                });
                            }
                        }
                    }
                }
                // Try simple "response" format (some models return just { "response": "text" })
                else if (root.TryGetProperty("response", out var responseElement))
                {
                    textContent = responseElement.GetString();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse chat response: {ex.Message}");
            }

            return new ParsedChatResponse
            {
                TextContent = textContent,
                ToolCalls = toolCalls,
                FinishReason = finishReason
            };
        }

        private static void TryParseEmbeddedToolCalls(string content, List<ToolCallFromModel> toolCalls)
        {
            ParseToolCallsFromString(content, toolCalls);
        }

        private static void ParseToolCallsFromString(string? content, List<ToolCallFromModel> toolCalls)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            var trimmed = content.Trim();
            if (!(trimmed.StartsWith("{") || trimmed.StartsWith("["))) return;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                ParseToolCallsFromElement(doc.RootElement, toolCalls);
            }
            catch
            {
                // ignore parse errors
            }
        }

        private static void ParseToolCallsFromElement(JsonElement root, List<ToolCallFromModel> toolCalls)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("function", out var func)) continue;

                        var argsJson = "{}";
                        if (func.TryGetProperty("arguments", out var argsElement))
                        {
                            if (argsElement.ValueKind == JsonValueKind.Object)
                                argsJson = argsElement.GetRawText();
                            else if (argsElement.ValueKind == JsonValueKind.String)
                                argsJson = argsElement.GetString() ?? "{}";
                        }

                        var toolName = (func.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null) ?? "unknown";
                        var toolId = (call.TryGetProperty("id", out var idProp) ? idProp.GetString() : null) ?? Guid.NewGuid().ToString();

                        if (toolCalls.Any(tc => tc.Id == toolId && tc.Arguments == argsJson))
                            continue;

                        toolCalls.Add(new ToolCallFromModel
                        {
                            Id = toolId,
                            ToolName = toolName,
                            Arguments = argsJson
                        });
                    }
                }

                if (root.TryGetProperty("content", out var contentProp))
                {
                    if (contentProp.ValueKind == JsonValueKind.String)
                    {
                        ParseToolCallsFromString(contentProp.GetString(), toolCalls);
                    }
                    else if (contentProp.ValueKind == JsonValueKind.Object || contentProp.ValueKind == JsonValueKind.Array)
                    {
                        ParseToolCallsFromElement(contentProp, toolCalls);
                    }
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    ParseToolCallsFromElement(item, toolCalls);
                }
            }
        }
    }

    public class ToolCallFromModel
    {
        public string Id { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string Arguments { get; set; } = "{}";
    }

    /// <summary>
    /// Full orchestrator: ReAct loop + LangChain chat bridge.
    /// This is the main entry point for story generation with proper function calling.
    /// </summary>
    public class LangChainStoryOrchestrator
    {
        private readonly LangChainChatBridge _modelBridge;
        private readonly HybridLangChainOrchestrator _tools;
        private readonly ReActLoopOrchestrator _reactLoop;
        private readonly ICustomLogger? _logger;

        public LangChainStoryOrchestrator(
            string modelEndpoint,
            string modelId,
            string apiKey,
            HybridLangChainOrchestrator tools,
            HttpClient? httpClient = null,
            ICustomLogger? logger = null,
            int? maxTokens = null)
        {
            _logger = logger;
            _tools = tools;
            // Read OpenAI model exclusion lists from appsettings.json to ensure consistent behaviour
            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .Build();

                var noTempModels = config.GetSection("OpenAI:NoTemperatureModels").Get<string[]>() ?? Array.Empty<string>();
                var noRepeatPenaltyModels = config.GetSection("OpenAI:NoRepeatPenaltyModels").Get<string[]>() ?? Array.Empty<string>();
                var noTopPModels = config.GetSection("OpenAI:NoTopPModels").Get<string[]>() ?? Array.Empty<string>();
                var noFrequencyPenaltyModels = config.GetSection("OpenAI:NoFrequencyPenaltyModels").Get<string[]>() ?? Array.Empty<string>();
                var noMaxTokensModels = config.GetSection("OpenAI:NoMaxTokensModels").Get<string[]>() ?? Array.Empty<string>();
                var noTopKModels = config.GetSection("OpenAI:NoTopKModels").Get<string[]>() ?? Array.Empty<string>();
                var noRepeatLastNModels = config.GetSection("OpenAI:NoRepeatLastNModels").Get<string[]>() ?? Array.Empty<string>();
                var noNumPredictModels = config.GetSection("OpenAI:NoNumPredictModels").Get<string[]>() ?? Array.Empty<string>();

                _modelBridge = new LangChainChatBridge(modelEndpoint, modelId, apiKey, httpClient, logger, null, null, null, false, noTempModels, noRepeatPenaltyModels, noTopPModels, noFrequencyPenaltyModels, noMaxTokensModels, noTopKModels, noRepeatLastNModels, noNumPredictModels);
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "StoryOrchestrator", $"Failed to initialize LangChainChatBridge with OpenAI exclusion lists: {ex.Message}", ex.ToString());
                throw;
            }
            if (maxTokens.HasValue && maxTokens.Value > 0)
            {
                if (!_modelBridge.MaxResponseTokens.HasValue)
                {
                    _modelBridge.MaxResponseTokens = maxTokens.Value;
                }
                else
                {
                    _modelBridge.MaxResponseTokens = Math.Max(_modelBridge.MaxResponseTokens.Value, maxTokens.Value);
                }
            }
            _reactLoop = new ReActLoopOrchestrator(tools, logger);
        }

        /// <summary>
        /// Execute story generation with full ReAct loop and tool use.
        /// </summary>
        public async Task<string> GenerateStoryAsync(
            string theme,
            string systemPrompt = "You are a creative story writer. Use available tools to enhance and structure the story.",
            CancellationToken ct = default)
        {
            try
            {
                _logger?.Log("Info", "StoryOrchestrator", $"Starting story generation for theme: {theme}");

                _reactLoop.ClearHistory();

                // Prepare messages with system prompt
                var messages = new List<ConversationMessage>
                {
                    new ConversationMessage { Role = "system", Content = systemPrompt },
                    new ConversationMessage { Role = "user", Content = $"Generate a story about: {theme}" }
                };

                // Get tool schemas
                var toolSchemas = _tools.GetToolSchemas();

                // TODO: Implement actual ReAct loop with model integration
                // For now, this is a placeholder structure
                var story = await GenerateWithModelIntegrationAsync(messages, toolSchemas, ct);

                return story;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "StoryOrchestrator", $"Story generation failed: {ex.Message}", ex.ToString());
                return $"Error: {ex.Message}";
            }
        }

        private async Task<string> GenerateWithModelIntegrationAsync(
            List<ConversationMessage> messages,
            List<Dictionary<string, object>> toolSchemas,
            CancellationToken ct)
        {
            // This method would:
            // 1. Call model with messages + tool definitions
            // 2. Parse response for tool calls
            // 3. Execute tools
            // 4. Loop until model returns final story
            
            // For now, return a placeholder
            return await Task.FromResult("Story generation with LangChain integration pending full model bridge implementation");
        }
    }
}
