using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public class ResponseCheckerService
    {
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly DatabaseService _database;
        private readonly ICustomLogger _logger;
        private readonly HttpClient _httpClient;

        public ResponseCheckerService(
            ILangChainKernelFactory kernelFactory,
            DatabaseService database,
            ICustomLogger logger,
            IHttpClientFactory httpClientFactory)
        {
            _kernelFactory = kernelFactory;
            _database = database;
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient();
        }

        /// <summary>
        /// Validate writer responses including semantic alignment checks (embeddings).
        /// This is intended to be used for writer-type agents where semantic similarity
        /// between the instruction and the output is required.
        /// </summary>
        public async Task<ValidationResult> ValidateWriterResponseAsync(
            string stepInstruction,
            string modelOutput,
            Dictionary<string, object>? validationCriteria,
            int threadId,
            string? agentName = null,
            string? modelName = null,
            string? taskType = null)
        {
            _logger.Log("Information", "MultiStep", $"Starting writer-specific validation - Agent: {agentName}, Model: {modelName}");

            double? semanticScore = null;
            try
            {
                // Skip semantic calculation for tts_schema explicitly
                if (!string.Equals(taskType, "tts_schema", StringComparison.OrdinalIgnoreCase))
                {
                    semanticScore = await CalculateSemanticAlignmentAsync(stepInstruction, modelOutput);
                }
            }
            catch (Exception ex)
            {
                _logger.Log("Warning", "MultiStep", $"Semantic alignment calculation failed: {ex.Message}");
            }

            // If semantic threshold is provided in validationCriteria, enforce it early
            if (semanticScore.HasValue && validationCriteria != null && validationCriteria.ContainsKey("semantic_threshold"))
            {
                var thresholdValue = validationCriteria["semantic_threshold"];
                double threshold;
                if (thresholdValue is JsonElement je)
                {
                    threshold = je.ValueKind == JsonValueKind.Number ? je.GetDouble() : 0.0;
                }
                else
                {
                    threshold = Convert.ToDouble(thresholdValue);
                }
                
                if (semanticScore.Value < threshold)
                {
                    _logger.Log("Warning", "MultiStep", $"Semantic score {semanticScore.Value:F2} below threshold {threshold:F2}, writer validation failed");
                    return new ValidationResult
                    {
                        IsValid = false,
                        Reason = $"Semantic alignment score ({semanticScore.Value:F2}) is below required threshold ({threshold:F2}). Output does not sufficiently match the step instruction.",
                        NeedsRetry = true,
                        SemanticScore = semanticScore
                    };
                }
            }

            // Delegate to the generic checker agent for the remaining checks, but include semantic info in the prompt
            var checkerAgent = _database.ListAgents()
                .FirstOrDefault(a => a.Role == "response_checker" && a.IsActive);

            if (checkerAgent == null)
            {
                _logger.Log("Warning", "MultiStep", "No active response_checker agent found for writer validation, skipping validation");
                return new ValidationResult
                {
                    IsValid = true,
                    Reason = "No checker agent available",
                    NeedsRetry = false,
                    SemanticScore = null
                };
            }

            var criteriaJson = validationCriteria != null ? JsonSerializer.Serialize(validationCriteria) : "{}";
            var semanticInfo = semanticScore.HasValue ? $"Semantic Alignment Score: {semanticScore.Value:F2} (0-1 scale)" : string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("You are a Response Checker. Validate if the writer's output meets the requirements.");
            sb.AppendLine();
            sb.AppendLine("**Step Instruction:**");
            sb.AppendLine(stepInstruction);
            sb.AppendLine();
            sb.AppendLine("**Writer Output:**");
            sb.AppendLine(modelOutput);
            sb.AppendLine();
            sb.AppendLine("**Validation Criteria:");
            sb.AppendLine(criteriaJson);
            if (!string.IsNullOrEmpty(semanticInfo))
            {
                sb.AppendLine();
                sb.AppendLine(semanticInfo);
            }
            sb.AppendLine();
            sb.AppendLine("**Your Task:");
            sb.AppendLine("Check if the output:");
            sb.AppendLine("1. Adheres to the step requirements");
            sb.AppendLine("2. Contains no questions or requests for clarification");
            sb.AppendLine("3. Does not anticipate future steps");
            sb.AppendLine("4. Is complete (not truncated)");
            sb.AppendLine("5. Meets minimum length if specified");
            sb.AppendLine();
            sb.AppendLine("Return ONLY a JSON object with this structure:");
            sb.AppendLine("{");
            sb.AppendLine("  \"is_valid\": true or false,");
            sb.AppendLine("  \"reason\": \"brief explanation\",");
            sb.AppendLine("  \"needs_retry\": true or false");
            sb.AppendLine("}");

            var prompt = sb.ToString();

            try
            {
                var modelInfo = _database.GetModelInfoById(checkerAgent.ModelId ?? 0);
                var checkerModelName = modelInfo?.Name ?? "phi3:mini";

                // Push a scope with "Response Checker" as agent name so logs show correctly in ChatLog
                using var checkerScope = LogScope.Push(
                    "response_checker_validation",
                    null,
                    LogScope.CurrentStepNumber,
                    LogScope.CurrentMaxStep,
                    "Response Checker");

                var orchestrator = _kernelFactory.CreateOrchestrator(checkerModelName, new List<string>());
                var bridge = _kernelFactory.CreateChatBridge(checkerModelName);
                var loop = new ReActLoopOrchestrator(orchestrator, _logger, maxIterations: 5, modelBridge: bridge);

                var result = await loop.ExecuteAsync(prompt);

                _logger.Log("Information", "MultiStep", $"Checker raw response (writer): {result.FinalResponse}");

                // Parse JSON response and include semantic score
                var validationResult = ParseValidationResponse(result.FinalResponse, semanticScore);
                _logger.Log("Information", "MultiStep", $"Writer validation result: is_valid={validationResult.IsValid}, reason={validationResult.Reason}");

                return validationResult;
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "MultiStep", $"Checker agent invocation failed (writer): {ex.Message}", ex.ToString());
                return new ValidationResult
                {
                    IsValid = false,
                    Reason = $"Validation error: {ex.Message}",
                    NeedsRetry = true,
                    SemanticScore = semanticScore
                };
            }
        }

        public async Task<ValidationResult> ValidateStepOutputAsync(
            string stepInstruction,
            string modelOutput,
            Dictionary<string, object>? validationCriteria,
            int threadId,
            string? agentName = null,
            string? modelName = null,
            string? taskType = null)
        {
            _logger.Log("Information", "MultiStep", $"Starting step validation - Agent: {agentName}, Model: {modelName}");
            await Task.CompletedTask;

            // Perform only deterministic/basic checks here and DO NOT invoke the response_checker agent.
            // Response checker (LLM) calls are disabled by default and will be invoked only when
            // the executor produced free text instead of expected tool calls (see orchestrator logic).

            // Basic deterministic checks
            if (string.IsNullOrWhiteSpace(modelOutput))
            {
                return new ValidationResult
                {
                    IsValid = false,
                    Reason = "Model output is empty",
                    NeedsRetry = true,
                    SemanticScore = null
                };
            }

            // Additional deterministic checks could be added here (length, simple required tokens, etc.)
            // For now, accept the output as valid at this stage; task-specific deterministic
            // checks (e.g. TTS coverage) are performed by the orchestrator before calling this.

            return new ValidationResult
            {
                IsValid = true,
                Reason = "Deterministic checks passed",
                NeedsRetry = false,
                SemanticScore = null
            };
        }

        /// <summary>
        /// Heuristic to detect whether the model output contains tool calls (e.g. ttsschema functions).
        /// Used by orchestrator to decide whether to invoke the response_checker to craft a reminder.
        /// </summary>
        public bool ContainsToolCalls(string modelOutput)
        {
            if (string.IsNullOrWhiteSpace(modelOutput)) return false;

            // Quick heuristics: presence of known function names or a tool_calls/json structure
            if (modelOutput.IndexOf("add_narration", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (modelOutput.IndexOf("add_phrase", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (modelOutput.IndexOf("\"tool_calls\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (modelOutput.IndexOf("\"function\"", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        /// <summary>
        /// Ask a response_checker agent to craft a corrective message when the primary model
        /// replies con testo libero invece di usare i tool. Restituisce la risposta da
        /// inviare al modello principale (plain text) oppure null se non disponibile.
        /// </summary>
        public async Task<(string role, string content)?> BuildToolUseReminderAsync(
            string? systemMessage,
            string userPrompt,
            string modelResponse,
            List<Dictionary<string, object>> toolSchemas,
            int attempt)
        {
            var checkerAgent = _database.ListAgents()
                .FirstOrDefault(a => a.Role == "response_checker" && a.IsActive);

            if (checkerAgent == null)
            {
                _logger.Log("Warning", "ResponseChecker", "No active response_checker agent available for tool reminder");
                return null;
            }

            var toolNames = toolSchemas
                .Select(s => s.TryGetValue("function", out var funcObj) && funcObj is Dictionary<string, object> f
                    ? f.TryGetValue("name", out var nameObj) ? nameObj?.ToString() ?? string.Empty : string.Empty
                    : string.Empty)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var prompt = new StringBuilder();
            prompt.AppendLine("You are a response_checker helping another agent that MUST call functions instead of replying with free text.");
            prompt.AppendLine($"Attempt: {attempt + 1}/2.");
            prompt.AppendLine("Provide a SHORT assistant reply to the primary model that reminds it to use the tools, and if possible suggest the first tool to call.");
            prompt.AppendLine("Return ONLY the assistant reply text, no JSON, no extra explanations.");
            prompt.AppendLine();
            if (!string.IsNullOrWhiteSpace(systemMessage))
            {
                prompt.AppendLine("=== System message ===");
                prompt.AppendLine(systemMessage);
                prompt.AppendLine();
            }
            prompt.AppendLine("=== User prompt ===");
            prompt.AppendLine(userPrompt);
            prompt.AppendLine();
            prompt.AppendLine("=== Model response (incorrect - no tool calls) ===");
            prompt.AppendLine(modelResponse);
            prompt.AppendLine();
            prompt.AppendLine("=== Available tools ===");
            foreach (var t in toolNames)
            {
                prompt.AppendLine($"- {t}");
            }

            try
            {
                var modelInfo = _database.GetModelInfoById(checkerAgent.ModelId ?? 0);
                var checkerModelName = modelInfo?.Name ?? "phi3:mini";

                // Push a scope with "Response Checker" as agent name so logs show correctly in ChatLog
                using var checkerScope = LogScope.Push(
                    "response_checker_tool_reminder",
                    null,
                    LogScope.CurrentStepNumber,
                    LogScope.CurrentMaxStep,
                    "Response Checker");

                var orchestrator = _kernelFactory.CreateOrchestrator(checkerModelName, new List<string>());
                var bridge = _kernelFactory.CreateChatBridge(checkerModelName);
                var loop = new ReActLoopOrchestrator(orchestrator, _logger, maxIterations: 3, modelBridge: bridge);

                var result = await loop.ExecuteAsync(prompt.ToString());
                var reply = result.FinalResponse;
                if (string.IsNullOrWhiteSpace(reply))
                {
                    _logger.Log("Warning", "ResponseChecker", "Tool reminder produced empty reply");
                    return null;
                }

                // Decide whether this reminder should be injected as a system message
                // For TTS schema scenarios, prefer using a system message so reminders don't appear inside the user prompt.
                var wantsSystem = toolNames.Any(n => string.Equals(n, "ttsschema", StringComparison.OrdinalIgnoreCase));
                var role = wantsSystem ? "system" : "assistant";
                return (role, reply);
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "ResponseChecker", $"Tool reminder failed: {ex.Message}", ex.ToString());
                return null;
            }
        }

        private ValidationResult ParseValidationResponse(string response, double? semanticScore)
        {
            try
            {
                // First, try to extract from Ollama response format (if present)
                if (response.Contains("\"message\"") && response.Contains("\"content\""))
                {
                    try
                    {
                        var ollamaDoc = JsonDocument.Parse(response);
                        if (ollamaDoc.RootElement.TryGetProperty("message", out var message) &&
                            message.TryGetProperty("content", out var content))
                        {
                            response = content.GetString() ?? response;
                        }
                    }
                    catch
                    {
                        // Continue with original response
                    }
                }

                // Try to extract JSON from response
                var jsonStart = response.IndexOf('{');
                var jsonEnd = response.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    
                    // Parse with JsonDocument for flexible property name handling
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    
                    // Support multiple property name formats: IsValid, is_valid, isValid, valid
                    var validFlag = false;
                    string? validReason = null;
                    bool? retryFlag = null;

                    if (root.TryGetProperty("is_valid", out var isValidProp))
                        validFlag = isValidProp.GetBoolean();
                    else if (root.TryGetProperty("IsValid", out isValidProp))
                        validFlag = isValidProp.GetBoolean();
                    else if (root.TryGetProperty("isValid", out isValidProp))
                        validFlag = isValidProp.GetBoolean();
                    else if (root.TryGetProperty("valid", out isValidProp))
                        validFlag = isValidProp.GetBoolean();

                    if (root.TryGetProperty("reason", out var reasonProp))
                        validReason = reasonProp.GetString();
                    else if (root.TryGetProperty("Reason", out reasonProp))
                        validReason = reasonProp.GetString();

                    if (root.TryGetProperty("needs_retry", out var retryProp))
                        retryFlag = retryProp.GetBoolean();
                    else if (root.TryGetProperty("NeedsRetry", out retryProp))
                        retryFlag = retryProp.GetBoolean();
                    else if (root.TryGetProperty("needsRetry", out retryProp))
                        retryFlag = retryProp.GetBoolean();

                    return new ValidationResult
                    {
                        IsValid = validFlag,
                        Reason = validReason ?? "Validation completed",
                        NeedsRetry = retryFlag ?? !validFlag,
                        SemanticScore = semanticScore
                    };
                }

                // Fallback: try to parse natural language response
                // Look for keywords like "valid", "invalid", "meets", "fails", etc.
                var lowerResponse = response.ToLowerInvariant();
                
                bool isValid = false;
                string reason = response;

                // Check for positive indicators
                if (lowerResponse.Contains("is valid") || 
                    lowerResponse.Contains("valido") ||
                    lowerResponse.Contains("meets all") || 
                    lowerResponse.Contains("soddisfa") ||
                    lowerResponse.Contains("rispetta"))
                {
                    isValid = true;
                    reason = "Output appears valid based on natural language response";
                }
                // Check for negative indicators
                else if (lowerResponse.Contains("invalid") || 
                         lowerResponse.Contains("non valido") ||
                         lowerResponse.Contains("does not meet") || 
                         lowerResponse.Contains("non soddisfa") ||
                         lowerResponse.Contains("manca") ||
                         lowerResponse.Contains("missing"))
                {
                    isValid = false;
                    // Try to extract the reason from the response
                    var sentences = response.Split('.', StringSplitOptions.RemoveEmptyEntries);
                    reason = sentences.Length > 0 ? sentences[0].Trim() : response;
                }

                return new ValidationResult
                {
                    IsValid = isValid,
                    Reason = reason,
                    NeedsRetry = !isValid,
                    SemanticScore = semanticScore
                };
            }
            catch
            {
                // Fall through to default
            }

            // Fallback: parse failed, assume needs retry
            return new ValidationResult
            {
                IsValid = false,
                Reason = "Could not parse validation response",
                NeedsRetry = true,
                SemanticScore = semanticScore
            };
        }

        public async Task<double?> CalculateSemanticAlignmentAsync(string text1, string text2)
        {
            try
            {
                // Call Ollama embeddings API for both texts
                var embedding1 = await GetEmbeddingAsync(text1);
                var embedding2 = await GetEmbeddingAsync(text2);

                if (embedding1 == null || embedding2 == null) return null;

                // Calculate cosine similarity
                return CosineSimilarity(embedding1, embedding2);
            }
            catch (Exception)
            {
                return null; // Best-effort: return null on error
            }
        }

        private async Task<float[]?> GetEmbeddingAsync(string text)
        {
            try
            {
                var payload = new
                {
                    model = "nomic-embed-text",
                    prompt = text
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("http://localhost:11434/api/embeddings", content);

                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("embedding", out var embeddingProp))
                {
                    var embedding = new List<float>();
                    foreach (var element in embeddingProp.EnumerateArray())
                    {
                        embedding.Add((float)element.GetDouble());
                    }
                    return embedding.ToArray();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private double CosineSimilarity(float[] vec1, float[] vec2)
        {
            if (vec1.Length != vec2.Length) return 0;

            double dotProduct = 0;
            double magnitude1 = 0;
            double magnitude2 = 0;

            for (int i = 0; i < vec1.Length; i++)
            {
                dotProduct += vec1[i] * vec2[i];
                magnitude1 += vec1[i] * vec1[i];
                magnitude2 += vec2[i] * vec2[i];
            }

            magnitude1 = Math.Sqrt(magnitude1);
            magnitude2 = Math.Sqrt(magnitude2);

            if (magnitude1 == 0 || magnitude2 == 0) return 0;

            return dotProduct / (magnitude1 * magnitude2);
        }

        /// <summary>
        /// Validates TTS schema response by checking text coverage and tool_call completeness.
        /// Tolerant of formatting issues, focuses on coverage analysis.
        /// Returns validation result with feedback message for model retry.
        /// </summary>
        public TtsValidationResult ValidateTtsSchemaResponse(
            string modelResponse,
            string originalStoryChunk,
            double minimumCoverageThreshold = 0.80)
        {
            var result = new TtsValidationResult
            {
                IsValid = false,
                CoveragePercent = 0,
                OriginalChars = 0,
                CoveredChars = 0,
                RemainingChars = 0
            };

            try
            {
                // Step 1: Try to deserialize response to ApiResponse
                ApiResponse? apiResponse = null;
                try
                {
                    apiResponse = JsonSerializer.Deserialize<ApiResponse>(modelResponse);
                }
                catch (JsonException)
                {
                    result.Warnings.Add("Response is not valid JSON, attempting fallback parsing");
                }

                // Step 2: Extract tool_calls from ApiResponse or fallback to manual parsing
                if (apiResponse?.Message?.ToolCalls != null && apiResponse.Message.ToolCalls.Count > 0)
                {
                    // Extract from structured response
                    foreach (var tc in apiResponse.Message.ToolCalls)
                    {
                        if (tc.Function == null) continue;

                        var parsedCall = new ParsedToolCall
                        {
                            Id = tc.Function.Name ?? "unknown",
                            FunctionName = tc.Function.Name ?? "unknown"
                        };

                        // Parse arguments
                        if (tc.Function.Arguments != null)
                        {
                            try
                            {
                                if (tc.Function.Arguments is JsonElement jsonElem)
                                {
                                    parsedCall.Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElem.GetRawText()) ?? new();
                                }
                                else if (tc.Function.Arguments is string str)
                                {
                                    parsedCall.Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(str) ?? new();
                                }
                                else
                                {
                                    var serialized = JsonSerializer.Serialize(tc.Function.Arguments);
                                    parsedCall.Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(serialized) ?? new();
                                }
                            }
                            catch
                            {
                                result.Warnings.Add($"Failed to parse arguments for tool_call {tc.Function.Name}");
                            }
                        }

                        result.ExtractedToolCalls.Add(parsedCall);
                    }
                }
                else
                {
                    // Fallback: manually extract tool_calls from response text
                    ExtractToolCallsFromText(modelResponse, result);
                }

                // Step 3: Build temporary TTS schema and extract all text
                var allExtractedText = new List<string>();
                foreach (var tc in result.ExtractedToolCalls)
                {
                    // Check for required parameters
                    if (tc.FunctionName == "add_narration")
                    {
                        if (!tc.Arguments.ContainsKey("text"))
                        {
                            result.Errors.Add($"Tool call {tc.FunctionName} missing required parameter 'text'");
                            continue;
                        }
                        var text = tc.Arguments["text"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            allExtractedText.Add(text);
                        }
                    }
                    else if (tc.FunctionName == "add_phrase")
                    {
                        if (!tc.Arguments.ContainsKey("text"))
                        {
                            result.Errors.Add($"Tool call {tc.FunctionName} missing required parameter 'text'");
                            continue;
                        }
                        if (!tc.Arguments.ContainsKey("character"))
                        {
                            result.Errors.Add($"Tool call {tc.FunctionName} missing required parameter 'character'");
                        }
                        if (!tc.Arguments.ContainsKey("emotion"))
                        {
                            result.Warnings.Add($"Tool call {tc.FunctionName} missing optional parameter 'emotion'");
                        }
                        var text = tc.Arguments["text"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            allExtractedText.Add(text);
                        }
                    }
                }

                // Step 4: Calculate text coverage
                var normalizedOriginal = NormalizeText(originalStoryChunk);
                var remainingText = normalizedOriginal;

                foreach (var extractedText in allExtractedText)
                {
                    var normalizedExtracted = NormalizeText(extractedText);
                    if (string.IsNullOrWhiteSpace(normalizedExtracted)) continue;

                    // Remove matched text from remaining (case-insensitive)
                    var index = remainingText.IndexOf(normalizedExtracted, StringComparison.OrdinalIgnoreCase);
                    if (index >= 0)
                    {
                        remainingText = remainingText.Remove(index, normalizedExtracted.Length);
                    }
                }

                result.OriginalChars = normalizedOriginal.Length;
                result.RemainingChars = remainingText.Length;
                result.CoveredChars = result.OriginalChars - result.RemainingChars;
                result.CoveragePercent = result.OriginalChars > 0
                    ? (double)result.CoveredChars / result.OriginalChars
                    : 0;

                // Step 5: Validate coverage threshold
                if (result.CoveragePercent >= minimumCoverageThreshold)
            {
                result.IsValid = true;
                result.FeedbackMessage = $"Text coverage: {result.CoveragePercent:P1}. Schema is valid.";
            }
            else
            {
                result.IsValid = false;
                var missingPercent = (1 - result.CoveragePercent) * 100;
                result.FeedbackMessage = $"Text coverage only {result.CoveragePercent:P1} (missing {missingPercent:F1}%). Trascrivi tutto il chunk con blocchi [NARRATORE] o [PERSONAGGIO: Nome | EMOZIONE: emotion], senza saltare nulla, finché il testo è esaurito.";
                // For TTS schema coverage failures, prefer injecting the corrective feedback as a system message
                // so the agent receives it outside of the user prompt.
                result.ShouldInjectAsSystem = true;
            }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Validation exception: {ex.Message}");
                result.FeedbackMessage = "Failed to validate response due to parsing error. Ensure your response contains valid tool_calls with all required parameters.";
            }

            return result;
        }

        /// <summary>
        /// Fallback method to extract tool_calls from raw response text using regex/heuristics.
        /// </summary>
        private void ExtractToolCallsFromText(string responseText, TtsValidationResult result)
        {
            try
            {
                // Try to parse as JSON and look for tool_calls array or embedded tool_calls
                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;

                // Look for tool_calls at various levels
                if (root.TryGetProperty("tool_calls", out var toolCallsArray))
                {
                    ParseToolCallsArray(toolCallsArray, result);
                }
                else if (root.TryGetProperty("message", out var message) && 
                         message.TryGetProperty("tool_calls", out var msgToolCalls))
                {
                    ParseToolCallsArray(msgToolCalls, result);
                }
            }
            catch
            {
                result.Warnings.Add("Could not extract tool_calls from response text");
            }
        }

        private void ParseToolCallsArray(JsonElement toolCallsArray, TtsValidationResult result)
        {
            if (toolCallsArray.ValueKind != JsonValueKind.Array) return;

            foreach (var tc in toolCallsArray.EnumerateArray())
            {
                if (!tc.TryGetProperty("function", out var func)) continue;

                var parsedCall = new ParsedToolCall
                {
                    Id = tc.TryGetProperty("id", out var idElem) ? idElem.GetString() ?? "unknown" : "unknown",
                    FunctionName = func.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "unknown" : "unknown"
                };

                if (func.TryGetProperty("arguments", out var argsElem))
                {
                    try
                    {
                        if (argsElem.ValueKind == JsonValueKind.Object)
                        {
                            parsedCall.Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(argsElem.GetRawText()) ?? new();
                        }
                        else if (argsElem.ValueKind == JsonValueKind.String)
                        {
                            var argsStr = argsElem.GetString() ?? "{}";
                            parsedCall.Arguments = JsonSerializer.Deserialize<Dictionary<string, object>>(argsStr) ?? new();
                        }
                    }
                    catch
                    {
                        // Ignore parse errors for individual arguments
                    }
                }

                result.ExtractedToolCalls.Add(parsedCall);
            }
        }

        /// <summary>
        /// Normalize text for coverage comparison: remove punctuation, lowercase, trim whitespace.
        /// </summary>
        private string NormalizeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            // Remove common punctuation
            var normalized = System.Text.RegularExpressions.Regex.Replace(text, @"[.,!?;:\""\'\-\(\)\[\]]", "");
            // Collapse multiple spaces
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");
            return normalized.Trim().ToLowerInvariant();
        }
    }
}
