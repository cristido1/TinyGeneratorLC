using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Sqlite;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace TinyGenerator.Services
{
    public class KernelWithPlugins
    {
        public Kernel? Kernel { get; set; }
        public TinyGenerator.Skills.TextPlugin? TextPlugin { get; set; }
        public TinyGenerator.Skills.MathPlugin? MathPlugin { get; set; }
        public TinyGenerator.Skills.TimePlugin? TimePlugin { get; set; }
        public TinyGenerator.Skills.FileSystemPlugin? FileSystemPlugin { get; set; }
        public TinyGenerator.Skills.HttpPlugin? HttpPlugin { get; set; }
        public TinyGenerator.Skills.MemorySkill? MemorySkill { get; set; }
        public TinyGenerator.Skills.StoryWriterSkill? StoryWriterSkill { get; set; }
        public TinyGenerator.Skills.AudioEvaluatorSkill? AudioEvaluatorSkill { get; set; }
        public TinyGenerator.Skills.StoryEvaluatorSkill? StoryEvaluatorSkill { get; set; }
    }
    public class KernelFactory : IKernelFactory
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, KernelWithPlugins> _agentKernels = new();
        private readonly IConfiguration _config;
        private readonly ILogger<KernelFactory>? _logger;
        private readonly ILoggerFactory? _loggerFactory;
        private readonly TinyGenerator.Services.PersistentMemoryService _memoryService;
        private readonly DatabaseService _database;
        private readonly System.IServiceProvider _serviceProvider;
        private readonly System.Net.Http.HttpClient _httpClient;
        private readonly System.Net.Http.HttpClient _ttsHttpClient;
        private readonly System.Net.Http.HttpClient _skHttpClient; // HttpClient for Semantic Kernel with longer timeout
    private readonly bool _forceAudioCpu;

        // Proprietà pubbliche per i plugin
        public TinyGenerator.Skills.TextPlugin TextPlugin { get; }
        public TinyGenerator.Skills.MathPlugin MathPlugin { get; }
        public TinyGenerator.Skills.TimePlugin TimePlugin { get; }
        public TinyGenerator.Skills.FileSystemPlugin FileSystemPlugin { get; }
        public TinyGenerator.Skills.HttpPlugin HttpPlugin { get; }
        public TinyGenerator.Skills.MemorySkill MemorySkill { get; }
        public TinyGenerator.Skills.AudioCraftSkill AudioCraftSkill { get; }
        public TinyGenerator.Skills.AudioEvaluatorSkill AudioEvaluatorSkill { get; }
            public TinyGenerator.Skills.TtsApiSkill TtsApiSkill { get; }
        public TinyGenerator.Skills.StoryEvaluatorSkill StoryEvaluatorSkill { get; }
        public TinyGenerator.Skills.StoryWriterSkill StoryWriterSkill { get; }

        public KernelFactory(
            IConfiguration config,
            TinyGenerator.Services.PersistentMemoryService memoryService,
            DatabaseService database,
            System.IServiceProvider serviceProvider,
            ILoggerFactory? loggerFactory = null,
            ILogger<KernelFactory>? logger = null)
        {
            _config = config;
            _logger = logger;
            _memoryService = memoryService;
            _serviceProvider = serviceProvider;
            _httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            _ttsHttpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _skHttpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) }; // SK HttpClient with 10 min timeout for long-running generations
            _forceAudioCpu = false;
            try
            {
                // Read optional configuration flag AudioCraft:ForceCpu (bool)
                var f = _config["AudioCraft:ForceCpu"];
                if (!string.IsNullOrWhiteSpace(f) && bool.TryParse(f, out var fv)) _forceAudioCpu = fv;
            }
            catch { }
            _loggerFactory = loggerFactory;
            _database = database;

            // Inizializzazione plugin (factory-level defaults when not using per-kernel instances)
            TextPlugin = new TinyGenerator.Skills.TextPlugin();
            MathPlugin = new TinyGenerator.Skills.MathPlugin();
            TimePlugin = new TinyGenerator.Skills.TimePlugin();
            FileSystemPlugin = new TinyGenerator.Skills.FileSystemPlugin();
            HttpPlugin = new TinyGenerator.Skills.HttpPlugin();
            MemorySkill = new TinyGenerator.Skills.MemorySkill(_memoryService);
            AudioCraftSkill = new TinyGenerator.Skills.AudioCraftSkill(_httpClient, _forceAudioCpu);
            AudioEvaluatorSkill = new TinyGenerator.Skills.AudioEvaluatorSkill(_httpClient);
            TtsApiSkill = new TinyGenerator.Skills.TtsApiSkill(_ttsHttpClient);
            StoryEvaluatorSkill = new TinyGenerator.Skills.StoryEvaluatorSkill(_database);
            // StoryWriterSkill will be created lazily when needed to avoid circular dependency
            StoryWriterSkill = null!;
        }

        // Ensure a kernel is created and cached for a given agent id. Allowed plugin aliases control which plugins are registered.
        public void EnsureKernelForAgent(int agentId, string? modelId, System.Collections.Generic.IEnumerable<string>? allowedPlugins = null)
        {
            try
            {
                // Se non viene passato un modelId esplicito, prova a risalire al modello dell'agente dal DB
                string? resolvedModel = modelId;
                try
                {
                    if (string.IsNullOrWhiteSpace(resolvedModel))
                    {
                        var agent = _database?.GetAgentById(agentId);
                        if (agent?.ModelId != null)
                        {
                            resolvedModel = _database?.GetModelNameById(agent.ModelId.Value);
                        }
                    }
                }
                catch { /* best-effort */ }

                // Create kernel using same factory method (which will attach plugin instances from this factory)
                var kw = CreateKernel(resolvedModel, allowedPlugins, agentId);
                // Do not overwrite the per-kernel plugin instances returned by CreateKernel.
                // The CreateKernel method already sets the plugin instances that were registered for this kernel.
                _agentKernels[agentId] = kw;
            }
            catch
            {
                // best-effort: do not throw on startup failure
            }
        }

        public KernelWithPlugins? GetKernelForAgent(int agentId)
        {
            if (_agentKernels.TryGetValue(agentId, out var kw)) return kw;
            return null;
        }

        public KernelWithPlugins CreateKernel(string? modelId = null, System.Collections.Generic.IEnumerable<string>? allowedPlugins = null, int? agentId = null)
        {
            var builder = Kernel.CreateBuilder();
            // Abilita l’auto-invocazione delle funzioni registrate

            if (_loggerFactory != null)
            {
                try
                {
                    // Some versions of Semantic Kernel expose WithLoggerFactory on the builder.
                    // Use reflection so compilation doesn't break if the method is absent.
                    var mi = builder.GetType().GetMethod("WithLoggerFactory", new[] { typeof(ILoggerFactory) });
                    if (mi != null)
                    {
                        mi.Invoke(builder, new object[] { _loggerFactory });
                    }
                }
                catch
                {
                    // best-effort: ignore if not supported
                }
            }

            var model = modelId ?? _config["AI:Model"] ?? "phi3:mini-128k";
            var numericModelId = _database?.GetModelIdByName(model);
            var modelInfo = _database?.GetModelInfo(model);
            var provider = modelInfo?.Provider?.Trim();
            var providerLower = provider?.ToLowerInvariant();

            // Choose connector based on provider or naming convention
            if (string.Equals(providerLower, "openai", StringComparison.OrdinalIgnoreCase) || model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase))
            {
                var apiKey = _config["Secrets:OpenAI:ApiKey"]
                            ?? _config["OpenAI:ApiKey"]
                            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException("OpenAI API key not configured. Set Secrets:OpenAI:ApiKey or OPENAI_API_KEY.");
                }

                var openAiEndpoint = modelInfo?.Endpoint ?? _config["OpenAI:Endpoint"];
                _logger?.LogInformation("Creazione kernel OpenAI con modello {model} (endpoint={endpoint})", model, openAiEndpoint ?? "default");

                if (!string.IsNullOrWhiteSpace(openAiEndpoint))
                {
                    builder.AddOpenAIChatCompletion(modelId: model, apiKey: apiKey, endpoint: new Uri(openAiEndpoint), httpClient: _skHttpClient);
                }
                else
                {
                    builder.AddOpenAIChatCompletion(modelId: model, apiKey: apiKey, httpClient: _skHttpClient);
                }
            }
            else if (string.Equals(providerLower, "azure", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(providerLower, "azure-openai", StringComparison.OrdinalIgnoreCase))
            {
                var endpoint = _config["AzureOpenAI:Endpoint"]
                               ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
                               ?? modelInfo?.Endpoint;
                var apiKey = _config["AzureOpenAI:ApiKey"]
                             ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
                var deployment = model; // usa metadata per override se disponibile

                if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException("Azure OpenAI non configurato. Imposta AzureOpenAI:Endpoint e AzureOpenAI:ApiKey o variabili AZURE_OPENAI_ENDPOINT/AZURE_OPENAI_API_KEY.");
                }

                _logger?.LogInformation("Creazione kernel Azure OpenAI con deployment {deployment} su {endpoint}", deployment, endpoint);
                builder.AddAzureOpenAIChatCompletion(deploymentName: deployment, endpoint: endpoint!, apiKey: apiKey!, httpClient: _skHttpClient);
            }
            else
            {
                // Fallback: use OpenAI connector even for models that previously used Ollama.
                // For Ollama endpoints, we need to ensure the endpoint ends with /v1 and provide a dummy API key
                var openAiEndpoint = modelInfo?.Endpoint ?? _config["OpenAI:Endpoint"] ?? _config["AI:Endpoint"];
                var isOllamaEndpoint = !string.IsNullOrWhiteSpace(openAiEndpoint) && 
                                       (openAiEndpoint.Contains("localhost:11434", StringComparison.OrdinalIgnoreCase) ||
                                        openAiEndpoint.Contains("127.0.0.1:11434", StringComparison.OrdinalIgnoreCase) ||
                                        providerLower == "ollama");

                string apiKey;
                if (isOllamaEndpoint)
                {
                    // Ollama doesn't need a real API key, use dummy value
                    apiKey = "ollama-dummy-key";
                    
                    // Ensure Ollama endpoint ends with /v1
                    if (!string.IsNullOrWhiteSpace(openAiEndpoint) && !openAiEndpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    {
                        openAiEndpoint = openAiEndpoint.TrimEnd('/') + "/v1";
                    }
                }
                else
                {
                    // Real OpenAI endpoint needs real API key
                    apiKey = _config["Secrets:OpenAI:ApiKey"]
                            ?? _config["OpenAI:ApiKey"]
                            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                            ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(apiKey))
                    {
                        throw new InvalidOperationException("OpenAI API key not configured. Set Secrets:OpenAI:ApiKey or OPENAI_API_KEY.");
                    }
                }

                _logger?.LogInformation("Creazione kernel OpenAI (fallback) con modello {model} (endpoint={endpoint}, isOllama={isOllama})", 
                    model, openAiEndpoint ?? "default", isOllamaEndpoint);

                if (!string.IsNullOrWhiteSpace(openAiEndpoint))
                {
                    builder.AddOpenAIChatCompletion(modelId: model, apiKey: apiKey, endpoint: new Uri(openAiEndpoint), httpClient: _skHttpClient);
                }
                else
                {
                    builder.AddOpenAIChatCompletion(modelId: model, apiKey: apiKey, httpClient: _skHttpClient);
                }
            }
            // Istanze plugin registrate nel kernel. If allowedPlugins is provided, only register
            // the plugins whose alias is present in the list (best-effort matching using lowercase).
            System.Func<string, bool> allowed = (alias) =>
            {
                if (allowedPlugins == null) return true;
                try { foreach (var a in allowedPlugins) { if (string.Equals(a?.Trim(), alias, StringComparison.OrdinalIgnoreCase)) return true; } } catch { }
                return false;
            };

            var registeredAliases = new System.Collections.Generic.List<string>();
            TinyGenerator.Skills.MemorySkill? memSkill = null;
            TinyGenerator.Skills.StoryEvaluatorSkill? evSkill = null;
            TinyGenerator.Skills.StoryWriterSkill? writerSkill = null;
            TinyGenerator.Skills.AudioEvaluatorSkill? audioEvalSkill = null;
            if (allowed("text")) { builder.Plugins.AddFromObject(TextPlugin, "text"); _logger?.LogDebug("Registered plugin: {plugin}", TextPlugin?.GetType().FullName); registeredAliases.Add("text"); }
            if (allowed("math")) { builder.Plugins.AddFromObject(MathPlugin, "math"); _logger?.LogDebug("Registered plugin: {plugin}", MathPlugin?.GetType().FullName); registeredAliases.Add("math"); }
            if (allowed("time")) { builder.Plugins.AddFromObject(TimePlugin, "time"); _logger?.LogDebug("Registered plugin: {plugin}", TimePlugin?.GetType().FullName); registeredAliases.Add("time"); }
            if (allowed("filesystem")) { builder.Plugins.AddFromObject(FileSystemPlugin, "filesystem"); _logger?.LogDebug("Registered plugin: {plugin}", FileSystemPlugin?.GetType().FullName); registeredAliases.Add("filesystem"); }
            if (allowed("http")) { builder.Plugins.AddFromObject(HttpPlugin, "http"); _logger?.LogDebug("Registered plugin: {plugin}", HttpPlugin?.GetType().FullName); registeredAliases.Add("http"); }
            if (allowed("memory")) { memSkill = new TinyGenerator.Skills.MemorySkill(_memoryService, numericModelId, agentId); builder.Plugins.AddFromObject(memSkill, "memory"); _logger?.LogDebug("Registered plugin: {plugin}", memSkill?.GetType().FullName); registeredAliases.Add("memory"); }
            if (allowed("audiocraft")) { builder.Plugins.AddFromObject(AudioCraftSkill, "audiocraft"); _logger?.LogDebug("Registered plugin: {plugin}", AudioCraftSkill?.GetType().FullName); registeredAliases.Add("audiocraft"); }
            if (allowed("audioevaluator")) { audioEvalSkill = new TinyGenerator.Skills.AudioEvaluatorSkill(_httpClient); builder.Plugins.AddFromObject(audioEvalSkill, "audioevaluator"); _logger?.LogDebug("Registered plugin: {plugin}", audioEvalSkill?.GetType().FullName); registeredAliases.Add("audioevaluator"); }
            if (allowed("tts")) { builder.Plugins.AddFromObject(TtsApiSkill, "tts"); _logger?.LogDebug("Registered plugin: {plugin}", TtsApiSkill?.GetType().FullName); registeredAliases.Add("tts"); }
            // Register the StoryEvaluatorSkill which exposes evaluation functions used by texteval tests
            if (allowed("evaluator")) { evSkill = new TinyGenerator.Skills.StoryEvaluatorSkill(_database!, numericModelId, agentId); builder.Plugins.AddFromObject(evSkill, "evaluator"); _logger?.LogDebug("Registered plugin: {plugin}", evSkill?.GetType().FullName); registeredAliases.Add("evaluator"); }
            if (allowed("story")) { 
                // Lazy resolve StoriesService to avoid circular dependency
                var storiesService = _serviceProvider.GetService<StoriesService>();
                writerSkill = new TinyGenerator.Skills.StoryWriterSkill(storiesService, _database, numericModelId, agentId, model); 
                builder.Plugins.AddFromObject(writerSkill, "story"); 
                _logger?.LogDebug("Registered plugin: {plugin}", writerSkill?.GetType().FullName); 
                registeredAliases.Add("story"); 
            }

            var kernel = builder.Build();
            // Best-effort verification: log that kernel was created and which plugin instances we attached.
            try
            {
                // Log the aliases that were actually registered for this kernel (more accurate than listing all plugin instances)
                _logger?.LogInformation("Kernel created for model {model}. Registered plugin aliases: {plugins}", model, string.Join(", ", registeredAliases));
            }
            catch
            {
                // ignore any logging/inspection failures
            }

            // Best-effort: write a small debug JSON that records which plugins we attempted to allow and
            // which aliases were actually registered for this kernel. Non-throwing and best-effort.
            try
            {
                // Only write kernel debug JSON when enabled via configuration.
                var enabled = _config?.GetValue<bool?>("Debug:EnableOutboundGeneration") ?? true;
                if (enabled)
                {
                    try { System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Directory.GetCurrentDirectory(), "data")); } catch { }
                    var dbg = new
                    {
                        Timestamp = DateTime.UtcNow.ToString("o"),
                        Model = model,
                        Provider = provider,
                        AllowedPlugins = allowedPlugins == null ? null : allowedPlugins.ToArray(),
                        Registered = registeredAliases.ToArray(),
                        Endpoint = modelInfo?.Endpoint
                    };
                    var fname = $"sk_kernel_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.json";
                    var full = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "data", fname);
                    var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    var json = System.Text.Json.JsonSerializer.Serialize(dbg, opts);
                    System.IO.File.WriteAllText(full, json);
                }
            }
            catch { /* never fail kernel creation because of debug write */ }

            var kw = new KernelWithPlugins
            {
                Kernel = kernel,
                TextPlugin = allowed("text") ? this.TextPlugin : null,
                MathPlugin = allowed("math") ? this.MathPlugin : null,
                TimePlugin = allowed("time") ? this.TimePlugin : null,
                FileSystemPlugin = allowed("filesystem") ? this.FileSystemPlugin : null,
                HttpPlugin = allowed("http") ? this.HttpPlugin : null,
                MemorySkill = memSkill,
                StoryWriterSkill = writerSkill,
                StoryEvaluatorSkill = evSkill,
                AudioEvaluatorSkill = audioEvalSkill
            };
            return kw;
        }

        // Backwards-compatible IKernelFactory implementation that returns the Kernel instance
        Microsoft.SemanticKernel.Kernel IKernelFactory.CreateKernel(string? model, System.Collections.Generic.IEnumerable<string>? allowedPlugins)
        {
            var kw = CreateKernel(model, allowedPlugins, null);
            return kw?.Kernel!;
        }
    }
}
