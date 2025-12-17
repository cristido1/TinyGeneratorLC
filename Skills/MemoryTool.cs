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
        private readonly IMemoryEmbeddingGenerator? _embeddingGenerator;
        private readonly IMemoryEmbeddingBackfillScheduler? _embeddingScheduler;
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public MemoryTool(
            PersistentMemoryService memoryService,
            ICustomLogger? logger = null,
            IMemoryEmbeddingGenerator? embeddingGenerator = null,
            IMemoryEmbeddingBackfillScheduler? embeddingScheduler = null) 
            : base("memory", "Memory operations", logger)
        {
            _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
            _embeddingGenerator = embeddingGenerator;
            _embeddingScheduler = embeddingScheduler;
        }

        public override Dictionary<string, object> GetSchema()
            => CreateFunctionSchema("memory_remember", "Remember text in a named collection", BuildProperties(includeText: true), new List<string> { "collection", "text" });

        public override IEnumerable<Dictionary<string, object>> GetFunctionSchemas()
        {
            yield return CreateFunctionSchema("memory_remember", "Store text in a memory collection. You can store up to 30000 characters", BuildProperties(includeText: true), new List<string> { "collection", "text" });
            yield return CreateFunctionSchema("memory_forget", "Forget text from a collection", BuildProperties(includeText: true), new List<string> { "collection", "text" });
            yield return CreateFunctionSchema("memory_recall", "Recall text using substring search", BuildProperties(includeQuery: true, includeLimit: true), new List<string> { "collection", "query" });
            yield return CreateFunctionSchema("memory_search", "Semantic/textual search within a collection", BuildProperties(includeQuery: true, includeLimit: true), new List<string> { "collection", "query" });
            yield return CreateFunctionSchema("memory_search_chat", "Search past chat conversation memory", BuildProperties(includeQuery: true, includeLimit: true, includeCollection: false), new List<string> { "query" });
        }

        public override IEnumerable<string> FunctionNames => new[]
        {
            "memory",  // Alias for backwards compatibility with simple schema
            "memory_remember",
            "memory_forget",
            "memory_recall",
            "memory_search",
            "memory_search_chat"
        };

        public override async Task<string> ExecuteAsync(string jsonInput)
        {
            try
            {
                var input = ParseInput<MemoryToolInput>(jsonInput);
                if (input == null)
                    return JsonSerializer.Serialize(new { error = "Invalid input format" });

                var op = input.Operation?.ToLowerInvariant() ?? "memory_remember";
                var normalized = op switch
                {
                    "remember" => "memory_remember",
                    "forget" => "memory_forget",
                    "recall" => "memory_recall",
                    "search" => "memory_search",
                    "search_chat" => "memory_search_chat",
                    _ => op
                };

                return await ExecuteOperationAsync(normalized, input, normalized == "memory_search_chat" ? "chat" : null);
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "MemoryTool", $"Execution failed: {ex.Message}", ex.ToString());
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        public override async Task<string> ExecuteFunctionAsync(string functionName, string input)
        {
            var parsed = ParseInput<MemoryToolInput>(input) ?? new MemoryToolInput();
            
            // If called as "memory" (legacy/simple schema), use the operation field to determine the actual function
            if (functionName.Equals("memory", StringComparison.OrdinalIgnoreCase))
            {
                var op = parsed.Operation?.ToLowerInvariant() ?? "recall";
                functionName = op switch
                {
                    "remember" => "memory_remember",
                    "forget" => "memory_forget",
                    "recall" => "memory_recall",
                    "search" => "memory_search",
                    "search_chat" => "memory_search_chat",
                    _ => $"memory_{op}"  // Fallback: prepend memory_ prefix
                };
            }
            
            return await ExecuteOperationAsync(functionName, parsed, functionName.Equals("memory_search_chat", StringComparison.OrdinalIgnoreCase) ? "chat" : null);
        }

        private async Task<string> ExecuteOperationAsync(string operation, MemoryToolInput input, string? forcedCollection)
        {
            var lowered = operation.ToLowerInvariant();
            var collection = forcedCollection ?? input.Collection ?? "default";
            string result;

            switch (lowered)
            {
                case "memory_remember":
                    {
                        if (string.IsNullOrEmpty(input.Text))
                            return JsonSerializer.Serialize(new { error = "Text required for remember operation" });

                        await _memoryService.SaveAsync(collection, input.Text, metadata: null, modelId: ModelId, agentId: AgentId);
                        _embeddingScheduler?.RequestBackfill("memory_tool");
                        result = $"Remembered in '{collection}': {input.Text}";
                        CustomLogger?.Log("Info", "MemoryTool", $"Remembered: {result}");
                        break;
                    }
                case "memory_recall":
                    {
                        var query = input.Query ?? input.Text ?? "";
                        if (string.IsNullOrEmpty(query))
                            return JsonSerializer.Serialize(new { error = "Query or text required for recall operation" });

                        var recallLimit = input.Limit.HasValue ? Math.Clamp(input.Limit.Value, 1, 20) : 5;
                        var memories = await _memoryService.SearchAsync(collection, query, limit: recallLimit, modelId: ModelId, agentId: AgentId);
                        result = memories.Count == 0
                            ? "No memories found."
                            : string.Join("\n- ", memories);
                        CustomLogger?.Log("Info", "MemoryTool", $"Recalled {memories.Count} memories from '{collection}'");
                        break;
                    }
                case "memory_search":
                case "memory_search_chat":
                    {
                        var query = input.Query ?? input.Text ?? "";
                        if (string.IsNullOrWhiteSpace(query))
                            return JsonSerializer.Serialize(new { error = "Query or text required for search operation" });

                        float[]? queryEmbedding = null;
                        if (_embeddingGenerator != null)
                        {
                            try
                            {
                                queryEmbedding = await _embeddingGenerator.GenerateAsync(query);
                            }
                            catch (Exception ex)
                            {
                                CustomLogger?.Log("Warn", "MemoryTool", $"Embedding for search query failed: {ex.Message}", ex.ToString());
                            }
                        }

                        var searchLimit = input.Limit.HasValue ? Math.Clamp(input.Limit.Value, 1, 20) : 5;
                        var searchResults = await _memoryService.SearchWithEmbeddingsAsync(collection, query, queryEmbedding, searchLimit, ModelId, AgentId);
                        result = searchResults.Count == 0
                            ? "No memories found."
                            : FormatSearchResults(searchResults);
                        CustomLogger?.Log("Info", "MemoryTool", $"Search returned {searchResults.Count} results for '{collection}'");
                        break;
                    }
                case "memory_forget":
                    {
                        if (string.IsNullOrEmpty(input.Text))
                            return JsonSerializer.Serialize(new { error = "Text required for forget operation" });

                        await _memoryService.DeleteAsync(collection, input.Text, modelId: ModelId, agentId: AgentId);
                        result = $"Forgotten from '{collection}': {input.Text}";
                        CustomLogger?.Log("Info", "MemoryTool", $"Forgot: {result}");
                        break;
                    }
                default:
                    return JsonSerializer.Serialize(new { error = $"Unknown operation: {operation}" });
            }

            var resultObj = new { result };
            LastFunctionCalled = operation;
            LastFunctionResult = JsonSerializer.Serialize(resultObj);
            return JsonSerializer.Serialize(resultObj);
        }

        private static string FormatSearchResults(IReadOnlyList<MemorySearchResult> results)
        {
            var formatted = new List<string>(results.Count);
            for (var i = 0; i < results.Count; i++)
            {
                var entry = results[i];
                formatted.Add($"{i + 1}. score={entry.Score:F2} text={entry.Text}");
            }
            return string.Join("\n", formatted);
        }

        private static Dictionary<string, object> BuildProperties(bool includeCollection = true, bool includeText = false, bool includeQuery = false, bool includeLimit = false)
        {
            var props = new Dictionary<string, object>();
            if (includeCollection)
            {
                props["collection"] = new Dictionary<string, object>
                {
                    { "type", "string" },
                    { "description", "Collection name" }
                };
            }
            if (includeText)
            {
                props["text"] = new Dictionary<string, object>
                {
                    { "type", "string" },
                    { "description", "Memory text" }
                };
            }
            if (includeQuery)
            {
                props["query"] = new Dictionary<string, object>
                {
                    { "type", "string" },
                    { "description", "Search query" }
                };
            }
            if (includeLimit)
            {
                props["limit"] = new Dictionary<string, object>
                {
                    { "type", "integer" },
                    { "description", "Maximum number of results" }
                };
            }

            return props;
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

        [JsonPropertyName("limit")]
        public int? Limit { get; set; }
    }
}
