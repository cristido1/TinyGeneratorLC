using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Services
{
    /// <summary>
    /// ReAct (Reasoning + Acting) loop orchestrator for LangChain.
    /// Replaces Semantic Kernel's AutoInvokeKernelFunctions with explicit control flow.
    /// 
    /// Pattern:
    ///   1. Send user prompt + tool schema to model
    ///   2. Parse model response for tool calls
    ///   3. Execute tools and collect results
    ///   4. Send results back to model as tool_result messages
    ///   5. Repeat until model signals completion (no tool calls OR stop_reason != tool_calls)
    /// </summary>
    public class ReActLoopOrchestrator
    {
        // Tracks retry attempts for tools that returned validation errors
        private readonly Dictionary<string, int> _toolRetryCounts = new(StringComparer.OrdinalIgnoreCase);
        // Track how many times we've asked the model to call an expected final function
        private int _missingFinalCallAttempts = 0;
        // Warnings when evaluator calls evaluate_full_story before all parts have been requested
        private int _evaluatorMissingPartsWarnings = 0;
        private const int MaxEvaluatorMissingPartsWarnings = 3;
        // Limit overall function calls to prevent infinite loops
        private const int MaxFunctionCalls = 200;
        private int _functionCallCount = 0;
        // Track repeated requests for the same part index
        private const int MaxRepeatedPartRequests = 3;
        private readonly Dictionary<int, int> _partRequestCounts = new();

        private void OnFunctionCalled(string functionName, string? argumentsJson)
        {
            _functionCallCount++;
            if (_functionCallCount > MaxFunctionCalls)
            {
                throw new InvalidOperationException($"Exceeded maximum function call limit ({MaxFunctionCalls}). Aborting to prevent infinite loop.");
            }

            if (string.Equals(functionName, "read_story_part", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(argumentsJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(argumentsJson);
                    var root = doc.RootElement;
                    int partIndex = -1;
                    if (root.TryGetProperty("part_index", out var idx) && idx.ValueKind == JsonValueKind.Number && idx.TryGetInt32(out var v))
                        partIndex = v;
                    else if (root.TryGetProperty("partIndex", out var idx2) && idx2.ValueKind == JsonValueKind.Number && idx2.TryGetInt32(out var v2))
                        partIndex = v2;

                    if (partIndex >= 0)
                    {
                        _partRequestCounts.TryGetValue(partIndex, out var attempts);
                        attempts++;
                        _partRequestCounts[partIndex] = attempts;
                        if (attempts > MaxRepeatedPartRequests)
                        {
                            throw new InvalidOperationException($"Part {partIndex} requested too many times ({attempts}). Aborting to prevent infinite loop.");
                        }
                    }
                }
                catch (JsonException) { /* ignore parse errors */ }
            }
        }

        private readonly HybridLangChainOrchestrator _tools;
        private readonly ICustomLogger? _logger;
        private readonly ProgressService? _progress;
        private readonly string? _runId;
        private readonly int _maxIterations;
        private readonly LangChainChatBridge? _modelBridge;
        private readonly string? _systemMessage;
        private readonly ResponseCheckerService? _responseChecker;
        private readonly string? _agentRole;
        private readonly List<ConversationMessage>? _initialExtraMessages;
        private List<ConversationMessage> _messageHistory;
        private int _toolReminderAttempts = 0;

        public ReActLoopOrchestrator(
            HybridLangChainOrchestrator tools,
            ICustomLogger? logger = null,
            int maxIterations = 100,
            ProgressService? progress = null,
            string? runId = null,
            LangChainChatBridge? modelBridge = null,
            string? systemMessage = null,
            ResponseCheckerService? responseChecker = null,
            string? agentRole = null,
            List<ConversationMessage>? extraMessages = null)
        {
            _tools = tools;
            _logger = logger;
            _progress = progress;
            _runId = runId;
            _maxIterations = maxIterations;
            _modelBridge = modelBridge;
            _systemMessage = systemMessage;
            _responseChecker = responseChecker;
            _agentRole = agentRole;
            _initialExtraMessages = extraMessages;
            _messageHistory = new List<ConversationMessage>();
        }

        public class ReActResult
        {
            public string FinalResponse { get; set; } = string.Empty;
            public int IterationCount { get; set; }
            public bool Success { get; set; }
            public string? Error { get; set; }
            public List<ToolExecutionRecord> ExecutedTools { get; set; } = new();
        }

        public class ToolExecutionRecord
        {
            public string ToolName { get; set; } = string.Empty;
            public string Input { get; set; } = string.Empty;
            public string Output { get; set; } = string.Empty;
            public int IterationNumber { get; set; }
        }

        /// <summary>
        /// Execute a ReAct loop: send prompt to model, parse tool calls, execute tools, repeat until done.
        /// For this version, we use a mock model response simulator since LangChain C# doesn't have full integration yet.
        /// In production, replace ModelResponseSimulator with actual LangChain chat call.
        /// </summary>
        public async Task<ReActResult> ExecuteAsync(string userPrompt, CancellationToken ct = default)
        {
            Console.WriteLine($"[DEBUG ReActLoop] ExecuteAsync called - modelBridge is null: {_modelBridge == null}");
            
            var result = new ReActResult();
            _messageHistory.Clear();
            
            // Add system message first if provided
            if (!string.IsNullOrWhiteSpace(_systemMessage))
            {
                _messageHistory.Add(new ConversationMessage { Role = "system", Content = _systemMessage });
                _logger?.Log("Info", "ReActLoop", $"Added system message (length={_systemMessage.Length})");
                Console.WriteLine($"[DEBUG ReActLoop] Added system message (length={_systemMessage.Length})");
            }
            // Add any initial assistant/system messages provided by the caller (e.g., validation feedback)
            if (_initialExtraMessages != null && _initialExtraMessages.Count > 0)
            {
                foreach (var m in _initialExtraMessages)
                {
                    _messageHistory.Add(m);
                    _logger?.Log("Info", "ReActLoop", $"Injected initial message role={m.Role} len={m.Content?.Length ?? 0}");
                }
            }
            
            _messageHistory.Add(new ConversationMessage { Role = "user", Content = userPrompt });

            _logger?.Log("Info", "ReActLoop", $"Starting ReAct loop with prompt length={userPrompt.Length}");
            Console.WriteLine($"[DEBUG ReActLoop] Starting ReAct loop with prompt length={userPrompt.Length}");

            // Main loop: reasoning → tool call → execution → feedback
            var unlimited = _maxIterations <= 0;
            int iteration = 0;
            while (unlimited || iteration < _maxIterations)
            {
                if (ct.IsCancellationRequested)
                {
                    result.Success = false;
                    result.Error = "Cancelled by user";
                    result.IterationCount = iteration + 1;
                    LogFinalOutcome(result);
                    return result;
                }

                _logger?.Log("Info", "ReActLoop", $"Iteration {iteration + 1}/{(unlimited ? "∞" : _maxIterations.ToString())}");

                try
                {
                    // Step 1: Call model with tool definitions
                    Console.WriteLine($"[DEBUG ReActLoop] Iteration {iteration + 1}: About to call CallModelAsync");
                    var modelResponse = await CallModelAsync(userPrompt, iteration);
                    Console.WriteLine($"[DEBUG ReActLoop] Iteration {iteration + 1}: CallModelAsync returned, response length: {modelResponse?.Length ?? 0}");
                    
                    if (string.IsNullOrEmpty(modelResponse))
                    {
                        Console.WriteLine($"[DEBUG ReActLoop] modelResponse is null or empty!");
                        result.FinalResponse = "No response from model";
                        result.Success = true;
                        result.IterationCount = iteration + 1;
                        LogFinalOutcome(result);
                        return result;
                    }

                    // Step 2: Parse tool calls from response
                    Console.WriteLine($"[DEBUG ReActLoop] About to parse tool calls from response");
                    var toolCalls = _tools.ParseToolCalls(modelResponse);
                    Console.WriteLine($"[DEBUG ReActLoop] ParseToolCalls returned {toolCalls.Count} tool calls");

                    if (toolCalls.Count == 0)
                    {
                        // No tool calls = model might be done — but sometimes the model skipped calling an
                        // expected final function (e.g. evaluate_full_story or finalize_global_coherence). In
                        // that case we should prompt it up to 3 times to call the function instead of silently
                        // accepting completion.

                        var finalFunctionCandidates = new[] { "evaluate_full_story", "finalize_global_coherence" };

                        bool expectsFinalFunction = _tools.GetToolSchemas()
                            .Any(s => s.TryGetValue("function", out var funcObj)
                                && funcObj is Dictionary<string, object> fd
                                && fd.TryGetValue("name", out var fname)
                                && finalFunctionCandidates.Any(c => string.Equals(fname?.ToString(), c, StringComparison.OrdinalIgnoreCase)));

                        var missingFinal = finalFunctionCandidates
                            .FirstOrDefault(fn => _tools.GetToolSchemas()
                                .Any(s => s.TryGetValue("function", out var funcObj)
                                    && funcObj is Dictionary<string, object> fd
                                    && fd.TryGetValue("name", out var fname)
                                    && string.Equals(fname?.ToString(), fn, StringComparison.OrdinalIgnoreCase))
                                && !result.ExecutedTools.Any(r => string.Equals(r.ToolName, fn, StringComparison.OrdinalIgnoreCase)));

                        if (expectsFinalFunction && !string.IsNullOrEmpty(missingFinal))
                        {
                            // Ask model to call the function up to 3 times
                            if (_missingFinalCallAttempts < 3)
                            {
                                _missingFinalCallAttempts++;
                                var assistantPrompt = missingFinal.Equals("evaluate_full_story", StringComparison.OrdinalIgnoreCase)
                                    ? $"You have not called the required function `evaluate_full_story`. Please call `evaluate_full_story` exactly once now, including all required score fields and corresponding *_defects. Retry attempt {_missingFinalCallAttempts} of 3."
                                    : $"You have not called the required function `finalize_global_coherence`. Please call `finalize_global_coherence` exactly once now with the final global coherence score. Retry attempt {_missingFinalCallAttempts} of 3.";

                                _messageHistory.Add(new ConversationMessage
                                {
                                    Role = "assistant",
                                    Content = assistantPrompt
                                });

                                _logger?.Log("Info", "ReActLoop", $"Requested missing final function {missingFinal} retry {_missingFinalCallAttempts}/3");

                                // Continue to next iteration so the model is asked again
                                iteration++;
                                continue;
                            }

                            // Exceeded retries
                            result.Success = false;
                            result.Error = $"Model failed to call {missingFinal} after 3 attempts (required final function never returned)";
                            result.IterationCount = iteration + 1;
                            _logger?.Log("Warn", "ReActLoop", $"Exceeded retry attempts for missing {missingFinal}: final function not returned");
                            LogFinalOutcome(result);
                            return result;
                        }

                        // Guardrail: if we have tools and a response_checker, ask it to craft a reminder (max 2 attempts)
                        var toolSchemas = _tools.GetToolSchemas();
                        if (toolSchemas.Any() && _responseChecker != null && _toolReminderAttempts < 2)
                        {
                            try
                            {
                                var reminderObj = await _responseChecker.BuildToolUseReminderAsync(
                                    _systemMessage,
                                    userPrompt,
                                    modelResponse,
                                    toolSchemas,
                                    _toolReminderAttempts);

                                if (reminderObj != null)
                                {
                                    _toolReminderAttempts++;
                                    _messageHistory.Add(new ConversationMessage
                                    {
                                        Role = reminderObj.Value.role,
                                        Content = reminderObj.Value.content
                                    });

                                    _logger?.Log("Info", "ReActLoop", $"response_checker injected reminder (role={reminderObj.Value.role}) attempt {_toolReminderAttempts}/2");
                                    iteration++;
                                    continue;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.Log("Warn", "ReActLoop", $"response_checker reminder failed: {ex.Message}");
                            }
                        }

                        // No expected final function or we already got it — accept plain response
                        Console.WriteLine($"[DEBUG ReActLoop] No tool calls, extracting plain text response");
                        result.FinalResponse = ExtractPlainTextResponse(modelResponse);
                        Console.WriteLine($"[DEBUG ReActLoop] Extracted FinalResponse length: {result.FinalResponse.Length}");
                        result.Success = true;
                        result.IterationCount = iteration + 1;
                        _logger?.Log("Info", "ReActLoop", "Model finished (no tool calls)");
                        LogFinalOutcome(result);
                        return result;
                    }

                    // Step 3: Execute all tools
                    var toolResults = new List<(string callId, string result)>();
                    foreach (var call in toolCalls)
                    {
                        try
                        {
                            // Track function call counts and detect potential loops
                                OnFunctionCalled(call.ToolName ?? string.Empty, call.Arguments);

                                _logger?.Log("Info", "ReActLoop", $"  Executing tool: {call.ToolName}");
                                _progress?.Append(_runId ?? string.Empty, $"  📞 Tool: {call.ToolName}");
                            
                                var output = await _tools.ExecuteToolAsync(call.ToolName ?? string.Empty, call.Arguments ?? string.Empty);
                            toolResults.Add((call.Id, output));
                            // Log tool output as a pseudo-model response so chat_text shows the function result
                            try
                            {
                                var toolResponseJson = JsonSerializer.Serialize(new
                                {
                                    message = new
                                    {
                                        role = "tool",
                                        content = output ?? string.Empty
                                    }
                                });
                                _logger?.LogResponseJson(call.ToolName ?? "tool", toolResponseJson);
                            }
                            catch { }
                            
                            // Log full output for better visibility. Be careful: outputs can be large.
                            // If you prefer truncation, change to substring only for specific tools.
                            // Log only length/preview to avoid huge log entries
                            try
                            {
                                var outLen = output?.Length ?? 0;
                                var preview = outLen > 200 ? (output?.Substring(0, 200) + "...") : output;
                                _logger?.Log("Info", "ReActLoop", $"  Tool {call.ToolName} result length={outLen}");
                                _progress?.Append(_runId ?? string.Empty, $"  ✓ {call.ToolName} output length: {outLen}");
                                // Also log small preview for quick debugging
                                if (outLen > 0 && outLen <= 500)
                                    _logger?.Log("Debug", "ReActLoop", $"  Tool {call.ToolName} preview: {preview}");
                            }
                            catch { }
                            
                            result.ExecutedTools.Add(new ToolExecutionRecord
                            {
                                ToolName = call.ToolName ?? string.Empty,
                                Input = call.Arguments ?? string.Empty,
                                Output = output ?? string.Empty,
                                IterationNumber = iteration + 1
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger?.Log("Error", "ReActLoop", $"  Tool execution failed: {ex.Message}");
                            _progress?.Append(_runId ?? string.Empty, $"  ✗ {call.ToolName} error: {ex.Message}");
                            
                            toolResults.Add((call.Id, JsonSerializer.Serialize(new { error = ex.Message })));
                        }
                    }

                    // After executing tools, check whether we've requested all parts for read_story_part
                    try
                    {
                        foreach (var (callId, toolResult) in toolResults)
                        {
                            var matchingCall = toolCalls.FirstOrDefault(tc => tc.Id == callId);
                            if (matchingCall == null)
                                continue;

                            // If the model called read_story_part repeatedly, check if the underlying tool
                            // believes it has served all parts. If so, nudge the model to call final function.
                            if (string.Equals(matchingCall.ToolName, "read_story_part", StringComparison.OrdinalIgnoreCase))
                            {
                                var toolInstance = _tools.GetToolByFunctionName("read_story_part");
                                if (toolInstance != null)
                                {
                                    // If the tool supports HasRequestedAllParts, call it dynamically
                                    try
                                    {
                                        // Use reflection to call HasRequestedAllParts if available
                                        try
                                        {
                                            var mi = toolInstance.GetType().GetMethod("HasRequestedAllParts");
                                            if (mi != null)
                                            {
                                                var hasAllObj = mi.Invoke(toolInstance, null);
                                                var hasAll = false;
                                                if (hasAllObj is bool b) hasAll = b;
                                                if (hasAll)
                                                {
                                                    // Determine appropriate final function to call
                                                    string finalFunc = "confirm";
                                                    if (toolInstance.GetType().Name.IndexOf("EvaluatorTool", StringComparison.OrdinalIgnoreCase) >= 0)
                                                        finalFunc = "evaluate_full_story";

                                                    var assistantPrompt = $"All parts of the story have been provided. Please call `{finalFunc}` now to finish the operation.";
                                                    _messageHistory.Add(new ConversationMessage { Role = "assistant", Content = assistantPrompt });
                                                    _logger?.Log("Info", "ReActLoop", "Requested final function call after all parts were read");
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }

                    // Step 4: Add assistant message with tool_calls and tool result messages
                    // Guardrail: ensure TTS schema tool covers current chunk before proceeding
                    try
                    {
                        var ttsTool = _tools.GetToolByFunctionName("ttsschema") as TinyGenerator.Skills.TtsSchemaTool;
                        // Current TtsSchemaTool implementation does not expose LastChunkIndex/HasCoveredCurrentChunk APIs.
                        // Skip the coverage guard when the methods/properties are not available.
                        if (ttsTool != null)
                        {
                            // no-op; keep placeholder for future coverage checks
                        }
                    }
                    catch { }

                    _messageHistory.Add(new ConversationMessage
                    {
                        Role = "assistant",
                        Content = modelResponse,
                        ToolCalls = toolCalls.Select(tc => new ToolCallFromModel
                        {
                            Id = tc.Id,
                            ToolName = tc.ToolName,
                            Arguments = tc.Arguments
                        }).ToList() // Store tool_calls for OpenAI format
                    });

                    // Diagnostic: log whether assistant content is empty and toolCalls count
                    try
                    {
                        var plain = ExtractPlainTextResponse(modelResponse);
                        _logger?.Log("Info", "ReActLoop", $"Assistant message added: plainContentLen={plain?.Length ?? 0}, toolCalls={toolCalls.Count}, rawResponseLen={modelResponse?.Length ?? 0}");
                        _progress?.Append(_runId ?? string.Empty, $"Assistant content length: {plain?.Length ?? 0}, toolCalls: {toolCalls.Count}");
                    }
                    catch { }

                    foreach (var (callId, toolResult) in toolResults)
                    {
                        _messageHistory.Add(new ConversationMessage
                        {
                            Role = "tool",
                            Content = toolResult,
                            ToolCallId = callId // Associate with the tool_call
                        });
                    }

                    // Inspect tool results for validation errors and request retries when appropriate
                    try
                    {
                        foreach (var (callId, toolResult) in toolResults)
                        {
                            // Find the matching tool call to get the tool name
                            var matchingCall = toolCalls.FirstOrDefault(tc => tc.Id == callId);
                            if (matchingCall == null)
                                continue;

                            var toolName = matchingCall.ToolName ?? string.Empty;

                            // Handle text coverage validation errors in confirm (TtsSchemaTool)
                            if (string.Equals(toolName, "confirm", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(toolResult);
                                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var errElem))
                                    {
                                        var errMsg = errElem.GetString() ?? string.Empty;
                                        
                                        // Check if this is a text coverage error
                                        if (errMsg.Contains("Text coverage", StringComparison.OrdinalIgnoreCase) && errMsg.Contains("attempts remaining", StringComparison.OrdinalIgnoreCase))
                                        {
                                            // Extract remaining attempts from error message
                                            var attemptsMatch = System.Text.RegularExpressions.Regex.Match(errMsg, @"Attempts remaining:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                                            int remainingAttempts = attemptsMatch.Success ? int.Parse(attemptsMatch.Groups[1].Value) : 0;

                                            if (remainingAttempts > 0)
                                            {
                                                // Still have attempts left, request retry
                                                var assistantPrompt = $"Coverage validation failed for TTS schema. {errMsg} Please add more narration or character dialogue to cover the remaining text and call `confirm` again.";

                                                _messageHistory.Add(new ConversationMessage
                                                {
                                                    Role = "assistant",
                                                    Content = assistantPrompt
                                                });

                                                _logger?.Log("Info", "ReActLoop", $"Requested TTS schema retry: {errMsg}");
                                                // Continue to next iteration to let model retry
                                                iteration++;
                                                continue;
                                            }
                                            else if (doc.RootElement.TryGetProperty("attempts_exhausted", out var exhausted) && exhausted.GetBoolean())
                                            {
                                                // Exceeded retries for text coverage
                                                var assistantPrompt = $"Maximum retry attempts exceeded for text coverage in TTS schema. {errMsg}";
                                                _messageHistory.Add(new ConversationMessage
                                                {
                                                    Role = "assistant",
                                                    Content = assistantPrompt
                                                });

                                                _logger?.Log("Warn", "ReActLoop", $"Exceeded retry limit for TTS schema text coverage");
                                                result.Success = false;
                                                result.Error = errMsg;
                                                result.IterationCount = iteration + 1;
                                                LogFinalOutcome(result);
                                                return result;
                                            }
                                        }
                                    }
                                }
                                catch { /* ignore parse errors and continue */ }
                            }

                            // Short-circuit when evaluation completed: if evaluate_full_story returned without error,
                            // stop the loop and avoid nudging the model to read more parts.
                            if (string.Equals(toolName, "evaluate_full_story", StringComparison.OrdinalIgnoreCase))
                            {
                                var evalHasError = false;
                                try
                                {
                                    using var doc = JsonDocument.Parse(toolResult);
                                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out _))
                                    {
                                        evalHasError = true;
                                    }
                                }
                                catch
                                {
                                    // If parsing fails, assume it's a valid payload and proceed to short-circuit
                                }

                                if (!evalHasError)
                                {
                                    result.FinalResponse = toolResult;
                                    result.Success = true;
                                    result.IterationCount = iteration + 1;
                                    _logger?.Log("Info", "ReActLoop", "evaluate_full_story completed; stopping loop without further read_story_part checks");
                                    LogFinalOutcome(result);
                                    return result;
                                }
                            }

                            // Only handle evaluate_full_story validation errors here (original logic)
                            if (!string.Equals(toolName, "evaluate_full_story", StringComparison.OrdinalIgnoreCase))
                                continue;

                            // If evaluator attempts to call evaluate_full_story before all parts have been requested,
                            // ask it to read the remaining parts. After MaxEvaluatorMissingPartsWarnings we abort.
                            try
                            {
                                var evalTool = _tools.GetToolByFunctionName("evaluate_full_story");
                                if (evalTool != null)
                                {
                                    var mi = evalTool.GetType().GetMethod("HasRequestedAllParts");
                                    if (mi != null)
                                    {
                                        var hasAllObj = mi.Invoke(evalTool, null);
                                        var hasAll = false;
                                        if (hasAllObj is bool b) hasAll = b;
                                        if (!hasAll)
                                        {
                                            _evaluatorMissingPartsWarnings++;
                                            if (_evaluatorMissingPartsWarnings < MaxEvaluatorMissingPartsWarnings)
                                            {
                                                var assistantPrompt = $"You attempted to evaluate the story before reading all parts. Please read the remaining parts using `read_story_part(part_index)` until the entire story has been provided, then call `evaluate_full_story` again. Warning {_evaluatorMissingPartsWarnings} of {MaxEvaluatorMissingPartsWarnings}.";
                                                _messageHistory.Add(new ConversationMessage { Role = "assistant", Content = assistantPrompt });
                                                _logger?.Log("Info", "ReActLoop", "Requested evaluator to read remaining parts before evaluation");
                                                // ask model to retry by continuing loop
                                                iteration++;
                                                continue;
                                            }
                                            else
                                            {
                                                var assistantPrompt = $"Maximum warnings reached: evaluator attempted to complete evaluation without reading all parts. Aborting.";
                                                _messageHistory.Add(new ConversationMessage { Role = "assistant", Content = assistantPrompt });
                                                _logger?.Log("Warn", "ReActLoop", "Evaluator attempted evaluation without reading all parts - aborting");
                                                result.Success = false;
                                                result.Error = "Evaluator attempted evaluation without reading all parts after multiple warnings";
                                                result.IterationCount = iteration + 1;
                                                LogFinalOutcome(result);
                                                return result;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { /* ignore reflection errors and proceed to field validation */ }

                            // Parse result JSON to look for an "error" property indicating missing fields
                            try
                            {
                                using var doc = JsonDocument.Parse(toolResult);
                                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var errElem))
                                {
                                    var errMsg = errElem.GetString() ?? string.Empty;
                                    if (errMsg.Contains("Missing or invalid fields", StringComparison.OrdinalIgnoreCase))
                                    {
                                        // Extract missing fields list if present after colon
                                        var parts = errMsg.Split(new[] { ':' }, 2);
                                        var missing = parts.Length > 1 ? parts[1].Trim() : "(unspecified)";

                                        // Check retry count
                                        _toolRetryCounts.TryGetValue(toolName, out var attempts);
                                        if (attempts < 3)
                                        {
                                            attempts++;
                                            _toolRetryCounts[toolName] = attempts;

                                            var assistantPrompt = $"Required fields missing for `{toolName}`: {missing}. Please call the `{toolName}` function again including these fields. Retry attempt {attempts} of 3.";

                                            _messageHistory.Add(new ConversationMessage
                                            {
                                                Role = "assistant",
                                                Content = assistantPrompt
                                            });

                                            _logger?.Log("Info", "ReActLoop", $"Requested retry {attempts}/3 for {toolName}: {missing}");
                                        }
                                        else
                                        {
                                            // Exceeded retries: record error and stop loop
                                            var assistantPrompt = $"Maximum retries reached for `{toolName}`. Required fields were not provided: {missing}. Aborting.";
                                            _messageHistory.Add(new ConversationMessage
                                            {
                                                Role = "assistant",
                                                Content = assistantPrompt
                                            });

                                            _logger?.Log("Warn", "ReActLoop", $"Exceeded retry limit for {toolName}");
                                            result.Success = false;
                                            result.Error = $"Missing required fields for {toolName} after 3 attempts: {missing}";
                                            result.IterationCount = iteration + 1;
                                            LogFinalOutcome(result);
                                            return result;
                                        }
                                    }
                                }
                            }
                            catch { /* ignore parse errors and continue */ }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Log("Warn", "ReActLoop", $"Error while handling tool validation retries: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Log("Error", "ReActLoop", $"Iteration {iteration + 1} failed: {ex.Message}", ex.ToString());
                    result.Success = false;
                    result.Error = ex.Message;
                    result.IterationCount = iteration + 1;
                    LogFinalOutcome(result);
                    return result;
                }

                iteration++;
            }

            // Reached max iterations
            result.Success = false;
            result.Error = $"Exceeded maximum iterations ({_maxIterations})";
            result.IterationCount = _maxIterations;
            LogFinalOutcome(result);
            return result;
        }

        /// <summary>
        /// Call model with current conversation and tool schema.
        /// If modelBridge is provided, calls actual model. Otherwise, returns mock for testing.
        /// </summary>
        private async Task<string> CallModelAsync(string userPrompt, int iteration)
        {
            Console.WriteLine($"[DEBUG CallModelAsync] Entry - _modelBridge is null: {_modelBridge == null}");
            
            // If no model bridge provided, return mock response
            if (_modelBridge == null)
            {
                Console.WriteLine($"[DEBUG CallModelAsync] No modelBridge, returning mock response");
                return await Task.FromResult(JsonSerializer.Serialize(new
                {
                    content = "Model would be called here with tool definitions and conversation history",
                    tool_calls = new[] {
                        new { function = new { name = "text", arguments = "{\"function\": \"toupper\", \"text\": \"hello\"}" } }
                    }
                }));
            }

            Console.WriteLine($"[DEBUG CallModelAsync] modelBridge available, proceeding with actual call");
            
            try
            {
                // Get tool schemas for the model
                var toolSchemas = _tools.GetToolSchemas();
                
                Console.WriteLine($"[DEBUG CallModelAsync] Got {toolSchemas.Count} tool schemas");
                _logger?.Log("Info", "ReActLoop", $"Iteration {iteration + 1}: Calling model with {toolSchemas.Count} tools available");
                
                // Log each tool schema for debugging
                foreach (var schema in toolSchemas)
                {
                    if (schema.TryGetValue("function", out var funcObj) && funcObj is Dictionary<string, object> func)
                    {
                        if (func.TryGetValue("name", out var name))
                        {
                            _logger?.Log("Info", "ReActLoop", $"  Available tool: {name}");
                        }
                    }
                }

                // Log the full request payload for debugging
                var requestData = new
                {
                    iteration = iteration + 1,
                    toolCount = toolSchemas.Count,
                    messageCount = _messageHistory.Count,
                    messages = _messageHistory.Select(m => new { role = m.Role, contentLength = m.Content.Length })
                };
                _logger?.Log("Info", "ReActLoop", $"Request data: {JsonSerializer.Serialize(requestData)}");
                Console.WriteLine($"[DEBUG CallModelAsync] Request data: {JsonSerializer.Serialize(requestData)}");

                // Call actual model via LangChainChatBridge
                Console.WriteLine($"[DEBUG CallModelAsync] About to call modelBridge.CallModelWithToolsAsync");
                var response = await _modelBridge.CallModelWithToolsAsync(
                    _messageHistory,
                    toolSchemas);
                Console.WriteLine($"[DEBUG CallModelAsync] modelBridge call completed, response length: {response?.Length ?? 0}");

                _logger?.Log("Info", "ReActLoop", $"Iteration {iteration + 1}: Model call succeeded");

                // Log full response for debugging
                _logger?.Log("Info", "ReActLoop", $"Response payload: {response}");
                Console.WriteLine($"[DEBUG CallModelAsync] Response payload (first 500 chars): {(response?.Length > 500 ? response.Substring(0, 500) : response)}");

                // Parse the response to extract tool calls
                var (textContent, toolCalls) = LangChainChatBridge.ParseChatResponse(response);
                Console.WriteLine($"[DEBUG CallModelAsync] Parsed response - textContent length: {textContent?.Length ?? 0}, toolCalls count: {toolCalls.Count}");

                _logger?.Log("Info", "ReActLoop", $"Iteration {iteration + 1}: Parsed {toolCalls.Count} tool calls from response");

                // Build response object with tool calls if present
                var responseObj = new Dictionary<string, object>();

                if (!string.IsNullOrWhiteSpace(textContent))
                {
                    responseObj["content"] = textContent;
                }

                if (toolCalls.Any())
                {
                    responseObj["tool_calls"] = toolCalls.Select(tc => new
                    {
                        id = tc.Id,
                        type = "function",
                        function = new { name = tc.ToolName, arguments = tc.Arguments }
                    }).ToList();
                }

                var finalResponse = JsonSerializer.Serialize(responseObj);
                Console.WriteLine($"[DEBUG CallModelAsync] Returning final response length: {finalResponse.Length}");
                return finalResponse;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG CallModelAsync] Exception caught: {ex.GetType().Name} - {ex.Message}");
                _logger?.Log("Error", "ReActLoop", $"Model call failed: {ex.Message}", ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Extract plain text response from model response JSON (if not tool calls).
        /// </summary>
        private string ExtractPlainTextResponse(string modelResponse)
        {
            try
            {
                var doc = JsonDocument.Parse(modelResponse);
                if (doc.RootElement.TryGetProperty("content", out var content))
                    return content.GetString() ?? modelResponse;
            }
            catch { }
            return modelResponse;
        }

        private void LogFinalOutcome(ReActResult result)
        {
            try
            {
                var level = result.Success ? "Info" : "Error";
                _logger?.Log(level, "ReActLoop", $"Final outcome: success={result.Success}, iterations={result.IterationCount}, error={result.Error}");
                _progress?.Append(_runId ?? string.Empty, $"Final: success={result.Success}, iterations={result.IterationCount}{(string.IsNullOrEmpty(result.Error) ? string.Empty : ", error=" + result.Error)}");
                Console.WriteLine($"[DEBUG ReActLoop] Final outcome: success={result.Success}, iterations={result.IterationCount}, error={result.Error}");
            }
            catch { }
        }

        /// <summary>
        /// Get message history for debugging/auditing.
        /// </summary>
        public List<ConversationMessage> GetMessageHistory() => _messageHistory;

        /// <summary>
        /// Clear message history for new conversation.
        /// </summary>
        public void ClearHistory()
        {
            _messageHistory.Clear();
        }
    }

    /// <summary>
    /// Represents a message in the conversation (compatible with OpenAI format).
    /// </summary>
    public class ConversationMessage
    {
        public string Role { get; set; } = string.Empty; // "user", "assistant", "tool"
        public string Content { get; set; } = string.Empty;
        public List<ToolCallFromModel>? ToolCalls { get; set; } // For assistant messages with tool_calls
        public string? ToolCallId { get; set; } // For tool messages, links to the tool_call id

        public override string ToString()
        {
            return JsonSerializer.Serialize(this);
        }
    }
}
