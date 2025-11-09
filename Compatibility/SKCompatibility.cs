using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

// Self-contained compatibility layer providing a minimal kernel API and helpers
// so the existing codebase (written against an older SK shape) can compile
// and run while we wire real connectors. This avoids referencing
// Microsoft.SemanticKernel.Abstractions directly and keeps behavior safe.
namespace Microsoft.SemanticKernel
{
    // Minimal kernel interface used by the application.
    public interface IKernel
    {
        /// <summary>
        /// Run the kernel with a prompt and return a simple result wrapper.
        /// Implementations may return a placeholder if no real model is configured.
        /// </summary>
        Task<KernelRunResult> RunAsync(string prompt);

        /// <summary>
        /// Simple memory abstraction used by the app (only SaveInformationAsync is required).
        /// </summary>
        IMemoryStore Memory { get; }
    }

    public class KernelRunResult
    {
        public string Result { get; set; } = string.Empty;
    }

    public interface IMemoryStore
    {
        Task SaveInformationAsync(string collection, string content, string id);
    }

    // A very small in-process kernel implementation that does not call external models.
    // It provides deterministic placeholder responses so the rest of the app can run.
    public class SimpleKernel : IKernel
    {
        private readonly SimpleMemoryStore _mem = new SimpleMemoryStore();
        private readonly string _notice;

        public SimpleKernel(string notice = "[SimpleKernel: no external model configured]")
        {
            _notice = notice;
        }

        public IMemoryStore Memory => _mem;

        public Task<KernelRunResult> RunAsync(string prompt)
        {
            // Return a safe placeholder that indicates there is no configured LLM yet.
            var shortPrompt = prompt?.Length > 300 ? prompt.Substring(0, 300) + "..." : prompt;
            var resultText = $"{_notice}\nPrompt (truncated): {shortPrompt}";
            return Task.FromResult(new KernelRunResult { Result = resultText });
        }
    }

    public class SimpleMemoryStore : IMemoryStore
    {
        public Task SaveInformationAsync(string collection, string content, string id)
        {
            try
            {
                // best-effort: write to a local file for debugging when possible
                var safe = System.IO.Path.Combine("data", "sk_memory.log");
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(safe) ?? "data");
                    System.IO.File.AppendAllText(safe, $"[{DateTime.UtcNow:O}] {collection} {id}: {content}\n");
                }
                catch { }
            }
            catch { }
            return Task.CompletedTask;
        }
    }
}

namespace Microsoft.SemanticKernel.Agents
{
    // Lightweight compatibility ChatCompletionAgent used by the existing codebase.
    // It wraps the minimal IKernel above and returns a single ChatResult.
    public class ChatCompletionAgent
    {
        private readonly object _kernel;
        public string Name { get; }
        public string? Model { get; }
        public string? Instructions { get; set; }


        public ChatCompletionAgent(string name, object kernel, string description, string? model = null)
        {
            Name = name;
            _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
            Model = model;
            Instructions = description;
        }

        public async Task<IEnumerable<ChatResult>> InvokeAsync(string prompt)
        {
            var finalPrompt = string.IsNullOrWhiteSpace(Instructions) ? prompt : Instructions + "\n\n" + prompt;
            if (_kernel is Microsoft.SemanticKernel.IKernel ikernel)
            {
                var run = await ikernel.RunAsync(finalPrompt);
                return new[] { new ChatResult { Content = run.Result ?? string.Empty } };
            }
            else if (_kernel is Microsoft.SemanticKernel.Kernel kernel)
            {
                var run = await kernel.InvokePromptAsync(finalPrompt);
                return new[] { new ChatResult { Content = run.GetValue<string>() ?? string.Empty } };
            }
            else
            {
                throw new InvalidOperationException("Kernel type non supportato in ChatCompletionAgent");
            }
        }
    }

    public class ChatResult
    {
        public string Content { get; set; } = string.Empty;
    }
}

namespace Microsoft.SemanticKernel.Planning
{
    // Minimal Plan and PlanStep compatibility types used by PlannerExecutor and StoryGeneratorService.
    public class Plan
    {
        public string? Goal { get; set; }
        public List<PlanStep> Steps { get; set; } = new List<PlanStep>();
    }

    public class PlanStep
    {
        public string Description { get; set; } = string.Empty;
    }

    // A tiny planner that creates a fixed sequence of steps used by the app.
    public class HandlebarsPlanner
    {
        private readonly Microsoft.SemanticKernel.IKernel _kernel;

        public HandlebarsPlanner(Microsoft.SemanticKernel.IKernel kernel)
        {
            _kernel = kernel;
        }

        public Task<Plan> CreatePlanAsync(string prompt)
        {
            var p = new Plan { Goal = prompt };
            p.Steps.Add(new PlanStep { Description = "trama" });
            p.Steps.Add(new PlanStep { Description = "personaggi" });
            p.Steps.Add(new PlanStep { Description = "primo capitolo" });
            p.Steps.Add(new PlanStep { Description = "secondo capitolo" });
            p.Steps.Add(new PlanStep { Description = "terzo capitolo" });
            p.Steps.Add(new PlanStep { Description = "riassunto cumulativo" });
            return Task.FromResult(p);
        }
    }
}

