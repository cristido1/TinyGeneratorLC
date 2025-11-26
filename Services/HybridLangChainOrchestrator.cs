using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;
using TinyGenerator.Skills;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Hybrid Orchestrator: Uses LangChain for tools that have been migrated, falls back to SK for legacy tools.
    /// This enables gradual migration from Semantic Kernel to LangChain without full rewrite.
    /// </summary>
    public class HybridLangChainOrchestrator
    {
        private readonly ICustomLogger? _logger;
        private readonly Dictionary<string, BaseLangChainTool> _langChainTools;
        private readonly Dictionary<string, BaseLangChainTool> _functionToolMap;
        private readonly Dictionary<string, object> _fallbackSkillsRegistry; // SK legacy skills
        private List<string> _conversationHistory;

        public HybridLangChainOrchestrator(ICustomLogger? logger = null)
        {
            _logger = logger;
            _langChainTools = new Dictionary<string, BaseLangChainTool>(StringComparer.OrdinalIgnoreCase);
            _functionToolMap = new Dictionary<string, BaseLangChainTool>(StringComparer.OrdinalIgnoreCase);
            _fallbackSkillsRegistry = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _conversationHistory = new List<string>();
        }

        /// <summary>
        /// Register a LangChain tool (new/migrated tools).
        /// </summary>
        public void RegisterTool(BaseLangChainTool tool)
        {
            _langChainTools[tool.Name] = tool;
            RemoveFunctionMappings(tool);
            foreach (var functionName in tool.FunctionNames)
            {
                _functionToolMap[functionName] = tool;
            }
            _logger?.Log("Info", "HybridOrchestrator", $"Registered LangChain tool: {tool.Name}");
        }

        /// <summary>
        /// Remove a LangChain tool by name.
        /// </summary>
        public void RemoveTool(string toolName)
        {
            if (_langChainTools.Remove(toolName))
            {
                RemoveFunctionMappings(toolName);
                _logger?.Log("Info", "HybridOrchestrator", $"Removed LangChain tool: {toolName}");
            }
            else
            {
                _logger?.Log("Warn", "HybridOrchestrator", $"Tool not found for removal: {toolName}");
            }
        }

        /// <summary>
        /// Register a fallback SK skill for tools not yet migrated.
        /// </summary>
        public void RegisterFallbackSkill(string name, object skill)
        {
            _fallbackSkillsRegistry[name] = skill;
            _logger?.Log("Info", "HybridOrchestrator", $"Registered fallback SK skill: {name}");
        }

        /// <summary>
        /// Execute a tool call by name with JSON input. Returns JSON output.
        /// If tool exists in LangChain registry, use it. Otherwise, fallback to SK skill.
        /// </summary>
        public async Task<string> ExecuteToolAsync(string toolName, string jsonInput)
        {
            try
            {
                // Try LangChain tools first (preferred)
                if (_functionToolMap.TryGetValue(toolName, out var tool))
                {
                    _logger?.Log("Info", "HybridOrchestrator", $"Executing LangChain tool: {toolName}");
                    return await tool.ExecuteFunctionAsync(toolName, jsonInput);
                }

                if (_langChainTools.TryGetValue(toolName, out var fallbackTool))
                {
                    _logger?.Log("Info", "HybridOrchestrator", $"Executing LangChain tool: {toolName}");
                    return await fallbackTool.ExecuteFunctionAsync(toolName, jsonInput);
                }

                // Fallback to SK legacy skills
                if (_fallbackSkillsRegistry.TryGetValue(toolName, out var skill))
                {
                    _logger?.Log("Info", "HybridOrchestrator", $"Executing fallback SK skill: {toolName}");
                    // For now, return a placeholder. In full migration, SK skills can be wrapped as tools.
                    return JsonSerializer.Serialize(new { message = "SK skill execution not yet bridged in hybrid mode", skill = toolName });
                }

                return JsonSerializer.Serialize(new { error = $"Tool not found: {toolName}" });
            }
            catch (Exception ex)
            {
                _logger?.Log("Error", "HybridOrchestrator", $"Tool execution failed: {ex.Message}", ex.ToString());
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        public T? GetTool<T>(string toolName) where T : BaseLangChainTool
        {
            if (_langChainTools.TryGetValue(toolName, out var tool))
            {
                return tool as T;
            }
            return null;
        }

        private void RemoveFunctionMappings(BaseLangChainTool tool)
        {
            var removeKeys = _functionToolMap
                .Where(kvp => kvp.Value == tool)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in removeKeys)
            {
                _functionToolMap.Remove(key);
            }
        }

        private void RemoveFunctionMappings(string toolName)
        {
            var removeKeys = _functionToolMap
                .Where(kvp => kvp.Value.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (var key in removeKeys)
            {
                _functionToolMap.Remove(key);
            }
        }

        /// <summary>
        /// Get available tools as JSON schema for model function calling.
        /// </summary>
        public List<Dictionary<string, object>> GetToolSchemas()
        {
            var schemas = new List<Dictionary<string, object>>();
            
            foreach (var tool in _langChainTools.Values)
            {
                try
                {
                    if (tool.ExposeToModel)
                    {
                        foreach (var schema in tool.GetFunctionSchemas())
                        {
                            schemas.Add(schema);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Log("Error", "HybridOrchestrator", $"Failed to get schema for {tool.Name}: {ex.Message}");
                }
            }

            return schemas;
        }

        /// <summary>
        /// Parse tool calls from model response (e.g., OpenAI function_calls format).
        /// </summary>
        public List<ToolCall> ParseToolCalls(string modelResponse)
        {
            var calls = new List<ToolCall>();
            
            try
            {
                // Try to parse as JSON containing tool_calls array (OpenAI format)
                var jsonDoc = JsonDocument.Parse(modelResponse);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("tool_calls", out var toolCalls))
                {
                    foreach (var call in toolCalls.EnumerateArray())
                    {
                        if (call.TryGetProperty("function", out var funcElem) &&
                            funcElem.TryGetProperty("name", out var nameElem))
                        {
                            // Get arguments - can be either a string or an object
                            string argumentsJson = "{}";
                            if (funcElem.TryGetProperty("arguments", out var argsElem))
                            {
                                if (argsElem.ValueKind == JsonValueKind.String)
                                {
                                    // Arguments is a string, use as-is
                                    argumentsJson = argsElem.GetString() ?? "{}";
                                }
                                else if (argsElem.ValueKind == JsonValueKind.Object)
                                {
                                    // Arguments is an object, serialize it to string
                                    argumentsJson = argsElem.GetRawText();
                                }
                            }

                            calls.Add(new ToolCall
                            {
                                ToolName = nameElem.GetString() ?? "unknown",
                                Arguments = argumentsJson,
                                Id = (call.TryGetProperty("id", out var id) ? id.GetString() : null) ?? Guid.NewGuid().ToString()
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Log("Warn", "HybridOrchestrator", $"Failed to parse tool calls: {ex.Message}");
            }

            return calls;
        }

        /// <summary>
        /// Add message to conversation history for multi-turn interactions.
        /// </summary>
        public void AddToHistory(string role, string content)
        {
            _conversationHistory.Add(JsonSerializer.Serialize(new { role, content }));
        }

        /// <summary>
        /// Get conversation history.
        /// </summary>
        public List<string> GetHistory() => _conversationHistory;

        /// <summary>
        /// Clear conversation history.
        /// </summary>
        public void ClearHistory()
        {
            _conversationHistory.Clear();
        }
    }

    /// <summary>
    /// Represents a tool call parsed from model response.
    /// </summary>
    public class ToolCall
    {
        public string ToolName { get; set; } = string.Empty;
        public string Arguments { get; set; } = "{}";
        public string Id { get; set; } = Guid.NewGuid().ToString();
    }
}
