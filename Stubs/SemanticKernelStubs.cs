using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

    // Lightweight IKernel interface used by the app
    public interface IKernel
    {
        MemoryService Memory { get; }
    }

    public class KernelInstance : IKernel
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
        // Reuse a single HttpClient for connection pooling and configure timeout via env var
        private static readonly HttpClient _httpClient;

        static ChatCompletionAgent()
        {
            // Default timeout in seconds (stories/chapters can take a while). Use a generous default
            var defaultTimeout = 900; // 15 minutes
            var minTimeout = 30; // don't accept very small timeouts
            var timeoutEnv = Environment.GetEnvironmentVariable("OLLAMA_TIMEOUT_SECONDS");
            if (!int.TryParse(timeoutEnv, out var timeoutSec) || timeoutSec <= 0)
            {
                timeoutSec = defaultTimeout;
            }
            // enforce a sensible minimum
            if (timeoutSec < minTimeout) timeoutSec = defaultTimeout;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };
            Console.WriteLine($"[StubAgent] Ollama HttpClient timeout set to {timeoutSec}s (env OLLAMA_TIMEOUT_SECONDS={timeoutEnv ?? "<unset>"})");
        }

        public string Name { get; }
        public string Description { get; }
        public string? Model { get; }
        public string Instructions { get; set; } = string.Empty;
        private readonly IKernel _kernel;
        public ChatCompletionAgent(string name, IKernel kernel, string desc, string? model = null)
        {
            Name = name; Description = desc; _kernel = kernel; Model = model;
            try
            {
                Console.WriteLine($"[StubAgent:{Name}] created. Model={(Model ?? "<null>")}, Description={desc}");
            }
            catch { }
        }

        public async Task<IEnumerable<ChatResult>> InvokeAsync(string prompt)
        {
            // If a model is specified and an Ollama daemon is reachable, try calling it.
            // Ensure an explicit model is configured for the agent; otherwise return an error
            if (string.IsNullOrEmpty(Model))
            {
                throw new InvalidOperationException($"Agent '{Name}' has no model configured.");
            }
            // Attempt to call Ollama; if anything fails, surface the error to the caller
            // Use the shared _httpClient (configured in static ctor) for timeouts and pooling
            var httpClient = _httpClient;
            // Diagnostic: check that the requested model is running in the local ollama daemon (best-effort)
            try
            {
                var skip = Environment.GetEnvironmentVariable("OLLAMA_SKIP_MODEL_CHECK");
                if (string.IsNullOrEmpty(skip) || skip != "1")
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo("ollama", "ps") { RedirectStandardOutput = true, UseShellExecute = false };
                        using var p = System.Diagnostics.Process.Start(psi);
                        if (p != null)
                        {
                            p.WaitForExit(3000);
                            var outp = p.StandardOutput.ReadToEnd();
                            if (!outp.Contains(Model ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"[StubAgent:{Name}] WARNING: requested model '{Model}' not listed in 'ollama ps' output. Available models:\n{outp}");
                            }
                            else
                            {
                                Console.WriteLine($"[StubAgent:{Name}] Model '{Model}' is present according to 'ollama ps'.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[StubAgent:{Name}] Could not run 'ollama ps' to validate models: {ex.Message}");
                    }
                }
            }
            catch { }
            var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://127.0.0.1:11434";
            var endpointUrl = baseUrl.TrimEnd('/') + "/v1/chat/completions";
            var payloadObj = new { model = Model, messages = new[] { new { role = "user", content = prompt } } };
            var payloadJson = JsonSerializer.Serialize(payloadObj);
            using var reqContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            // Diagnostic: ensure the payload contains the exact model we're about to send
            try
            {
                var shortPayload = payloadJson.Length > 400 ? payloadJson.Substring(0, 400) + "..." : payloadJson;
                Console.WriteLine($"[StubAgent:{Name}] Calling Ollama at {endpointUrl} for model {Model}. Payload: {shortPayload}");
            }
            catch { Console.WriteLine($"[StubAgent:{Name}] Calling Ollama at {endpointUrl} for model {Model}"); }
            // record the last prompt issued to this model for monitoring purposes
            try { TinyGenerator.Services.OllamaMonitorService.RecordPrompt(Model ?? string.Empty, prompt); } catch { }
            HttpResponseMessage response;
            try
            {
                response = await httpClient.PostAsync(endpointUrl, reqContent);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to call Ollama at {endpointUrl}: {ex.Message}", ex);
            }

            Console.WriteLine($"[StubAgent:{Name}] Ollama responded: {response.StatusCode}");
            var respBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}: {respBody}");
            }

            try
            {
                using var doc = JsonDocument.Parse(respBody);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var cont))
                    {
                        var text = cont.GetString() ?? string.Empty;
                        return new[] { new ChatResult { Content = text } };
                    }
                    if (first.TryGetProperty("content", out var cont2))
                    {
                        var text = cont2.GetString() ?? string.Empty;
                        return new[] { new ChatResult { Content = text } };
                    }
                }
            }
            catch (JsonException) { /* fall through to return raw body */ }

            // If parsing fails, return the raw response body as the content
            return new[] { new ChatResult { Content = respBody } };
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

