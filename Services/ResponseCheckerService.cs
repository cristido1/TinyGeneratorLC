using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services.Text;

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
            string? taskType = null,
            int? currentStepNumber = null,
            Models.StepTemplate? stepTemplate = null,
            Models.TaskExecution? execution = null)
        {
            _logger.Log("Information", "MultiStep", $"Starting writer-specific validation - Agent: {agentName}, Model: {modelName}");

            // VALIDAZIONE MINIMO CARATTERI TRAMA (PRIMA del response_checker)
            // Controlla se:
            // 1. Lo step corrente è configurato come step di trama (TramaSteps)
            // 2. È stato impostato un numero minimo di caratteri per la trama (MinCharsTrama)
            // Se entrambe le condizioni sono vere, verifica che la trama abbia almeno MinCharsTrama caratteri
            // Se la validazione fallisce, ritorna errore SENZA chiamare il response_checker
            if (currentStepNumber.HasValue && stepTemplate != null && stepTemplate.MinCharsTrama.HasValue && stepTemplate.MinCharsTrama > 0)
            {
                if (stepTemplate.ParsedTramaSteps.Contains(currentStepNumber.Value))
                {
                    if (modelOutput.Length < stepTemplate.MinCharsTrama.Value)
                    {
                        _logger.Log("Warning", "MultiStep", 
                            $"Step {currentStepNumber} output too short for trama: {modelOutput.Length} chars < {stepTemplate.MinCharsTrama} required");
                        return new ValidationResult
                        {
                            IsValid = false,
                            Reason = $"La trama non ha abbastanza caratteri. Richiesti: {stepTemplate.MinCharsTrama} caratteri, ottenuti: {modelOutput.Length}. Scrivi una trama più completa.",
                            NeedsRetry = true,
                            SemanticScore = null
                        };
                    }
                    else
                    {
                        _logger.Log("Information", "MultiStep",
                            $"Step {currentStepNumber} trama validation passed: {modelOutput.Length} chars >= {stepTemplate.MinCharsTrama} required");
                    }
                }
            }

            // VALIDAZIONE MINIMO CARATTERI STORIA COMPLETA (PRIMA del response_checker)
            // Controlla se:
            // 1. Lo step corrente è lo step della storia completa (FullStoryStep)
            // 2. È stato impostato un numero minimo di caratteri per la storia (MinCharsStory)
            // Se entrambe le condizioni sono vere, verifica che la storia abbia almeno MinCharsStory caratteri
            // Se la validazione fallisce, ritorna errore SENZA chiamare il response_checker
            if (currentStepNumber.HasValue && stepTemplate != null && stepTemplate.FullStoryStep.HasValue && 
                currentStepNumber == stepTemplate.FullStoryStep && 
                stepTemplate.MinCharsStory.HasValue && stepTemplate.MinCharsStory > 0)
            {
                if (modelOutput.Length < stepTemplate.MinCharsStory.Value)
                {
                    _logger.Log("Warning", "MultiStep", 
                        $"Step {currentStepNumber} output too short for full story: {modelOutput.Length} chars < {stepTemplate.MinCharsStory} required");
                    return new ValidationResult
                    {
                        IsValid = false,
                        Reason = $"La storia è troppo breve. Richiesti: {stepTemplate.MinCharsStory} caratteri, ottenuti: {modelOutput.Length}. Scrivi una storia più completa con più dettagli.",
                        NeedsRetry = true,
                        SemanticScore = null
                    };
                }
                else
                {
                    _logger.Log("Information", "MultiStep",
                        $"Step {currentStepNumber} full story validation passed: {modelOutput.Length} chars >= {stepTemplate.MinCharsStory} required");
                }
            }

            // Detect if this is a plot/outline step (trama)
            bool isPlotStep = IsPlotOutlineStep(stepInstruction);

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

            // For plot/outline steps, perform pre-checker validation (semantic + length)
            // without consulting response_checker agent
            if (isPlotStep)
            {
                // Check semantic alignment for plot steps (must be >= 0.60)
                if (semanticScore.HasValue && semanticScore.Value < 0.60)
                {
                    _logger.Log("Warning", "MultiStep", $"Plot outline semantic score {semanticScore.Value:F2} below threshold 0.60");
                    return new ValidationResult
                    {
                        IsValid = false,
                        Reason = $"La trama non è sufficientemente coerente con la richiesta (punteggio: {semanticScore.Value:F2}/1.0). Riscrivi una trama che segua più strettamente le istruzioni.",
                        NeedsRetry = true,
                        SemanticScore = semanticScore
                    };
                }

                // Check minimum length for plot steps (at least 150 characters)
                if (modelOutput.Length < 150)
                {
                    _logger.Log("Warning", "MultiStep", $"Plot outline too short: {modelOutput.Length} chars");
                    return new ValidationResult
                    {
                        IsValid = false,
                        Reason = "La trama è troppo corta. Scrivi una trama più dettagliata con almeno 150 caratteri.",
                        NeedsRetry = true,
                        SemanticScore = semanticScore
                    };
                }

                // If pre-checks pass for plot step, accept it without calling response_checker
                _logger.Log("Information", "MultiStep", $"Plot outline passed pre-checks (semantic: {semanticScore?.ToString("F2") ?? "N/A"}, length: {modelOutput.Length} chars)");
                return new ValidationResult
                {
                    IsValid = true,
                    Reason = "La trama è coerente e ha una lunghezza adeguata.",
                    NeedsRetry = false,
                    SemanticScore = semanticScore
                };
            }

            // For non-plot steps, use the standard response_checker validation
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
                .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                    a.Role.Equals("response_checker", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (checkerAgent == null)
            {
                throw new InvalidOperationException("No active response_checker agent found for writer validation.");
            }

            var criteriaJson = validationCriteria != null ? JsonSerializer.Serialize(validationCriteria) : "{}";
            // For non-plot steps, include semantic score in prompt; for plot steps already validated above
            var semanticInfo = semanticScore.HasValue && !isPlotStep ? $"Semantic Alignment Score: {semanticScore.Value:F2} (0-1 scale)" : string.Empty;

            // IMPORTANT: when invoking response_checker, append response-format instructions to the SYSTEM message,
            // not to a subsequent user prompt.
            var checkerSystemSb = new StringBuilder();
            // Include agent-level prompt/instructions if present so DB-managed instructions are honored
            if (!string.IsNullOrWhiteSpace(checkerAgent.Prompt))
            {
                checkerSystemSb.AppendLine("=== Agent Prompt ===");
                checkerSystemSb.AppendLine(checkerAgent.Prompt);
                checkerSystemSb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(checkerAgent.Instructions))
            {
                checkerSystemSb.AppendLine("=== Agent Instructions ===");
                checkerSystemSb.AppendLine(checkerAgent.Instructions);
                checkerSystemSb.AppendLine();
            }

            checkerSystemSb.AppendLine("You are a Response Checker. Validate if the writer's output meets the requirements.");
            checkerSystemSb.AppendLine();
            checkerSystemSb.AppendLine("**Your Task:");
            checkerSystemSb.AppendLine("Check if the output:");
            checkerSystemSb.AppendLine("1. Adheres to the step requirements");
            checkerSystemSb.AppendLine("2. Contains no questions or requests for clarification");
            checkerSystemSb.AppendLine("3. Does not anticipate future steps");
            checkerSystemSb.AppendLine("4. Is complete (not truncated)");
            checkerSystemSb.AppendLine("5. Meets minimum length if specified");
            checkerSystemSb.AppendLine();
            checkerSystemSb.AppendLine("=== OUTPUT RICHIESTO (OBBLIGATORIO) ===");
            checkerSystemSb.AppendLine("Restituisci SOLO TAG tra parentesi quadre (no JSON) con questa struttura:");
            checkerSystemSb.AppendLine("[IS_VALID]true|false");
            checkerSystemSb.AppendLine("[NEEDS_RETRY]true|false");
            checkerSystemSb.AppendLine("[REASON]Spiega brevemente. Se non valido, cita almeno una REGOLA n violata.");
            checkerSystemSb.AppendLine("[VIOLATED_RULES]1,2  // vuoto se valido");

            var checkerUserSb = new StringBuilder();
            checkerUserSb.AppendLine("**Step Instruction:**");
            checkerUserSb.AppendLine(stepInstruction);
            checkerUserSb.AppendLine();
            checkerUserSb.AppendLine("**Writer Output:**");
            checkerUserSb.AppendLine(modelOutput);
            checkerUserSb.AppendLine();
            checkerUserSb.AppendLine("**Validation Criteria:");
            checkerUserSb.AppendLine(criteriaJson);
            if (!string.IsNullOrEmpty(semanticInfo))
            {
                checkerUserSb.AppendLine();
                checkerUserSb.AppendLine(semanticInfo);
            }

            var checkerSystemMessage = checkerSystemSb.ToString();
            var prompt = checkerUserSb.ToString();

            try
            {
                var modelInfo = _database.GetModelInfoById(checkerAgent.ModelId ?? 0);
                var checkerModelName = modelInfo?.Name;
                if (string.IsNullOrWhiteSpace(checkerModelName))
                {
                    throw new InvalidOperationException($"Response checker agent \"{checkerAgent.Name}\" has no model configured.");
                }

                // Push a scope with "Response Checker" as agent name so logs show correctly in ChatLog
                using var checkerScope = LogScope.Push(
                    "response_checker_validation",
                    null,
                    LogScope.CurrentStepNumber,
                    LogScope.CurrentMaxStep,
                    "Response Checker",
                    agentRole: "response_checker");

                var orchestrator = _kernelFactory.CreateOrchestrator(checkerModelName, new List<string>());
                var bridge = _kernelFactory.CreateChatBridge(checkerModelName);
                var loop = new ReActLoopOrchestrator(
                    orchestrator,
                    _logger,
                    maxIterations: 5,
                    modelBridge: bridge,
                    systemMessage: checkerSystemMessage);

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
            string? taskType = null,
            int? currentStepNumber = null,
            Models.StepTemplate? stepTemplate = null)
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

            // VALIDAZIONE MINIMO CARATTERI TRAMA
            if (currentStepNumber.HasValue && stepTemplate != null && stepTemplate.MinCharsTrama.HasValue && stepTemplate.MinCharsTrama > 0)
            {
                if (stepTemplate.ParsedTramaSteps.Contains(currentStepNumber.Value))
                {
                    if (modelOutput.Length < stepTemplate.MinCharsTrama.Value)
                    {
                        _logger.Log("Warning", "MultiStep", 
                            $"Step {currentStepNumber} output too short for trama: {modelOutput.Length} chars < {stepTemplate.MinCharsTrama} required");
                        return new ValidationResult
                        {
                            IsValid = false,
                            Reason = $"La trama non ha abbastanza caratteri. Richiesti: {stepTemplate.MinCharsTrama} caratteri, ottenuti: {modelOutput.Length}. Scrivi una trama più completa.",
                            NeedsRetry = true,
                            SemanticScore = null
                        };
                    }
                    else
                    {
                        _logger.Log("Information", "MultiStep",
                            $"Step {currentStepNumber} trama validation passed: {modelOutput.Length} chars >= {stepTemplate.MinCharsTrama} required");
                    }
                }
            }

            // VALIDAZIONE MINIMO CARATTERI STORIA COMPLETA (FullStoryStep + MinCharsStory)
            if (currentStepNumber.HasValue && stepTemplate != null && stepTemplate.FullStoryStep.HasValue && 
                currentStepNumber == stepTemplate.FullStoryStep && 
                stepTemplate.MinCharsStory.HasValue && stepTemplate.MinCharsStory > 0)
            {
                if (modelOutput.Length < stepTemplate.MinCharsStory.Value)
                {
                    _logger.Log("Warning", "MultiStep", 
                        $"Step {currentStepNumber} output too short for full story: {modelOutput.Length} chars < {stepTemplate.MinCharsStory} required");
                    return new ValidationResult
                    {
                        IsValid = false,
                        Reason = $"La storia è troppo breve. Richiesti: {stepTemplate.MinCharsStory} caratteri, ottenuti: {modelOutput.Length}. Scrivi una storia più completa con più dettagli.",
                        NeedsRetry = true,
                        SemanticScore = null
                    };
                }
                else
                {
                    _logger.Log("Information", "MultiStep",
                        $"Step {currentStepNumber} full story validation passed: {modelOutput.Length} chars >= {stepTemplate.MinCharsStory} required");
                }
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
                .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                    a.Role.Equals("response_checker", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

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

            // IMPORTANT: when invoking response_checker, append response-format instructions to the SYSTEM message.
            var checkerSystemSb = new StringBuilder();
            // Include agent-level prompt/instructions if present
            if (!string.IsNullOrWhiteSpace(checkerAgent.Prompt))
            {
                checkerSystemSb.AppendLine("=== Agent Prompt ===");
                checkerSystemSb.AppendLine(checkerAgent.Prompt);
                checkerSystemSb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(checkerAgent.Instructions))
            {
                checkerSystemSb.AppendLine("=== Agent Instructions ===");
                checkerSystemSb.AppendLine(checkerAgent.Instructions);
                checkerSystemSb.AppendLine();
            }

            checkerSystemSb.AppendLine("You are a response_checker helping another agent that MUST call functions instead of replying with free text.");
            checkerSystemSb.AppendLine($"Attempt: {attempt + 1}/2.");
            checkerSystemSb.AppendLine("Provide a SHORT assistant reply to the primary model that reminds it to use the tools, and if possible suggest the first tool to call.");
            checkerSystemSb.AppendLine("Return ONLY the assistant reply text, no JSON, no extra explanations.");

            var checkerUserSb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(systemMessage))
            {
                checkerUserSb.AppendLine("=== System message ===");
                checkerUserSb.AppendLine(systemMessage);
                checkerUserSb.AppendLine();
            }
            checkerUserSb.AppendLine("=== User prompt ===");
            checkerUserSb.AppendLine(userPrompt);
            checkerUserSb.AppendLine();
            checkerUserSb.AppendLine("=== Model response (incorrect - no tool calls) ===");
            checkerUserSb.AppendLine(modelResponse);
            checkerUserSb.AppendLine();
            checkerUserSb.AppendLine("=== Available tools ===");
            foreach (var t in toolNames)
            {
                checkerUserSb.AppendLine($"- {t}");
            }

            try
            {
                var modelInfo = _database.GetModelInfoById(checkerAgent.ModelId ?? 0);
                var checkerModelName = modelInfo?.Name;
                if (string.IsNullOrWhiteSpace(checkerModelName))
                {
                    _logger.Log("Warning", "ResponseChecker", $"Response checker agent \"{checkerAgent.Name}\" has no model configured.");
                    return null;
                }

                // Push a scope with "Response Checker" as agent name so logs show correctly in ChatLog
                using var checkerScope = LogScope.Push(
                    "response_checker_tool_reminder",
                    null,
                    LogScope.CurrentStepNumber,
                    LogScope.CurrentMaxStep,
                    "Response Checker",
                    agentRole: "response_checker");

                var orchestrator = _kernelFactory.CreateOrchestrator(checkerModelName, new List<string>());
                var bridge = _kernelFactory.CreateChatBridge(checkerModelName);
                var loop = new ReActLoopOrchestrator(
                    orchestrator,
                    _logger,
                    maxIterations: 3,
                    modelBridge: bridge,
                    systemMessage: checkerSystemSb.ToString());

                var result = await loop.ExecuteAsync(checkerUserSb.ToString());
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
                // Preferred: TAG output parsing.
                if (BracketTagParser.TryGetTagContent(response, "IS_VALID", out var isValidText))
                {
                    var isValidTag = BracketTagParser.TryParseBool(isValidText, out var parsedValid) && parsedValid;

                    var needsRetry = !isValidTag;
                    if (BracketTagParser.TryGetTagContent(response, "NEEDS_RETRY", out var needsRetryText)
                        && BracketTagParser.TryParseBool(needsRetryText, out var parsedRetry))
                    {
                        needsRetry = parsedRetry;
                    }

                    var reasonTag = BracketTagParser.GetTagContentOrNull(response, "REASON") ?? "Validation completed";
                    var violated = BracketTagParser.ParseIntList(BracketTagParser.GetTagContentOrNull(response, "VIOLATED_RULES"));
                    var violatedRules = violated.Count > 0 ? violated.ToList() : null;

                    return new ValidationResult
                    {
                        IsValid = isValidTag,
                        Reason = reasonTag,
                        NeedsRetry = needsRetry,
                        SemanticScore = semanticScore,
                        ViolatedRules = violatedRules
                    };
                }

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
                    List<int>? violatedRules = null;

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

                    if (root.TryGetProperty("violated_rules", out var violatedProp) && violatedProp.ValueKind == JsonValueKind.Array)
                    {
                        try
                        {
                            var tmp = new List<int>();
                            foreach (var item in violatedProp.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
                                    tmp.Add(n);
                                else if (item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out var ns))
                                    tmp.Add(ns);
                            }
                            if (tmp.Count > 0) violatedRules = tmp;
                        }
                        catch
                        {
                            // best-effort
                        }
                    }

                    return new ValidationResult
                    {
                        IsValid = validFlag,
                        Reason = validReason ?? "Validation completed",
                        NeedsRetry = retryFlag ?? !validFlag,
                        SemanticScore = semanticScore
                        ,
                        ViolatedRules = violatedRules
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

        /// <summary>
        /// Generic rule-based validation via the response_checker agent.
        /// Designed for central interception of agent responses.
        /// </summary>
        public async Task<ValidationResult> ValidateGenericResponseAsync(
            string instruction,
            string modelOutput,
            IReadOnlyList<ResponseValidationRule> rules,
            string? agentName = null,
            string? modelName = null,
            CancellationToken ct = default)
        {
            var checkerAgent = _database.ListAgents()
                .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                    a.Role.Equals("response_checker", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (checkerAgent == null)
            {
                return new ValidationResult
                {
                    IsValid = true,
                    Reason = "No response_checker agent configured; skipping LLM validation.",
                    NeedsRetry = false
                };
            }

            var modelInfo = _database.GetModelInfoById(checkerAgent.ModelId ?? 0);
            var checkerModelName = modelInfo?.Name;
            if (string.IsNullOrWhiteSpace(checkerModelName))
            {
                return new ValidationResult
                {
                    IsValid = true,
                    Reason = "Response checker has no model configured; skipping LLM validation.",
                    NeedsRetry = false
                };
            }

            // IMPORTANT: when invoking response_checker, append response-format instructions to the SYSTEM message.
            var checkerSystemSb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(checkerAgent.Prompt))
            {
                checkerSystemSb.AppendLine("=== Agent Prompt ===");
                checkerSystemSb.AppendLine(checkerAgent.Prompt);
                checkerSystemSb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(checkerAgent.Instructions))
            {
                checkerSystemSb.AppendLine("=== Agent Instructions ===");
                checkerSystemSb.AppendLine(checkerAgent.Instructions);
                checkerSystemSb.AppendLine();
            }

            checkerSystemSb.AppendLine("Sei un Response Checker. Devi validare l'output di un agente rispetto alle regole.");
            if (!string.IsNullOrWhiteSpace(agentName) || !string.IsNullOrWhiteSpace(modelName))
            {
                checkerSystemSb.AppendLine($"Agente: {agentName ?? "(unknown)"} | Modello: {modelName ?? "(unknown)"}");
            }
            checkerSystemSb.AppendLine();
            checkerSystemSb.AppendLine("=== OUTPUT RICHIESTO (OBBLIGATORIO) ===");
            checkerSystemSb.AppendLine("Restituisci SOLO TAG tra parentesi quadre (no JSON) con questa struttura:");
            checkerSystemSb.AppendLine("[IS_VALID]true|false");
            checkerSystemSb.AppendLine("[NEEDS_RETRY]true|false");
            checkerSystemSb.AppendLine("[REASON]Spiega brevemente. Se non valido, cita almeno una REGOLA n violata.");
            checkerSystemSb.AppendLine("[VIOLATED_RULES]1,2  // vuoto se valido");

            var checkerUserSb = new StringBuilder();
            checkerUserSb.AppendLine("=== ISTRUZIONE / CONTESTO ===");
            checkerUserSb.AppendLine(instruction);
            checkerUserSb.AppendLine();
            checkerUserSb.AppendLine("=== OUTPUT DELL'AGENTE ===");
            checkerUserSb.AppendLine(modelOutput);
            checkerUserSb.AppendLine();
            checkerUserSb.AppendLine("=== REGOLE ===");
            if (rules != null && rules.Count > 0)
            {
                foreach (var r in rules.OrderBy(r => r.Id))
                {
                    checkerUserSb.AppendLine($"REGOLA {r.Id}: {r.Text}");
                }
            }
            else
            {
                checkerUserSb.AppendLine("(Nessuna regola configurata)");
            }

            try
            {
                // Keep the existing operation scope so the checker request/response is visible
                // in the same log thread, but override agent identity to avoid confusion.
                var parentScope = LogScope.Current ?? "response_checker_generic_validation";
                using var checkerScope = LogScope.Push(
                    parentScope,
                    null,
                    LogScope.CurrentStepNumber,
                    LogScope.CurrentMaxStep,
                    "Response Checker",
                    agentRole: "response_checker");

                var orchestrator = _kernelFactory.CreateOrchestrator(checkerModelName, new List<string>());
                var bridge = _kernelFactory.CreateChatBridge(checkerModelName);
                var loop = new ReActLoopOrchestrator(
                    orchestrator,
                    _logger,
                    maxIterations: 5,
                    modelBridge: bridge,
                    systemMessage: checkerSystemSb.ToString());
                var result = await loop.ExecuteAsync(checkerUserSb.ToString(), ct);

                _logger.Log("Information", "ResponseChecker", $"Checker raw response (generic): {result.FinalResponse}");
                return ParseValidationResponse(result.FinalResponse, semanticScore: null);
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "ResponseChecker", $"Generic checker invocation failed: {ex.Message}", ex.ToString());
                return new ValidationResult
                {
                    IsValid = false,
                    Reason = $"Validation error: {ex.Message}",
                    NeedsRetry = true
                };
            }
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
                        continue;
                    }

                    // Fallback: allow a "gappy" in-order match (dialogue with inserted attributions in the original).
                    // If we can match all words in order, remove the whole span (including the gaps) from remaining.
                    if (TryRemoveInOrderSpanMatch(ref remainingText, normalizedExtracted))
                    {
                        continue;
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

            // Some stored chunks contain literal escape sequences (e.g. "\\n") rather than real newlines.
            // Treat them as whitespace to keep coverage matching stable.
            text = text.Replace("\\r", " ", StringComparison.Ordinal)
                       .Replace("\\n", " ", StringComparison.Ordinal)
                       .Replace("\\t", " ", StringComparison.Ordinal);

            // Keep only letters/digits/whitespace to be robust against Unicode punctuation
            // (curly quotes, guillemets, em-dash, smart apostrophes, etc.).
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append(' ');
                }
            }

            var normalized = sb.ToString();
            // Collapse multiple spaces
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");
            return normalized.Trim().ToLowerInvariant();
        }

        private static bool TryRemoveInOrderSpanMatch(ref string remainingText, string normalizedExtracted)
        {
            if (string.IsNullOrWhiteSpace(remainingText)) return false;
            if (string.IsNullOrWhiteSpace(normalizedExtracted)) return false;

            var remainingTokens = remainingText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var extractedTokens = normalizedExtracted.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (remainingTokens.Length < 2 || extractedTokens.Length < 2) return false;

            const int maxGapWords = 10;

            for (int start = 0; start < remainingTokens.Length; start++)
            {
                if (!string.Equals(remainingTokens[start], extractedTokens[0], StringComparison.OrdinalIgnoreCase))
                    continue;

                int currentIndex = start;
                bool ok = true;

                for (int i = 1; i < extractedTokens.Length; i++)
                {
                    var target = extractedTokens[i];
                    int searchFrom = currentIndex + 1;
                    int searchToExclusive = Math.Min(remainingTokens.Length, searchFrom + maxGapWords + 1);

                    int found = -1;
                    for (int j = searchFrom; j < searchToExclusive; j++)
                    {
                        if (string.Equals(remainingTokens[j], target, StringComparison.OrdinalIgnoreCase))
                        {
                            found = j;
                            break;
                        }
                    }

                    if (found < 0)
                    {
                        ok = false;
                        break;
                    }

                    currentIndex = found;
                }

                if (!ok) continue;

                // Remove the entire span (including any gap words) between the first and last matched tokens.
                var newTokens = new List<string>(remainingTokens.Length);
                for (int i = 0; i < remainingTokens.Length; i++)
                {
                    if (i < start || i > currentIndex)
                    {
                        newTokens.Add(remainingTokens[i]);
                    }
                }

                remainingText = string.Join(' ', newTokens);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Detect if the step instruction is for a plot/outline (trama) step.
        /// </summary>
        private bool IsPlotOutlineStep(string stepInstruction)
        {
            if (string.IsNullOrWhiteSpace(stepInstruction)) return false;

            var lowerInstruction = stepInstruction.ToLowerInvariant();
            var plotKeywords = new[] { "trama", "plot", "outline", "story outline", "schema narrativo", "struttura della storia" };

            return plotKeywords.Any(kw => lowerInstruction.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }
    }
}
