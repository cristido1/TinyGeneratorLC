using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            string? runId = null,
            LangChainChatBridge? modelBridge = null,
            string? systemMessage = null,
            ResponseCheckerService? responseChecker = null,
            string? agentRole = null,
            List<ConversationMessage>? extraMessages = null)
        {
            _tools = tools;
            _logger = logger;
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
            
            // Add any conversation history messages (e.g., previous response + validation feedback)
            // These come before the current user prompt so feedback can stay at the end.
            if (_initialExtraMessages != null && _initialExtraMessages.Count > 0)
            {
                foreach (var m in _initialExtraMessages)
                {
                    _messageHistory.Add(m);
                    _logger?.Log("Info", "ReActLoop", $"Added conversation history message role={m.Role} len={m.Content?.Length ?? 0}");
                }
            }

            // Add the user prompt only if it is not already present in the history.
            if (!string.IsNullOrWhiteSpace(userPrompt))
            {
                var hasUserInHistory = _initialExtraMessages != null
                    && _initialExtraMessages.Any(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));
                if (!hasUserInHistory)
                {
                    _messageHistory.Add(new ConversationMessage { Role = "user", Content = userPrompt });
                }
            }

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

                        var finalFunctionCandidates = new[] { "evaluate_full_story" };

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
                                var assistantPrompt = $"You have not called the required function `evaluate_full_story`. Please call `evaluate_full_story` exactly once now, including all required score fields and corresponding *_defects. Retry attempt {_missingFinalCallAttempts} of 3.";
                                if (missingFinal.Equals("evaluate_full_story", StringComparison.OrdinalIgnoreCase))
                                {
                                    var evalTool = _tools.GetToolByFunctionName("evaluate_full_story");
                                    var readAnyPart = false;
                                    var hasAllParts = false;
                                    var nextIndex = 0;
                                    try
                                    {
                                        var requestedPartsProp = evalTool?.GetType().GetProperty("RequestedParts");
                                        if (requestedPartsProp != null)
                                        {
                                            if (requestedPartsProp.GetValue(evalTool) is System.Collections.IEnumerable parts)
                                            {
                                                var indices = parts.Cast<object>()
                                                    .Select(p => Convert.ToInt32(p))
                                                    .ToList();
                                                if (indices.Count > 0)
                                                {
                                                    readAnyPart = true;
                                                    nextIndex = indices.Max() + 1;
                                                }
                                            }
                                        }

                                        var hasAllMethod = evalTool?.GetType().GetMethod("HasRequestedAllParts");
                                        if (hasAllMethod != null)
                                        {
                                            var hasAllObj = hasAllMethod.Invoke(evalTool, null);
                                            if (hasAllObj is bool b) hasAllParts = b;
                                        }
                                    }
                                    catch { }

                                    if (!readAnyPart)
                                    {
                                        assistantPrompt = "You have not read any story parts yet. Call `read_story_part` with part_index=0 to begin, then continue until you receive is_last=true.";
                                    }
                                    else if (!hasAllParts)
                                    {
                                        assistantPrompt = $"You have not read the full story yet. Call `read_story_part` with part_index={nextIndex} to continue, then proceed until is_last=true.";
                                    }
                                }

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
                                _logger?.Append(_runId ?? string.Empty, $"  📞 Tool: {call.ToolName}");
                            
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
                                _logger?.LogResponseJson(call.ToolName ?? "tool", toolResponseJson, null, LogScope.CurrentAgentName);
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
                                _logger?.Append(_runId ?? string.Empty, $"  ✓ {call.ToolName} output length: {outLen}");
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
                            _logger?.Append(_runId ?? string.Empty, $"  ✗ {call.ToolName} error: {ex.Message}");
                            
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
                        _logger?.Append(_runId ?? string.Empty, $"Assistant content length: {plain?.Length ?? 0}, toolCalls: {toolCalls.Count}");
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
                                                var instruction = $"Please read the remaining parts using `read_story_part(part_index)` until the entire story has been provided, then call `evaluate_full_story` again. Warning {_evaluatorMissingPartsWarnings} of {MaxEvaluatorMissingPartsWarnings}.";
                                                var reason = "Evaluation rejected: you attempted to evaluate the story before reading all parts.";
                                                _messageHistory.Add(new ConversationMessage { Role = "assistant", Content = instruction });
                                                _messageHistory.Add(new ConversationMessage { Role = "assistant", Content = reason });
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

                                            var instruction = $"Please call the `{toolName}` function again including the required fields. Retry attempt {attempts} of 3.";
                                            var reason = $"Evaluation rejected: required fields missing for `{toolName}`: {missing}.";

                                            _messageHistory.Add(new ConversationMessage
                                            {
                                                Role = "assistant",
                                                Content = instruction
                                            });
                                            _messageHistory.Add(new ConversationMessage
                                            {
                                                Role = "assistant",
                                                Content = reason
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

                // Parse the response to extract tool calls + finish reason
                var parsed = LangChainChatBridge.ParseChatResponseWithFinishReason(response);
                var textContent = parsed.TextContent;
                var toolCalls = parsed.ToolCalls;
                var finishReason = parsed.FinishReason;

                Console.WriteLine($"[DEBUG CallModelAsync] Parsed response - textContent length: {textContent?.Length ?? 0}, toolCalls count: {toolCalls.Count}, finishReason: {finishReason ?? "(null)"}");
                _logger?.Log("Info", "ReActLoop", $"Iteration {iteration + 1}: Parsed {toolCalls.Count} tool calls from response (finishReason={finishReason ?? "(null)"})");

                // If the model was cut off due to token limit (finish_reason=length) and it isn't emitting tool calls,
                // ask it to continue exactly from where it stopped. This should not be treated as an error.
                if (toolCalls.Count == 0 && string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    const int maxContinuations = 5;
                    var continuationPrompt = "Continue EXACTLY from the last written line, preserving all tags.\nThe next block must follow naturally from the previous one.";

                    var combined = textContent ?? string.Empty;
                    _logger?.Log("Info", "ReActLoop", $"finish_reason=length detected. Attempting up to {maxContinuations} continuation calls.");

                    for (var i = 0; i < maxContinuations; i++)
                    {
                        // Add the partial assistant message to the conversation, then ask to continue.
                        _messageHistory.Add(new ConversationMessage { Role = "assistant", Content = combined });
                        _messageHistory.Add(new ConversationMessage { Role = "user", Content = continuationPrompt });

                        var contResponse = await _modelBridge.CallModelWithToolsAsync(_messageHistory, toolSchemas);
                        var contParsed = LangChainChatBridge.ParseChatResponseWithFinishReason(contResponse);

                        // If tool calls appear in continuation, stop auto-continue and let normal flow handle next iteration.
                        if (contParsed.ToolCalls.Count > 0)
                        {
                            _logger?.Log("Info", "ReActLoop", $"Continuation returned tool calls ({contParsed.ToolCalls.Count}); stopping auto-continue.");
                            textContent = combined;
                            toolCalls = contParsed.ToolCalls;
                            finishReason = contParsed.FinishReason;
                            break;
                        }

                        var nextText = contParsed.TextContent ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(nextText))
                        {
                            _logger?.Log("Warning", "ReActLoop", "Continuation returned empty content; stopping auto-continue.");
                            break;
                        }

                        combined += nextText;
                        textContent = combined;
                        toolCalls = contParsed.ToolCalls;
                        finishReason = contParsed.FinishReason;

                        if (!string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger?.Log("Info", "ReActLoop", $"Continuation completed (finishReason={finishReason ?? "(null)"}).");
                            break;
                        }
                    }
                }

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
            string content;
            try
            {
                var doc = JsonDocument.Parse(modelResponse);
                if (doc.RootElement.TryGetProperty("content", out var contentProp))
                    content = contentProp.GetString() ?? modelResponse;
                else
                    content = modelResponse;
            }
            catch
            {
                content = modelResponse;
            }
            return SanitizeModelResponse(content);
        }

        /// <summary>
        /// Remove thinking/analysis tags from model responses.
        /// These are internal reasoning sections that should not appear in the final output.
        /// Also removes common retry apology patterns that pollute the output.
        /// </summary>
        private static string SanitizeModelResponse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return response;
            
            // Remove <think>...</think> sections
            response = Regex.Replace(response, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            // Remove <analysis>...</analysis> sections
            response = Regex.Replace(response, @"<analysis>.*?</analysis>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            // Remove <reasoning>...</reasoning> sections (common variant)
            response = Regex.Replace(response, @"<reasoning>.*?</reasoning>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            
            // Remove common retry apology patterns at the beginning of responses
            // These patterns appear when the model acknowledges a previous error
            var apologyPatterns = new[]
            {
                @"^[Ss]cusa[,.]?\s*(ho fatto un errore|mi sono sbagliato|per l['']errore)[^.]*\.\s*",
                @"^[Ss]orry[,.]?\s*(I made a mistake|for the error|I apologize)[^.]*\.\s*",
                @"^[Mm]i scuso[^.]*\.\s*",
                @"^[Hh]ai ragione[,.]?\s*[^.]*\.\s*",
                @"^[Yy]ou['']re right[,.]?\s*[^.]*\.\s*",
                @"^[Ee]cco (la versione corretta|una versione migliore|la correzione)[^:]*:\s*",
                @"^[Hh]ere['']?s? (the corrected|a better|the fixed) version[^:]*:\s*",
                @"^(Qui sotto|Di seguito|Ecco) (una versione migliore|la versione corretta)[^:]*:\s*"
            };
            
            foreach (var pattern in apologyPatterns)
            {
                response = Regex.Replace(response, pattern, "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }
            
            return response.Trim();
        }

        private void LogFinalOutcome(ReActResult result)
        {
            try
            {
                var level = result.Success ? "Info" : "Error";
                _logger?.Log(level, "ReActLoop", $"Final outcome: success={result.Success}, iterations={result.IterationCount}, error={result.Error}");
                _logger?.Append(_runId ?? string.Empty, $"Final: success={result.Success}, iterations={result.IterationCount}{(string.IsNullOrEmpty(result.Error) ? string.Empty : ", error=" + result.Error)}");
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