namespace Microsoft.SemanticKernel.Planning
{
    using Microsoft.SemanticKernel;
    using Microsoft.SemanticKernel.Agents;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    public class HandlebarsPlanner
    {
        private readonly IKernel _kernel;

        public HandlebarsPlanner(IKernel kernel)
        {
            _kernel = kernel;
        }

        public async Task<Plan> CreatePlanAsync(string goal)
        {
            // Stub: return a hardcoded plan for story generation
            var plan = new Plan
            {
                Goal = goal,
                Steps = new List<PlanStep>
                {
                    new PlanStep { Description = "Creare trama dettagliata suddivisa in 6 capitoli", Agent = "WriterA" },
                    new PlanStep { Description = "Definire personaggi e caratteri", Agent = "WriterA" },
                    new PlanStep { Description = "Scrivere primo capitolo con narratore e dialoghi", Agent = "WriterA" },
                    new PlanStep { Description = "Fare riassunto di quello che è successo nel primo capitolo", Agent = "WriterA" },
                    new PlanStep { Description = "Scrivere secondo capitolo usando trama, personaggi, riassunto primo", Agent = "WriterA" },
                    new PlanStep { Description = "Aggiornare riassunto cumulativo aggiungendo riassunto secondo capitolo", Agent = "WriterA" },
                    new PlanStep { Description = "Scrivere terzo capitolo usando trama, personaggi, riassunto cumulativo", Agent = "WriterA" },
                    new PlanStep { Description = "Aggiornare riassunto cumulativo aggiungendo riassunto terzo capitolo", Agent = "WriterA" },
                    new PlanStep { Description = "Scrivere quarto capitolo usando trama, personaggi, riassunto cumulativo", Agent = "WriterA" },
                    new PlanStep { Description = "Aggiornare riassunto cumulativo aggiungendo riassunto quarto capitolo", Agent = "WriterA" },
                    new PlanStep { Description = "Scrivere quinto capitolo usando trama, personaggi, riassunto cumulativo", Agent = "WriterA" },
                    new PlanStep { Description = "Aggiornare riassunto cumulativo aggiungendo riassunto quinto capitolo", Agent = "WriterA" },
                    new PlanStep { Description = "Scrivere sesto capitolo usando trama, personaggi, riassunto cumulativo", Agent = "WriterA" },
                    new PlanStep { Description = "Aggiornare riassunto cumulativo finale aggiungendo riassunto sesto capitolo", Agent = "WriterA" }
                }
            };
            return plan;
        }
    }

    public class Plan
    {
        public string Goal { get; set; } = string.Empty;
        public List<PlanStep> Steps { get; set; } = new List<PlanStep>();
    }

    public class PlanStep
    {
        public string Description { get; set; } = string.Empty;
        public string Agent { get; set; } = string.Empty;
    }
}
 
