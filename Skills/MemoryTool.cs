using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain version of MemorySkill.
    /// Provides persistent memory operations: remember, recall, forget.
    /// </summary>
    public class MemoryTool : BaseLangChainTool, ITinyTool
    {
        private readonly PersistentMemoryService _memoryService;
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public MemoryTool(PersistentMemoryService memoryService, ICustomLogger? logger = null) 
            : base("memory", "Persistent memory operations: remember, recall, forget text in collections", logger)
        {
            _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        }

        public override Dictionary<string, object> GetSchema()
        {
            var properties = new Dictionary<string, object>
            {
                { "operation", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "enum", new List<string> { "remember", "recall", "forget" } },
                        { "description", "The memory operation: remember, recall, or forget" }
                    }
                },
                { "collection", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "The collection name (namespace) for memory storage" }
                    }
                },
                { "text", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "The text to remember or forget" }
                    }
                },
                { "query", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "The search query for recall operations" }
                    }
                }
            };

            return CreateFunctionSchema("memory", "Persistent memory operations", properties, new List<string> { "operation", "collection" });
        }

        public override async Task<string> ExecuteAsync(string jsonInput)
        {
            try
            {
                var input = JsonSerializer.Deserialize<MemoryToolInput>(jsonInput, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                if (input == null)
                    return JsonSerializer.Serialize(new { error = "Invalid input format" });

                var operation = input.Operation?.ToLower() ?? "";
                var collection = input.Collection ?? "default";

                string result;

                if (operation == "remember")
                {
                    if (string.IsNullOrEmpty(input.Text))
                        return JsonSerializer.Serialize(new { error = "Text required for remember operation" });

                    await _memoryService.SaveAsync(collection, input.Text, metadata: null, modelId: ModelId, agentId: AgentId);
                    result = $"Remembered in '{collection}': {input.Text}";
                    CustomLogger?.Log("Info", "MemoryTool", $"Remembered: {result}");
                }
                else if (operation == "recall")
                {
                    var query = input.Query ?? input.Text ?? "";
                    if (string.IsNullOrEmpty(query))
                        return JsonSerializer.Serialize(new { error = "Query or text required for recall operation" });

                    var memories = await _memoryService.SearchAsync(collection, query, limit: 5, modelId: ModelId, agentId: AgentId);
                    result = memories.Count == 0 
                        ? "No memories found." 
                        : string.Join("\n- ", memories);
                    CustomLogger?.Log("Info", "MemoryTool", $"Recalled {memories.Count} memories from '{collection}'");
                }
                else if (operation == "forget")
                {
                    if (string.IsNullOrEmpty(input.Text))
                        return JsonSerializer.Serialize(new { error = "Text required for forget operation" });

                    await _memoryService.DeleteAsync(collection, input.Text, modelId: ModelId, agentId: AgentId);
                    result = $"Forgotten from '{collection}': {input.Text}";
                    CustomLogger?.Log("Info", "MemoryTool", $"Forgot: {result}");
                }
                else
                    return JsonSerializer.Serialize(new { error = $"Unknown operation: {operation}" });

                var resultObj = new { result = result };
                LastFunctionCalled = operation;
                LastFunctionResult = JsonSerializer.Serialize(resultObj);
                return await Task.FromResult(JsonSerializer.Serialize(resultObj));
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "MemoryTool", $"Execution failed: {ex.Message}", ex.ToString());
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }

    public class MemoryToolInput
    {
        [JsonPropertyName("operation")]
        public string? Operation { get; set; }

        [JsonPropertyName("collection")]
        public string? Collection { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("query")]
        public string? Query { get; set; }
    }
}
