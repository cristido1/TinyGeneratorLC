using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.SemanticKernel
{
    // Minimal builder + kernel stubs to allow the project to compile when the
    // actual Semantic Kernel APIs are not present or have incompatible shapes.
    // These stubs run in "offline" mode and return placeholder text. Replace
    // with the real Semantic Kernel packages for full functionality.

    public static class Kernel
    {
        public static KernelBuilder CreateBuilder() => new KernelBuilder();
    }

    public class KernelBuilder
    {
        public KernelBuilder AddOllamaChatCompletion(string model, Uri url, string? arg = null)
        {
            // no-op stub
            return this;
        }

        public KernelBuilder WithMemoryStorage(object store)
        {
            // no-op stub
            return this;
        }

        public KernelInstance Build() => new KernelInstance();
    }

    public class KernelInstance
    {
        private readonly MemoryService _memory = new MemoryService();
        public MemoryService Memory => _memory;
    }

    public class MemoryService
    {
        public Task SaveInformationAsync(string collection, string info, string id)
        {
            // Simple placeholder: write to a local file under .\out\memory.log
            try
            {
                var dir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "out");
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "memory.log");
                var line = $"[{DateTime.UtcNow:o}] ({collection}:{id}) {info.Replace(Environment.NewLine, " ")}" + Environment.NewLine;
                return System.IO.File.AppendAllTextAsync(path, line);
            }
            catch
            {
                return Task.CompletedTask;
            }
        }
    }
}

namespace Microsoft.SemanticKernel.Agents
{
    using Microsoft.SemanticKernel;
    using System.Linq;

    public class ChatResult
    {
        public string Content { get; set; } = string.Empty;
    }

    public class ChatCompletionAgent
    {
        public string Name { get; }
        public string Description { get; }
        public string Instructions { get; set; } = string.Empty;
        private readonly KernelInstance _kernel;

        public ChatCompletionAgent(string name, KernelInstance kernel, string desc)
        {
            Name = name; Description = desc; _kernel = kernel;
        }

        public Task<IEnumerable<ChatResult>> InvokeAsync(string prompt)
        {
            var preview = prompt.Length <= 200 ? prompt : prompt.Substring(0, 200) + "...";
            var content = $"[STUB {Name}] Risposta simulata per prompt: {preview}";
            var res = new[] { new ChatResult { Content = content } };
            return Task.FromResult<IEnumerable<ChatResult>>(res);
        }
    }
}

namespace Microsoft.SemanticKernel.Connectors.Ollama
{
    // marker namespace for compatibility
    public static class OllamaMarker { }
}

namespace Microsoft.SemanticKernel.Memory
{
    public class SqliteMemoryStore
    {
        public SqliteMemoryStore(string dbPath) { /* stub */ }
    }
}
