
using Microsoft.SemanticKernel;
using TinyGenerator.Services;
using System.ComponentModel;

namespace TinyGenerator.Skills
{
    [Description("Provides memory-related functions such as remember, recall, and forget.")]
    public class MemorySkill : ITinySkill
    {
        private readonly PersistentMemoryService _memory;
        private readonly ICustomLogger? _logger;
        private int? _modelId;
        private int? _agentId;
        private string? _modelName;
        private DateTime? _lastCalled;
        private string? _lastFunction;
        public string? LastCollection { get; set; }
        public string? LastText { get; set; }

        // ITinySkill implementation
        int? ITinySkill.ModelId { get => _modelId; set => _modelId = value; }
        string? ITinySkill.ModelName { get => _modelName; set => _modelName = value; }
        int? ITinySkill.AgentId => _agentId;
        string? ITinySkill.AgentName => null;
        DateTime? ITinySkill.LastCalled { get => _lastCalled; set => _lastCalled = value; }
        string? ITinySkill.LastFunction { get => _lastFunction; set => _lastFunction = value; }
        ICustomLogger? ITinySkill.Logger { get => _logger; set { } }

        public MemorySkill(PersistentMemoryService memory, int? modelId = null, int? agentId = null, ICustomLogger? logger = null)
        {
            _memory = memory;
            _logger = logger;
            _modelId = modelId;
            _agentId = agentId;
        }

        [KernelFunction("remember"), Description("Remembers a piece of text in a specific collection.")]
        public async Task<string> RememberAsync([Description("The collection to remember the text in.")] string collection, [Description("The text to remember.")] string text)
        {
            ((ITinySkill)this).LogFunctionCall("remember");
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(RememberAsync);
            LastCollection = collection;
            LastText = text;
            await _memory.SaveAsync(collection, text, metadata: null, modelId: _modelId, agentId: _agentId);
            return $"🧠 Ricordato in '{collection}': {text}";
        }

        [KernelFunction("recall"), Description("Recalls a piece of text from a specific collection.")]
        public async Task<string> RecallAsync([Description("The collection to recall the text from.")] string collection, [Description("The query to search for.")] string query)
        {
            ((ITinySkill)this).LogFunctionCall("recall");
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(RecallAsync);
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
            ((ITinySkill)this).LogFunctionCall("forget");
            ((ITinySkill)this).LastCalled = DateTime.UtcNow;
            ((ITinySkill)this).LastFunction = nameof(ForgetAsync);
            LastCollection = collection;
            LastText = text;
            await _memory.DeleteAsync(collection, text, modelId: _modelId, agentId: _agentId);
            return $"❌ Ricordo cancellato da '{collection}': {text}";
        }
        [KernelFunction("describe"), Description("Describes the available memory functions.")]
        public string Describe()
        {
            ((ITinySkill)this).LogFunctionCall("describe");
            return "Available functions: remember(collection, text), recall(collection, query), forget(collection, text). " +
                "Example: memory.remember('notes', 'Buy milk').";
        }
    }
}
