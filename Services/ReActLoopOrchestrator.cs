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
        private readonly HybridLangChainOrchestrator _tools;
        private readonly ICustomLogger? _logger;
        private readonly ProgressService? _progress;
        private readonly string? _runId;
        private readonly int _maxIterations;
        private readonly LangChainChatBridge? _modelBridge;
        private readonly string? _systemMessage;
        private List<ConversationMessage> _messageHistory;

        public ReActLoopOrchestrator(
            HybridLangChainOrchestrator tools,
            ICustomLogger? logger = null,
            int maxIterations = 10,
            ProgressService? progress = null,
            string? runId = null,
            LangChainChatBridge? modelBridge = null,
            string? systemMessage = null)
        {
            _tools = tools;
            _logger = logger;
            _progress = progress;
            _runId = runId;
            _maxIterations = maxIterations;
            _modelBridge = modelBridge;
            _systemMessage = systemMessage;
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
            var result = new ReActResult();
            _messageHistory.Clear();
            
            // Add system message first if provided
            if (!string.IsNullOrWhiteSpace(_systemMessage))
            {
                _messageHistory.Add(new ConversationMessage { Role = "system", Content = _systemMessage });
                _logger?.Log("Info", "ReActLoop", $"Added system message (length={_systemMessage.Length})");
            }
            
            _messageHistory.Add(new ConversationMessage { Role = "user", Content = userPrompt });

            _logger?.Log("Info", "ReActLoop", $"Starting ReAct loop with prompt length={userPrompt.Length}");

            // Main loop: reasoning → tool call → execution → feedback
            for (int iteration = 0; iteration < _maxIterations; iteration++)
            {
                if (ct.IsCancellationRequested)
                {
                    result.Error = "Cancelled by user";
                    return result;
                }

                _logger?.Log("Info", "ReActLoop", $"Iteration {iteration + 1}/{_maxIterations}");

                try
                {
                    // Step 1: Call model with tool definitions
                    var modelResponse = await CallModelAsync(userPrompt, iteration);
                    
                    if (string.IsNullOrEmpty(modelResponse))
                    {
                        result.FinalResponse = "No response from model";
                        result.Success = true;
                        result.IterationCount = iteration + 1;
                        return result;
                    }

                    // Step 2: Parse tool calls from response
                    var toolCalls = _tools.ParseToolCalls(modelResponse);

                    if (toolCalls.Count == 0)
                    {
                        // No tool calls = model is done
                        result.FinalResponse = ExtractPlainTextResponse(modelResponse);
                        result.Success = true;
                        result.IterationCount = iteration + 1;
                        _logger?.Log("Info", "ReActLoop", "Model finished (no tool calls)");
                        return result;
                    }

                    // Step 3: Execute all tools
                    var toolResults = new List<(string callId, string result)>();
                    foreach (var call in toolCalls)
                    {
                        try
                        {
                            _logger?.Log("Info", "ReActLoop", $"  Executing tool: {call.ToolName}");
                            _progress?.Append(_runId, $"  📞 Tool: {call.ToolName}");
                            
                            var output = await _tools.ExecuteToolAsync(call.ToolName, call.Arguments);
                            toolResults.Add((call.Id, output));
                            
                            // Log full output for better visibility. Be careful: outputs can be large.
                            // If you prefer truncation, change to substring only for specific tools.
                            _logger?.Log("Info", "ReActLoop", $"  Tool {call.ToolName} result: {output}");
                            _progress?.Append(_runId, $"  ✓ {call.ToolName} output: {output}");
                            
                            result.ExecutedTools.Add(new ToolExecutionRecord
                            {
                                ToolName = call.ToolName,
                                Input = call.Arguments,
                                Output = output,
                                IterationNumber = iteration + 1
                            });
                        }
                        catch (Exception ex)
                        {
                            _logger?.Log("Error", "ReActLoop", $"  Tool execution failed: {ex.Message}");
                            _progress?.Append(_runId, $"  ✗ {call.ToolName} error: {ex.Message}");
                            
                            toolResults.Add((call.Id, JsonSerializer.Serialize(new { error = ex.Message })));
                        }
                    }

                    // Step 4: Add assistant message with tool_calls and tool result messages
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
                    
                    foreach (var (callId, toolResult) in toolResults)
                    {
                        _messageHistory.Add(new ConversationMessage 
                        { 
                            Role = "tool", 
                            Content = toolResult,
                            ToolCallId = callId // Associate with the tool_call
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Log("Error", "ReActLoop", $"Iteration {iteration + 1} failed: {ex.Message}", ex.ToString());
                    result.Error = ex.Message;
                    result.IterationCount = iteration + 1;
                    return result;
                }
            }

            // Reached max iterations
            result.Error = $"Exceeded maximum iterations ({_maxIterations})";
            result.IterationCount = _maxIterations;
            return result;
        }

        /// <summary>
        /// Call model with current conversation and tool schema.
        /// If modelBridge is provided, calls actual model. Otherwise, returns mock for testing.
        /// </summary>
        private async Task<string> CallModelAsync(string userPrompt, int iteration)
        {
            // If no model bridge provided, return mock response
            if (_modelBridge == null)
            {
                return await Task.FromResult(JsonSerializer.Serialize(new
                {
                    content = "Model would be called here with tool definitions and conversation history",
                    tool_calls = new[] {
                        new { function = new { name = "text", arguments = "{\"function\": \"toupper\", \"text\": \"hello\"}" } }
                    }
                }));
            }

            try
            {
                // Get tool schemas for the model
                var toolSchemas = _tools.GetToolSchemas();
                
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

                // Call actual model via LangChainChatBridge
                var response = await _modelBridge.CallModelWithToolsAsync(
                    _messageHistory,
                    toolSchemas);

                _logger?.Log("Info", "ReActLoop", $"Iteration {iteration + 1}: Model call succeeded");

                // Log full response for debugging
                _logger?.Log("Info", "ReActLoop", $"Response payload: {response}");

                // Parse the response to extract tool calls
                var (textContent, toolCalls) = LangChainChatBridge.ParseChatResponse(response);

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

                return JsonSerializer.Serialize(responseObj);
            }
            catch (Exception ex)
            {
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
