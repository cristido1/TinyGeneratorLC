
using Microsoft.SemanticKernel;
using TinyGenerator.Services;
using System.ComponentModel;

namespace TinyGenerator.Skills
{
    [Description("Provides memory-related functions such as remember, recall, and forget.")]
    public class MemorySkill
    {
        private readonly PersistentMemoryService _memory;
        private readonly int? _modelId;
        private readonly int? _agentId;
        public string? LastCalled { get; set; }
        public string? LastCollection { get; set; }
        public string? LastText { get; set; }

        public MemorySkill(PersistentMemoryService memory, int? modelId = null, int? agentId = null)
        {
            _memory = memory;
            _modelId = modelId;
            _agentId = agentId;
        }

        [KernelFunction("remember"), Description("Remembers a piece of text in a specific collection.")]
        public async Task<string> RememberAsync([Description("The collection to remember the text in.")] string collection, [Description("The text to remember.")] string text)
        {
            LastCalled = nameof(RememberAsync);
            LastCollection = collection;
            LastText = text;
            await _memory.SaveAsync(collection, text, metadata: null, modelId: _modelId, agentId: _agentId);
            return $"🧠 Ricordato in '{collection}': {text}";
        }

        [KernelFunction("recall"), Description("Recalls a piece of text from a specific collection.")]
        public async Task<string> RecallAsync([Description("The collection to recall the text from.")] string collection, [Description("The query to search for.")] string query)
        {
            LastCalled = nameof(RecallAsync);
            LastCollection = collection;
            LastText = query;
            var results = await _memory.SearchAsync(collection, query, limit: 5, modelId: _modelId, agentId: _agentId);
            return results.Count == 0
                ? "Nessun ricordo trovato."
                : string.Join("\n- ", results);
        }

        [KernelFunction("forget"), Description("Forgets a piece of text from a specific collection.")]
        public async Task<string> ForgetAsync([Description("The collection to forget the text from.")] string collection, [Description("The text to forget.")] string text)
        {
            LastCalled = nameof(ForgetAsync);
            LastCollection = collection;
            LastText = text;
            await _memory.DeleteAsync(collection, text, modelId: _modelId, agentId: _agentId);
            return $"❌ Ricordo cancellato da '{collection}': {text}";
        }
        [KernelFunction("describe"), Description("Describes the available memory functions.")]
        public string Describe() =>
            "Available functions: remember(collection, text), recall(collection, query), forget(collection, text). " +
            "Example: memory.remember('notes', 'Buy milk').";
    }
}

// La funzione forget che cancella solo un singolo item è già presente sopra
