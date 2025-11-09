
using Microsoft.SemanticKernel;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    public class MemorySkill
    {
        private readonly PersistentMemoryService _memory;

        public MemorySkill(PersistentMemoryService memory)
        {
            _memory = memory;
        }

    [KernelFunction("remember")]
    public async Task<string> RememberAsync(string collection, string text)
    {
        await _memory.SaveAsync(collection, text);
        return $"🧠 Ricordato in '{collection}': {text}";
    }

    [KernelFunction("recall")]
    public async Task<string> RecallAsync(string collection, string query)
    {
        var results = await _memory.SearchAsync(collection, query);
        return results.Count == 0
            ? "Nessun ricordo trovato."
            : string.Join("\n- ", results);
    }
    }
}
