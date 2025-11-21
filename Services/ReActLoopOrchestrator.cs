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
        private readonly int _maxIterations;
        private List<ConversationMessage> _messageHistory;

        public ReActLoopOrchestrator(HybridLangChainOrchestrator tools, ICustomLogger? logger = null, int maxIterations = 10)
        {
            _tools = tools;
            _logger = logger;
            _maxIterations = maxIterations;
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
                    var toolResults = new List<string>();
                    foreach (var call in toolCalls)
                    {
                        try
                        {
                            _logger?.Log("Info", "ReActLoop", $"  Executing tool: {call.ToolName}");
                            var output = await _tools.ExecuteToolAsync(call.ToolName, call.Arguments);
                            toolResults.Add(output);
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
                            toolResults.Add(JsonSerializer.Serialize(new { error = ex.Message }));
                        }
                    }

                    // Step 4: Add assistant and tool results to history for next iteration
                    _messageHistory.Add(new ConversationMessage { Role = "assistant", Content = modelResponse });
                    foreach (var toolResult in toolResults)
                    {
                        _messageHistory.Add(new ConversationMessage { Role = "tool", Content = toolResult });
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
        /// Mock: Call model with current conversation and tool schema.
        /// In production, this would call LangChain ChatOpenAI or similar with:
        ///   - messages = _messageHistory
        ///   - tools = _tools.GetToolSchemas()
        ///   - tool_choice = "auto"
        /// </summary>
        private async Task<string> CallModelAsync(string userPrompt, int iteration)
        {
            // TODO: Replace with actual LangChain model call
            // This is a placeholder that simulates a model response for testing
            
            return await Task.FromResult(JsonSerializer.Serialize(new
            {
                content = "Model would be called here with tool definitions and conversation history",
                tool_calls = new[] {
                    new { function = new { name = "text", arguments = "{\"function\": \"toupper\", \"text\": \"hello\"}" } }
                }
            }));
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

        public override string ToString()
        {
            return JsonSerializer.Serialize(this);
        }
    }
}
