using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
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
        private bool _isVllm;
        private Func<CancellationToken, Task>? _beforeCallAsync;
        private Func<CancellationToken, Task>? _afterCallAsync;
        private bool _logRequestsAsLlama;
        private readonly IServiceProvider? _services;
        private readonly AsyncLocal<long?> _currentPrimaryResponseLogId = new();
        public double Temperature { get; set; } = 0.7;
        public double TopP { get; set; } = 1.0;
        public double? RepeatPenalty { get; set; }
        public int? TopK { get; set; }
        public int? RepeatLastN { get; set; }
        public int? NumPredict { get; set; }
        public int? NumCtx { get; set; }
        public bool? Think { get; set; }
        // If null, do not send any explicit max tokens parameter to the model.
        // This avoids forcing an unsafe default such as 8000. Set when required.
        public int? MaxResponseTokens { get; set; } = null;
        // OpenAI-style response_format (usato per forzare JSON schema)
        public object? ResponseFormat { get; set; }
        public bool EnableStreaming { get; set; } = false;
        public Func<string, Task>? StreamChunkCallbackAsync { get; set; }

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
            bool isVllm = false,
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
            _isVllm = isVllm;
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
            CancellationToken ct = default,
            bool skipResponseChecker = false,
            bool skipResponseValidation = false)
        {
            // Generate unique request ID for tracking this call in logs
            var requestId = RequestIdGenerator.Generate();
            _logger?.Log("Info", "LangChainBridge", $"[ReqID: {requestId}] --> Chiamata {_modelId} (responseValidation: {!skipResponseValidation}, checker: {!skipResponseChecker})");

            if (skipResponseValidation)
            {
                var raw = await CallModelWithToolsOnceAsync(messages, tools, ct, requestId).ConfigureAwait(false);
                _logger?.Log("Info", "LangChainBridge", $"[ReqID: {requestId}] <-- Response OK (response validation skipped)");
                return raw;
            }

            var options = TryGetResponseValidationOptions();
            if (options == null || !options.Enabled)
            {
                var result = await CallModelWithToolsOnceAsync(messages, tools, ct, requestId).ConfigureAwait(false);
                _logger?.Log("Info", "LangChainBridge", $"[ReqID: {requestId}] <-- Response OK (no validation)");
                return result;
            }

            var operationKey = NormalizeOperationKeyForResponseValidation(LogScope.Current ?? string.Empty);
            var agentRole = TryResolveAgentRole();
            if (ShouldSkipValidation(options, agentRole))
            {
                var result = await CallModelWithToolsOnceAsync(messages, tools, ct, requestId).ConfigureAwait(false);
                _logger?.Log("Info", "LangChainBridge", $"[ReqID: {requestId}] <-- Response OK (validation skipped for role: {agentRole})");
                return result;
            }

            var policy = TryGetPolicyForOperation(options, operationKey);
            var enableChecker = IsCheckerEnabledForOperation(options, operationKey);
            if (policy?.EnableChecker.HasValue == true)
            {
                enableChecker = policy.EnableChecker.Value;
            }
            if (skipResponseChecker)
            {
                enableChecker = false;
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

                try
                {
                    lastResponseJson = await CallModelWithToolsOnceAsync(messages, tools, ct, requestId).ConfigureAwait(false);
                    AppendAssistantResponseToHistory(messages, lastResponseJson);
                }
                catch (OperationCanceledException oce) when (!ct.IsCancellationRequested && options.EnableFallback && !string.IsNullOrWhiteSpace(agentRole))
                {
                    _logger?.Log("Warning", "ResponseValidation",
                        $"Primary model '{_modelId}' cancelled/timed out for role '{agentRole}'. Trying fallback models. Details: {oce.Message}");

                    if (askFailureReason)
                    {
                        try
                        {
                            await DiagnoseFailureAsync(messages, $"Primary model timeout/cancelled: {oce.Message}", CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                            // best-effort
                        }
                    }

                    var fallbackResponse = await TryFallbackAsync(
                        agentRole!,
                        messages,
                        tools,
                        options,
                        enableChecker,
                        rules,
                        maxRetries,
                        CancellationToken.None).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(fallbackResponse))
                    {
                        return fallbackResponse;
                    }

                    throw;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { throw; }

                lastPrimaryResponseLogId = _currentPrimaryResponseLogId.Value ?? TryGetLatestPrimaryModelResponseLogId();
                var validation = await ValidateResponseJsonAsync(
                    messages,
                    tools,
                    lastResponseJson,
                    options,
                    enableChecker,
                    rules,
                    lastPrimaryResponseLogId,
                    ct).ConfigureAwait(false);

                TryApplyResponseValidation(lastPrimaryResponseLogId, validation, $"req:{requestId}");

                if (validation.IsValid)
                {
                    _logger?.Log("Info", "LangChainBridge", $"[ReqID: {requestId}] ✅ Validation OK");
                    return lastResponseJson;
                }

                lastValidationError = string.IsNullOrWhiteSpace(validation.Reason) ? "Validation failed" : validation.Reason;

                if (!validation.NeedsRetry)
                {
                    _logger?.Log("Warning", "LangChainBridge", $"[ReqID: {requestId}] ⚠️ Validation failed (no retry): {lastValidationError}");
                    _logger?.Log("Warning", "LangChainBridge", $"[ReqID: {requestId}] ⚠️ Validation failed (no retry): {lastValidationError}");
                    return lastResponseJson;
                }

                if (attempt < Math.Max(0, maxRetries))
                {
                    _logger?.Log("Warning", "LangChainBridge", $"[ReqID: {requestId}] 🔄 Validation failed, retry {attempt + 1}/{maxRetries}: {lastValidationError}");
                    InjectValidationFeedback(messages, validation, attempt + 1);
                    continue;
                }

                // Retries exhausted: ask for diagnosis (best-effort)
                if (askFailureReason)
                {
                    _logger?.Log("Warning", "LangChainBridge", $"[ReqID: {requestId}] ❌ Retries exhausted, requesting diagnosis...");
                    await DiagnoseFailureAsync(messages, lastValidationError, ct).ConfigureAwait(false);
                }
                // After diagnosis, try fallback models (if enabled + role known)
                if (options.EnableFallback && !string.IsNullOrWhiteSpace(agentRole))
                {
                    var fallbackRequestId = RequestIdGenerator.Generate();
                    _logger?.Log("Warning", "LangChainBridge", $"[ReqID: {fallbackRequestId}] 🔄 Tentativo fallback per request {requestId} (role: {agentRole})");
                    
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
                        _logger?.Log("Info", "LangChainBridge", $"[ReqID: {fallbackRequestId}] ✅ Fallback completato per request {requestId}");
                        return fallbackResponse;
                    }
                }

                // No fallback (or failed): return last response
                _logger?.Log("Warning", "LangChainBridge", $"[ReqID: {requestId}] ⚠️ Returning last response (validation failed, no fallback)");
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
                var storyId = LogScope.CurrentStoryId;

                // Exclude internal checker agent by construction: this is called immediately after the primary model call,
                // before invoking response_checker (which produces its own ModelResponse rows).
                var db = _services.GetService<DatabaseService>();
                if (db == null) return null;

                var agentName = LogScope.CurrentAgentName;
                if (!string.IsNullOrWhiteSpace(agentName))
                {
                    return db.TryGetLatestModelResponseLogId(
                        threadId.Value,
                        agentName: agentName,
                        modelName: modelId,
                        storyId: storyId);
                }

                // Some commands do not set CurrentAgentName in scope: fall back to a thread+model lookup.
                return db.TryGetLatestModelResponseLogId(
                    threadId.Value,
                    agentName: null,
                    modelName: modelId,
                    storyId: storyId);
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

        private void TryApplyResponseValidation(long? logId, ValidationResult validation, string context)
        {
            if (logId is null || logId.Value <= 0)
            {
                _logger?.Log("Warning", "ResponseValidation", $"Skipped persistence: missing response log id (context={context})");
                return;
            }

            try
            {
                var responseValidation = BuildResponseValidation(logId, validation);
                ApplyResponseValidation(responseValidation);
            }
            catch (Exception ex)
            {
                _logger?.Log("Warning", "ResponseValidation", $"Failed to persist response validation (context={context}): {ex.Message}");
            }
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
            if (string.IsNullOrWhiteSpace(agentRole)) return false;

            var role = agentRole.Trim();
            if (string.Equals(role, "response_checker", StringComparison.OrdinalIgnoreCase) &&
                !options.EnableSelfValidationForResponseChecker)
            {
                return true;
            }

            if (options.SkipRoles == null || options.SkipRoles.Count == 0) return false;
            return options.SkipRoles.Any(r => string.Equals(r?.Trim(), role, StringComparison.OrdinalIgnoreCase));
        }

        private long? TryPersistPrimaryModelResponseLogNow(string modelId, string responseJson, int? threadId, string? agentName)
        {
            try
            {
                if (_services == null)
                {
                    return null;
                }

                var db = _services.GetService<DatabaseService>();
                if (db == null)
                {
                    return null;
                }

                var effectiveThreadId = threadId ?? LogScope.CurrentThreadId ?? Environment.CurrentManagedThreadId;
                var effectiveAgentName = !string.IsNullOrWhiteSpace(agentName)
                    ? agentName!.Trim()
                    : LogScope.CurrentAgentName;
                var scope = (LogScope.Current ?? "unknown").Trim();
                var storyId = LogScope.CurrentStoryId.HasValue
                    && LogScope.CurrentStoryId.Value > 0
                    && LogScope.CurrentStoryId.Value <= int.MaxValue
                    ? (int?)LogScope.CurrentStoryId.Value
                    : null;
                var usageTokens = TryExtractUsageTokens(responseJson);

                var (textContent, _) = ParseChatResponse(responseJson);
                var chatText = string.IsNullOrWhiteSpace(textContent) ? responseJson : textContent!;
                var entry = new LogEntry
                {
                    Ts = DateTime.UtcNow.ToString("o"),
                    Level = "Information",
                    Category = "ModelResponse",
                    Message = $"[{modelId}] RESPONSE_JSON: {responseJson}",
                    Exception = null,
                    State = null,
                    ThreadId = effectiveThreadId,
                    StoryId = storyId,
                    ThreadScope = scope,
                    AgentName = effectiveAgentName,
                    ModelName = modelId,
                    Context = null,
                    Analized = false,
                    ChatText = chatText,
                    Result = "SUCCESS",
                    ResultFailReason = null,
                    Examined = false,
                    DurationSecs = 1,
                    Tokens = usageTokens.OutputTokens ?? usageTokens.TotalTokens
                };

                var logId = db.InsertLogEntryImmediate(entry);
                if (usageTokens.PromptTokens.HasValue && usageTokens.PromptTokens.Value >= 0)
                {
                    db.UpdateLatestModelRequestTokensForAgent(
                        effectiveThreadId,
                        effectiveAgentName,
                        modelId,
                        usageTokens.PromptTokens.Value,
                        storyId);
                }

                return logId;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsCheckerEnabledForOperation(ResponseValidationOptions options, string operationKey)
        {
            if (!string.IsNullOrWhiteSpace(operationKey) && options.CommandPolicies != null)
            {
                foreach (var key in CommandOperationNameResolver.GetLookupKeys(operationKey))
                {
                    if (options.CommandPolicies.TryGetValue(key, out var policy) &&
                        policy != null &&
                        policy.EnableChecker.HasValue)
                    {
                        return policy.EnableChecker.Value;
                    }
                }
            }

            return options.EnableCheckerByDefault;
        }

        private static ResponseValidationCommandPolicy? TryGetPolicyForOperation(ResponseValidationOptions options, string operationKey)
        {
            if (string.IsNullOrWhiteSpace(operationKey)) return null;
            if (options.CommandPolicies == null) return null;

            foreach (var key in CommandOperationNameResolver.GetLookupKeys(operationKey))
            {
                if (options.CommandPolicies.TryGetValue(key, out var policy) && policy != null)
                {
                    return policy;
                }
            }

            return null;
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
                var agent = db.ListAgents().FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Description) &&
                    string.Equals(a.Description.Trim(), agentName.Trim(), StringComparison.OrdinalIgnoreCase));
                var agentRole = agent?.Role;
                return string.IsNullOrWhiteSpace(agentRole) ? null : agentRole.Trim();
            }
            catch
            {
                return null;
            }
        }

        private sealed record ProviderTimingStats(
            long PromptTokens,
            long OutputTokens,
            long PromptTimeNs,
            long GenTimeNs,
            long LoadTimeNs,
            long TotalTimeNs)
        {
            public bool HasAnyValue =>
                PromptTokens > 0 ||
                OutputTokens > 0 ||
                PromptTimeNs > 0 ||
                GenTimeNs > 0 ||
                LoadTimeNs > 0 ||
                TotalTimeNs > 0;
        }

        private void TryRecordModelRolePerformance(string? responseJson)
        {
            try
            {
                if (_services == null || string.IsNullOrWhiteSpace(responseJson)) return;

                var roleCode = TryResolveAgentRole();
                if (string.IsNullOrWhiteSpace(roleCode)) return;

                var stats = TryParseProviderTimingStats(responseJson!);
                if (stats == null || !stats.HasAnyValue) return;

                using var scope = _services.CreateScope();
                var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
                var db = scope.ServiceProvider.GetService<DatabaseService>();
                if (fallbackService == null || db == null) return;

                var model = db.ListModels()
                    .FirstOrDefault(x => string.Equals(x.Name, _modelId, StringComparison.OrdinalIgnoreCase));
                if (model?.Id is not int modelId) return;

                fallbackService.RecordPrimaryModelPerformance(
                    roleCode!,
                    modelId,
                    new ModelFallbackService.ModelRolePerformanceDelta(
                        stats.PromptTokens,
                        stats.OutputTokens,
                        stats.PromptTimeNs,
                        stats.GenTimeNs,
                        stats.LoadTimeNs,
                        stats.TotalTimeNs));
            }
            catch
            {
                // best-effort metric collection
            }
        }

        private static ProviderTimingStats? TryParseProviderTimingStats(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson)) return null;

            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                // Primary source: Ollama timing/count fields at root level.
                var promptTokens = TryGetLong(root, "prompt_eval_count");
                var outputTokens = TryGetLong(root, "eval_count");
                var promptTimeNs = TryGetLong(root, "prompt_eval_duration");
                var genTimeNs = TryGetLong(root, "eval_duration");
                var loadTimeNs = TryGetLong(root, "load_duration");
                var totalTimeNs = TryGetLong(root, "total_duration");

                // Optional fallback for providers exposing usage only (no timings).
                if (outputTokens == 0 &&
                    TryGetPropertyIgnoreCase(root, "usage", out var usage) &&
                    usage.ValueKind == JsonValueKind.Object)
                {
                    promptTokens = promptTokens == 0 ? TryGetLong(usage, "prompt_tokens") : promptTokens;
                    outputTokens = TryGetLong(usage, "completion_tokens");
                    if (outputTokens == 0)
                    {
                        outputTokens = TryGetLong(usage, "output_tokens");
                    }
                }

                var stats = new ProviderTimingStats(
                    PromptTokens: promptTokens,
                    OutputTokens: outputTokens,
                    PromptTimeNs: promptTimeNs,
                    GenTimeNs: genTimeNs,
                    LoadTimeNs: loadTimeNs,
                    TotalTimeNs: totalTimeNs);

                return stats.HasAnyValue ? stats : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty(propertyName, out value))
                {
                    return true;
                }

                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static long TryGetLong(JsonElement root, string propertyName)
        {
            if (!TryGetPropertyIgnoreCase(root, propertyName, out var value))
            {
                return 0;
            }

            try
            {
                return value.ValueKind switch
                {
                    JsonValueKind.Number => value.TryGetInt64(out var n) ? n : 0,
                    JsonValueKind.String => long.TryParse(value.GetString(), out var s) ? s : 0,
                    _ => 0
                };
            }
            catch
            {
                return 0;
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
                var start = content.LastIndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return set;

                var end = content.IndexOf(endMarker, start, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    end = content.Length;
                }

                if (end <= start) return set;

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
                messages.Add(new ConversationMessage { Role = "user", Content = validation.SystemMessageOverride });
                return;
            }

            var msg = $"Tentativo {attemptNumber}: la risposta non rispetta i vincoli. Motivo: {validation.Reason}. Correggi e riprova.";
            messages.Add(new ConversationMessage { Role = "user", Content = msg });
        }

        private static void AppendAssistantResponseToHistory(List<ConversationMessage> messages, string? responseJson)
        {
            if (messages == null)
            {
                return;
            }

            var parsed = ParseChatResponseWithFinishReason(responseJson);
            var assistantContent = parsed.TextContent ?? string.Empty;
            messages.Add(new ConversationMessage
            {
                Role = "assistant",
                Content = assistantContent
            });
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
                    Role = "user",
                    Content =
                        "ISTRUZIONI DIAGNOSTICHE:\n" +
                        "Spiega in 3-6 righe perché la tua risposta precedente ha fallito la validazione e cosa farai per correggerla. " +
                        "Non riscrivere la risposta finale.\n\n" +
                        $"Errore di validazione: {lastValidationError}"
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
                    candidateBridge.Think = mr.Thinking ?? Think;
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
                        AppendAssistantResponseToHistory(attemptMessages, lastResponseJson);

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

                        TryApplyResponseValidation(fallbackLogId, lastValidation, $"fallback:{candidateBridge._modelId}:attempt:{attempt}");

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
            _isVllm = other._isVllm;
            _beforeCallAsync = other._beforeCallAsync;
            _afterCallAsync = other._afterCallAsync;
            _logRequestsAsLlama = other._logRequestsAsLlama;
            Think = other.Think;
        }

        /// <summary>
        /// One-shot model call (no centralized validation/retry/fallback). Used internally.
        /// </summary>
        private async Task<string> CallModelWithToolsOnceAsync(
            List<ConversationMessage> messages,
            List<Dictionary<string, object>> tools,
            CancellationToken ct = default,
            string? requestId = null)
        {
            // Generate request ID if not provided
            requestId ??= RequestIdGenerator.Generate();

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
                var workingMessages = PrepareMessagesForSend(messages, tools, requestId);
                
                // For Ollama, create request with format or tools
                // For OpenAI, include tools with tool_choice="auto"
                object request;
                string fullUrl;

                if (isOllama)
                {
                    _logger?.Log("Debug", "LangChainBridge", $"[ReqID: {requestId}] Routing a Ollama ({tools.Count} tools)");
                    var ollamaStreaming = EnableStreaming && StreamChunkCallbackAsync != null;
                    
                    var requestBody = new Dictionary<string, object>
                    {
                        { "model", _modelId },
                        { "messages", workingMessages.Select(m => new { role = m.Role, content = m.Content }).ToList() },
                        { "stream", ollamaStreaming }
                    };

                    var serializedMessages = SerializeConversationMessages(workingMessages);
                    var options = new Dictionary<string, object>
                    {
                        { "temperature", Temperature },
                        { "top_p", TopP },
                        { "num_keep", 0 },
                        { "mirostat", 0 },
                        { "stop", Array.Empty<string>() }
                    };

                    if (!_noRepeatPenaltyModels.Contains(_modelId) && RepeatPenalty.HasValue) options["repeat_penalty"] = RepeatPenalty.Value;
                    if (!_noTopKModels.Contains(_modelId) && TopK.HasValue) options["top_k"] = TopK.Value;
                    if (!_noRepeatLastNModels.Contains(_modelId) && RepeatLastN.HasValue) options["repeat_last_n"] = RepeatLastN.Value;
                    if (!_noNumPredictModels.Contains(_modelId))
                    {
                        var numPredict = NumPredict ?? -2;
                        if (numPredict > 0)
                        {
                            numPredict = ClampTokensToKnownContextBudget(
                                numPredict,
                                serializedMessages,
                                tools,
                                ResponseFormat != null);
                        }
                        options["num_predict"] = numPredict;
                    }
                    if (NumCtx.HasValue && NumCtx.Value > 0) options["num_ctx"] = NumCtx.Value;

                    requestBody["options"] = options;
                    if (Think.HasValue)
                    {
                        requestBody["think"] = Think.Value;
                    }

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
                    _logger?.Log("Debug", "LangChainBridge", $"[ReqID: {requestId}] Routing a OpenAI-compatible endpoint");
                    
                    // Determine if model uses new parameter name (o1, gpt-4o series)
                    bool usesNewTokenParam = _modelId.Contains("o1", StringComparison.OrdinalIgnoreCase) ||
                                             _modelId.Contains("gpt-4o", StringComparison.OrdinalIgnoreCase) ||
                                             _modelId.Contains("gpt-5", StringComparison.OrdinalIgnoreCase);
                    
                    // Serialize messages properly for OpenAI format
                    var serializedMessages = SerializeConversationMessages(workingMessages);
                    
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
                    if (_isVllm && Think.HasValue)
                    {
                        requestDict["chat_template_kwargs"] = new Dictionary<string, object>
                        {
                            ["enable_thinking"] = Think.Value
                        };
                    }
                    if (ResponseFormat != null)
                        requestDict["response_format"] = NormalizeResponseFormatForProvider(ResponseFormat);
                    if (tools.Any())
                        requestDict["tools"] = tools;
                    // Add correct token limit parameter based on model
                    if (MaxResponseTokens.HasValue)
                    {
                        if (!_noMaxTokensModels.Contains(_modelId))
                        {
                            var effectiveMaxTokens = MaxResponseTokens.Value;
                            if (_isVllm)
                            {
                                effectiveMaxTokens = ClampVllmMaxTokens(
                                    effectiveMaxTokens,
                                    serializedMessages,
                                    tools,
                                    ResponseFormat != null);
                            }
                            else if (NumCtx.HasValue && NumCtx.Value > 0)
                            {
                                effectiveMaxTokens = ClampTokensToKnownContextBudget(
                                    effectiveMaxTokens,
                                    serializedMessages,
                                    tools,
                                    ResponseFormat != null);
                            }

                            if (usesNewTokenParam)
                                requestDict["max_completion_tokens"] = effectiveMaxTokens;
                            else
                                requestDict["max_tokens"] = effectiveMaxTokens;
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
                var useOllamaStreamingResponse = isOllama && EnableStreaming && StreamChunkCallbackAsync != null;
                string responseContent;
                if (useOllamaStreamingResponse && response.IsSuccessStatusCode)
                {
                    responseContent = await ReadOllamaStreamedResponseAsync(response, ct).ConfigureAwait(false);
                }
                else
                {
                    responseContent = await response.Content.ReadAsStringAsync(ct);
                }

                _logger?.Log("Info", "LangChainBridge", $"Model responded (status={response.StatusCode})");
                var persistedLogId = TryPersistPrimaryModelResponseLogNow(_modelId, responseContent, currentThreadId, LogScope.CurrentAgentName);
                _currentPrimaryResponseLogId.Value = persistedLogId;
                if (!persistedLogId.HasValue || persistedLogId.Value <= 0)
                {
                    _logger?.LogResponseJson(_modelId, responseContent, currentThreadId, LogScope.CurrentAgentName);
                }
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
                    TryApplyResponseValidation(failureLogId, failureValidation, $"http-failure:req:{requestId}");
                    
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

                TryRecordModelRolePerformance(responseContent);
                
                // Count tags in response for logging
                var tagCount = System.Text.RegularExpressions.Regex.Matches(responseContent ?? "", @"\[([A-Z_]+):").Count;
                _logger?.Log("Info", "LangChainBridge", $"[ReqID: {requestId}] <-- Response OK ({responseContent?.Length ?? 0} char, {tagCount} tags)");
                
                return responseContent ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "LangChainBridge", $"[ReqID: {requestId}] ❌ Model call failed: {ex.Message}", ex.ToString());
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

        private async Task<string> ReadOllamaStreamedResponseAsync(HttpResponseMessage response, CancellationToken ct)
        {
            var contentBuilder = new StringBuilder();
            var streamChunks = 0;
            string? doneReason = null;
            long? totalDuration = null;
            long? loadDuration = null;
            long? promptEvalCount = null;
            long? promptEvalDuration = null;
            long? evalCount = null;
            long? evalDuration = null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("message", out var msg) &&
                        msg.ValueKind == JsonValueKind.Object &&
                        msg.TryGetProperty("content", out var contentEl))
                    {
                        var delta = contentEl.GetString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(delta))
                        {
                            streamChunks++;
                            contentBuilder.Append(delta);
                            if (StreamChunkCallbackAsync != null)
                            {
                                try
                                {
                                    await StreamChunkCallbackAsync(delta).ConfigureAwait(false);
                                }
                                catch
                                {
                                    // best-effort streaming callback
                                }
                            }
                        }
                    }

                    if (root.TryGetProperty("done_reason", out var doneReasonEl) && doneReasonEl.ValueKind == JsonValueKind.String)
                    {
                        doneReason = doneReasonEl.GetString();
                    }
                    if (root.TryGetProperty("total_duration", out var totalDurEl) && totalDurEl.ValueKind == JsonValueKind.Number && totalDurEl.TryGetInt64(out var td))
                    {
                        totalDuration = td;
                    }
                    if (root.TryGetProperty("load_duration", out var loadDurEl) && loadDurEl.ValueKind == JsonValueKind.Number && loadDurEl.TryGetInt64(out var ld))
                    {
                        loadDuration = ld;
                    }
                    if (root.TryGetProperty("prompt_eval_count", out var pecEl) && pecEl.ValueKind == JsonValueKind.Number && pecEl.TryGetInt64(out var pec))
                    {
                        promptEvalCount = pec;
                    }
                    if (root.TryGetProperty("prompt_eval_duration", out var pedEl) && pedEl.ValueKind == JsonValueKind.Number && pedEl.TryGetInt64(out var ped))
                    {
                        promptEvalDuration = ped;
                    }
                    if (root.TryGetProperty("eval_count", out var ecEl) && ecEl.ValueKind == JsonValueKind.Number && ecEl.TryGetInt64(out var ec))
                    {
                        evalCount = ec;
                    }
                    if (root.TryGetProperty("eval_duration", out var edEl) && edEl.ValueKind == JsonValueKind.Number && edEl.TryGetInt64(out var ed))
                    {
                        evalDuration = ed;
                    }
                }
                catch
                {
                    // Ignore malformed partial lines
                }
            }

            var payload = new Dictionary<string, object?>
            {
                ["message"] = new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = contentBuilder.ToString()
                },
                ["done"] = true
            };

            if (!string.IsNullOrWhiteSpace(doneReason)) payload["done_reason"] = doneReason;
            if (totalDuration.HasValue) payload["total_duration"] = totalDuration.Value;
            if (loadDuration.HasValue) payload["load_duration"] = loadDuration.Value;
            if (promptEvalCount.HasValue) payload["prompt_eval_count"] = promptEvalCount.Value;
            if (promptEvalDuration.HasValue) payload["prompt_eval_duration"] = promptEvalDuration.Value;
            if (evalCount.HasValue) payload["eval_count"] = evalCount.Value;
            if (evalDuration.HasValue) payload["eval_duration"] = evalDuration.Value;

            return JsonSerializer.Serialize(payload);
        }

        private static (int? PromptTokens, int? OutputTokens, int? TotalTokens) TryExtractUsageTokens(string? responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return (null, null, null);
            }

            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;
                static int? ReadInt(JsonElement parent, string name)
                {
                    if (!TryGetPropertyIgnoreCase(parent, name, out var value))
                    {
                        return null;
                    }

                    try
                    {
                        var parsed = value.ValueKind switch
                        {
                            JsonValueKind.Number when value.TryGetInt32(out var n32) => n32,
                            JsonValueKind.Number when value.TryGetInt64(out var n64) => (int)Math.Clamp(n64, 0, int.MaxValue),
                            JsonValueKind.String when int.TryParse(value.GetString(), out var s) => s,
                            _ => -1
                        };
                        return parsed >= 0 ? parsed : null;
                    }
                    catch
                    {
                        return null;
                    }
                }

                int? promptTokens = null;
                int? outputTokens = null;
                int? totalTokens = null;

                if (TryGetPropertyIgnoreCase(root, "usage", out var usage) &&
                    usage.ValueKind == JsonValueKind.Object)
                {
                    promptTokens = ReadInt(usage, "prompt_tokens");
                    outputTokens = ReadInt(usage, "completion_tokens");
                    outputTokens ??= ReadInt(usage, "output_tokens");
                    totalTokens = ReadInt(usage, "total_tokens");
                }

                promptTokens ??= ReadInt(root, "prompt_eval_count");
                outputTokens ??= ReadInt(root, "eval_count");
                totalTokens ??= (promptTokens.HasValue || outputTokens.HasValue)
                    ? (promptTokens ?? 0) + (outputTokens ?? 0)
                    : null;

                return (promptTokens, outputTokens, totalTokens);
            }
            catch
            {
                return (null, null, null);
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

        private List<ConversationMessage> PrepareMessagesForSend(
            IReadOnlyList<ConversationMessage> sourceMessages,
            IReadOnlyList<Dictionary<string, object>> tools,
            string? requestId)
        {
            var prepared = CloneConversationMessages(sourceMessages);
            if (!_isVllm || !NumCtx.HasValue || NumCtx.Value <= 0)
            {
                return prepared;
            }

            var hasStructuredResponseFormat = ResponseFormat != null;
            var requestedOutputTokens = Math.Max(32, MaxResponseTokens ?? 128);
            // Do not reserve the full configured max output for prompt-shrinking checks:
            // an oversized output budget can force unnecessary message reductions even when
            // the actual input is moderate (e.g. ~1k tokens).
            var promptSafetyOutputCap = NumCtx.HasValue && NumCtx.Value > 0
                ? Math.Max(128, (int)Math.Floor(NumCtx.Value * 0.35))
                : requestedOutputTokens;
            var expectedOutputTokens = Math.Min(requestedOutputTokens, promptSafetyOutputCap);

            if (IsWithinVllmSafetyMargin(prepared, tools, hasStructuredResponseFormat, expectedOutputTokens, out var initialEstimated, out var initialAllowed))
            {
                return prepared;
            }

            var removedThirdStep = false;
            var removedHistoricalErrors = false;
            var removedJsonExample = false;
            var reductionRound = 0;

            while (reductionRound < 12)
            {
                reductionRound++;
                var changed = false;

                if (!removedThirdStep)
                {
                    removedThirdStep = TryRemoveThirdConversationStep(prepared);
                    changed = changed || removedThirdStep;
                }

                if (!changed && !removedHistoricalErrors)
                {
                    removedHistoricalErrors = TryRemoveHistoricalErrorsFromSystem(prepared);
                    changed = changed || removedHistoricalErrors;
                }

                if (!changed && !removedJsonExample)
                {
                    removedJsonExample = TryRemoveSingleJsonExample(prepared);
                    changed = changed || removedJsonExample;
                }

                if (!changed)
                {
                    changed = TryShrinkLongestMessage(prepared);
                }

                if (!changed)
                {
                    break;
                }

                if (IsWithinVllmSafetyMargin(prepared, tools, hasStructuredResponseFormat, expectedOutputTokens, out var currentEstimated, out var currentAllowed))
                {
                    _logger?.Log(
                        "Warning",
                        "LangChainBridge",
                        $"[ReqID: {requestId}] vLLM prompt reduction applied: est_input {initialEstimated}->{currentEstimated}, allowed_input={currentAllowed}, num_ctx={NumCtx.Value}, expected_output={expectedOutputTokens}");
                    return prepared;
                }
            }

            if (IsWithinVllmSafetyMargin(prepared, tools, hasStructuredResponseFormat, expectedOutputTokens, out var finalEstimated, out var finalAllowed))
            {
                _logger?.Log(
                    "Warning",
                    "LangChainBridge",
                    $"[ReqID: {requestId}] vLLM prompt reduction applied: est_input {initialEstimated}->{finalEstimated}, allowed_input={finalAllowed}, num_ctx={NumCtx.Value}, expected_output={expectedOutputTokens}");
            }

            return prepared;
        }

        private bool IsWithinVllmSafetyMargin(
            IReadOnlyList<ConversationMessage> messages,
            IReadOnlyList<Dictionary<string, object>> tools,
            bool hasStructuredResponseFormat,
            int expectedOutputTokens,
            out int estimatedInputTokens,
            out int allowedInputTokens)
        {
            var serialized = SerializeConversationMessages(messages);
            estimatedInputTokens = EstimateVllmInputTokens(serialized, tools, hasStructuredResponseFormat);
            var dynamicSlack = Math.Max(64, estimatedInputTokens / 20);
            var safetyBuffer = 256 + dynamicSlack;
            allowedInputTokens = NumCtx.HasValue ? (NumCtx.Value - Math.Max(1, expectedOutputTokens) - safetyBuffer) : int.MaxValue;
            return NumCtx.HasValue && NumCtx.Value > 0 && allowedInputTokens > 0 && estimatedInputTokens <= allowedInputTokens;
        }

        private static List<Dictionary<string, object>> SerializeConversationMessages(IReadOnlyList<ConversationMessage> messages)
        {
            var serialized = new List<Dictionary<string, object>>();
            foreach (var m in messages ?? Array.Empty<ConversationMessage>())
            {
                if (m == null) continue;

                var msgDict = new Dictionary<string, object>
                {
                    { "role", m.Role ?? string.Empty },
                    { "content", m.Content ?? string.Empty }
                };

                if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) &&
                    m.ToolCalls != null &&
                    m.ToolCalls.Any())
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

                if (string.Equals(m.Role, "tool", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(m.ToolCallId))
                {
                    msgDict["tool_call_id"] = m.ToolCallId!;
                }

                serialized.Add(msgDict);
            }

            return serialized;
        }

        private static List<ConversationMessage> CloneConversationMessages(IReadOnlyList<ConversationMessage> messages)
        {
            var clone = new List<ConversationMessage>();
            foreach (var message in messages ?? Array.Empty<ConversationMessage>())
            {
                if (message == null) continue;
                clone.Add(new ConversationMessage
                {
                    Role = message.Role,
                    Content = message.Content,
                    ToolCallId = message.ToolCallId,
                    ToolCalls = message.ToolCalls?.Select(tc => new ToolCallFromModel
                    {
                        Id = tc.Id,
                        ToolName = tc.ToolName,
                        Arguments = tc.Arguments
                    }).ToList()
                });
            }

            return clone;
        }

        private static bool TryRemoveThirdConversationStep(List<ConversationMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return false;
            }

            var conversationIndexes = messages
                .Select((m, idx) => new { Message = m, Index = idx })
                .Where(x => x.Message != null &&
                            !string.Equals(x.Message.Role, "system", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(x.Message.Content))
                .Select(x => x.Index)
                .ToList();

            if (conversationIndexes.Count <= 4)
            {
                return false;
            }

            messages.RemoveAt(conversationIndexes[2]);
            return true;
        }

        private static bool TryRemoveHistoricalErrorsFromSystem(List<ConversationMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return false;
            }

            var changed = false;
            const string marker = "IN PASSATO HAI COMMESSO QUESTI ERRORI";
            foreach (var m in messages)
            {
                if (m == null ||
                    !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(m.Content))
                {
                    continue;
                }

                var content = m.Content!;
                var idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    continue;
                }

                var reduced = content[..idx].TrimEnd();
                if (!string.Equals(reduced, content, StringComparison.Ordinal))
                {
                    m.Content = reduced;
                    changed = true;
                }
            }

            return changed;
        }

        private static bool TryRemoveSingleJsonExample(List<ConversationMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return false;
            }

            var jsonBlockRegex = new Regex(@"```json\s*[\s\S]*?```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var seenExamples = 0;
            for (var i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m == null || string.IsNullOrWhiteSpace(m.Content))
                {
                    continue;
                }

                var matches = jsonBlockRegex.Matches(m.Content);
                if (matches.Count == 0)
                {
                    continue;
                }

                foreach (Match match in matches)
                {
                    if (!match.Success) continue;
                    seenExamples++;
                    if (seenExamples == 2)
                    {
                        m.Content = m.Content.Remove(match.Index, match.Length).Trim();
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryShrinkLongestMessage(List<ConversationMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                return false;
            }

            var candidate = messages
                .Where(m => m != null && !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Content?.Length ?? 0)
                .FirstOrDefault();
            if (candidate == null || string.IsNullOrWhiteSpace(candidate.Content))
            {
                return false;
            }

            var original = candidate.Content!;
            if (original.Length < 600)
            {
                return false;
            }

            var targetLength = Math.Max(400, (int)(original.Length * 0.85));
            if (targetLength >= original.Length)
            {
                return false;
            }

            var headLength = Math.Max(1, (int)(targetLength * 0.7));
            var tailLength = Math.Max(1, targetLength - headLength);
            if (headLength + tailLength >= original.Length)
            {
                return false;
            }

            var reduced = string.Concat(
                original[..headLength],
                Environment.NewLine,
                "[...contenuto ridotto automaticamente per limiti di contesto...]",
                Environment.NewLine,
                original[^tailLength..]);

            if (reduced.Length >= original.Length)
            {
                return false;
            }

            candidate.Content = reduced;
            return true;
        }

        private int ClampVllmMaxTokens(
            int requestedMaxTokens,
            IReadOnlyList<Dictionary<string, object>> serializedMessages,
            IReadOnlyList<Dictionary<string, object>> tools,
            bool hasStructuredResponseFormat)
        {
            var positiveRequested = Math.Max(1, requestedMaxTokens);
            if (!NumCtx.HasValue || NumCtx.Value <= 0)
            {
                return positiveRequested;
            }

            var estimatedInputTokens = EstimateVllmInputTokens(serializedMessages, tools, hasStructuredResponseFormat);
            var dynamicSlack = Math.Max(64, estimatedInputTokens / 20);
            var safetyBuffer = 256 + dynamicSlack;
            var safeLimit = NumCtx.Value - estimatedInputTokens - safetyBuffer;
            if (safeLimit <= 0)
            {
                throw new InvalidOperationException(
                    $"vLLM prompt troppo lungo per il contesto disponibile: num_ctx={NumCtx.Value}, input_stimato={estimatedInputTokens}, margine_sicurezza={safetyBuffer}. Riduci prompt/schema o aumenta MaxModelLen.");
            }

            return Math.Min(positiveRequested, safeLimit);
        }

        private int ClampTokensToKnownContextBudget(
            int requestedTokens,
            IReadOnlyList<Dictionary<string, object>> serializedMessages,
            IReadOnlyList<Dictionary<string, object>> tools,
            bool hasStructuredResponseFormat)
        {
            var positiveRequested = Math.Max(1, requestedTokens);
            if (!NumCtx.HasValue || NumCtx.Value <= 0)
            {
                return positiveRequested;
            }

            var estimatedInputTokens = EstimateVllmInputTokens(serializedMessages, tools, hasStructuredResponseFormat);
            const int safetyBuffer = 64;
            var safeLimit = NumCtx.Value - estimatedInputTokens - safetyBuffer;

            // Avoid invalid requests where output pushes total tokens above context.
            // If prompt is already too large, keep at least 1 token to let provider return a precise error.
            return Math.Min(positiveRequested, Math.Max(1, safeLimit));
        }

        private static int EstimateVllmInputTokens(
            IReadOnlyList<Dictionary<string, object>> serializedMessages,
            IReadOnlyList<Dictionary<string, object>> tools,
            bool hasStructuredResponseFormat)
        {
            var contentChars = 0;
            var structuralOverhead = 0;

            foreach (var message in serializedMessages)
            {
                if (message.TryGetValue("content", out var contentObj) && contentObj != null)
                {
                    contentChars += contentObj.ToString()?.Length ?? 0;
                }

                if (message.TryGetValue("role", out var roleObj) && roleObj != null)
                {
                    structuralOverhead += 8 + (roleObj.ToString()?.Length ?? 0);
                }

                // Small per-message overhead for separators / wrappers.
                structuralOverhead += 16;
            }

            if (tools.Count > 0)
            {
                structuralOverhead += JsonSerializer.Serialize(tools).Length / 4;
            }

            if (hasStructuredResponseFormat)
            {
                // response_format is not part of the visible prompt, but reserve a modest budget
                // because some providers count schema-related serialization overhead.
                structuralOverhead += 256;
            }

            // Approximation closer to actual tokenizer behavior for Italian prose + JSON payloads.
            // Using only the serialized request length was too pessimistic and could collapse
            // the output budget to 1 token on large but still valid prompts.
            var estimated = (contentChars / 4) + structuralOverhead + 64;
            return Math.Max(128, estimated);
        }

        private object NormalizeResponseFormatForProvider(object responseFormat)
        {
            if (!_isVllm)
            {
                return responseFormat;
            }

            JsonElement element;
            if (responseFormat is JsonElement jsonElement)
            {
                element = jsonElement;
            }
            else
            {
                element = JsonSerializer.SerializeToElement(responseFormat);
            }

            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("type", out var typeProp) &&
                typeProp.ValueKind == JsonValueKind.String)
            {
                var typeValue = typeProp.GetString();
                if (string.Equals(typeValue, "json_schema", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeValue, "json_object", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeValue, "text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(typeValue, "structural_tag", StringComparison.OrdinalIgnoreCase))
                {
                    return responseFormat;
                }
            }

            var schemaName = SanitizeResponseFormatName(_modelId);
            return new Dictionary<string, object?>
            {
                ["type"] = "json_schema",
                ["json_schema"] = new Dictionary<string, object?>
                {
                    ["name"] = schemaName,
                    ["schema"] = responseFormat
                }
            };
        }

        private static string SanitizeResponseFormatName(string? modelId)
        {
            var source = string.IsNullOrWhiteSpace(modelId) ? "response_schema" : modelId;
            var chars = source
                .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
                .ToArray();
            var sanitized = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(sanitized) ? "response_schema" : sanitized;
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

                _modelBridge = new LangChainChatBridge(modelEndpoint, modelId, apiKey, httpClient, logger, null, false, null, null, false, noTempModels, noRepeatPenaltyModels, noTopPModels, noFrequencyPenaltyModels, noMaxTokensModels, noTopKModels, noRepeatLastNModels, noNumPredictModels);
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
