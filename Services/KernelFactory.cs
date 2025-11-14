using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Sqlite;

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
    }
    public class KernelFactory : IKernelFactory
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, KernelWithPlugins> _agentKernels = new();
        private readonly IConfiguration _config;
        private readonly ILogger<KernelFactory>? _logger;
        private readonly ILoggerFactory? _loggerFactory;
        private readonly TinyGenerator.Services.PersistentMemoryService _memoryService;
        private readonly DatabaseService _database;
        private readonly System.Net.Http.HttpClient _httpClient;
        private readonly System.Net.Http.HttpClient _ttsHttpClient;
    private readonly bool _forceAudioCpu;

        // Proprietà pubbliche per i plugin
        public TinyGenerator.Skills.TextPlugin TextPlugin { get; }
        public TinyGenerator.Skills.MathPlugin MathPlugin { get; }
        public TinyGenerator.Skills.TimePlugin TimePlugin { get; }
        public TinyGenerator.Skills.FileSystemPlugin FileSystemPlugin { get; }
        public TinyGenerator.Skills.HttpPlugin HttpPlugin { get; }
        public TinyGenerator.Skills.MemorySkill MemorySkill { get; }
        public TinyGenerator.Skills.AudioCraftSkill AudioCraftSkill { get; }
            public TinyGenerator.Skills.TtsApiSkill TtsApiSkill { get; }
        public TinyGenerator.Skills.StoryEvaluatorSkill StoryEvaluatorSkill { get; }

        public KernelFactory(
            IConfiguration config,
            TinyGenerator.Services.PersistentMemoryService memoryService,
            DatabaseService database,
            ILoggerFactory? loggerFactory = null,
            ILogger<KernelFactory>? logger = null)
        {
            _config = config;
            _logger = logger;
            _memoryService = memoryService;
            _httpClient = new System.Net.Http.HttpClient();
            _ttsHttpClient = new System.Net.Http.HttpClient();
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

            // Inizializzazione plugin
            TextPlugin = new TinyGenerator.Skills.TextPlugin();
            MathPlugin = new TinyGenerator.Skills.MathPlugin();
            TimePlugin = new TinyGenerator.Skills.TimePlugin();
            FileSystemPlugin = new TinyGenerator.Skills.FileSystemPlugin();
            HttpPlugin = new TinyGenerator.Skills.HttpPlugin();
            MemorySkill = new TinyGenerator.Skills.MemorySkill(_memoryService);
            AudioCraftSkill = new TinyGenerator.Skills.AudioCraftSkill(_httpClient, _forceAudioCpu);
            TtsApiSkill = new TinyGenerator.Skills.TtsApiSkill(_ttsHttpClient);
            StoryEvaluatorSkill = new TinyGenerator.Skills.StoryEvaluatorSkill();
        }

        // Ensure a kernel is created and cached for a given agent id. Allowed plugin aliases control which plugins are registered.
        public void EnsureKernelForAgent(int agentId, string? modelId, System.Collections.Generic.IEnumerable<string>? allowedPlugins = null)
        {
            try
            {
                // Create kernel using same factory method (which will attach plugin instances from this factory)
                var kernel = CreateKernel(modelId, allowedPlugins);
                var kw = new KernelWithPlugins
                {
                    Kernel = kernel,
                    TextPlugin = this.TextPlugin,
                    MathPlugin = this.MathPlugin,
                    TimePlugin = this.TimePlugin,
                    FileSystemPlugin = this.FileSystemPlugin,
                    HttpPlugin = this.HttpPlugin,
                    MemorySkill = this.MemorySkill
                };
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

        public Microsoft.SemanticKernel.Kernel CreateKernel(string? modelId = null, System.Collections.Generic.IEnumerable<string>? allowedPlugins = null)
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
                    builder.AddOpenAIChatCompletion(modelId: model, apiKey: apiKey, endpoint: new Uri(openAiEndpoint));
                }
                else
                {
                    builder.AddOpenAIChatCompletion(modelId: model, apiKey: apiKey);
                }
            }
            else
            {
                var endpoint = modelInfo?.Endpoint ?? _config["AI:Endpoint"] ?? "http://localhost:11434";
                _logger?.LogInformation("Creazione kernel Ollama con modello {model} su {endpoint}", model, endpoint);
                builder.AddOllamaChatCompletion(modelId: model, endpoint: new Uri(endpoint));
            }
            //builder.Plugins.AddFromType<Microsoft.SemanticKernel.Plugins.Core.FileIOPlugin>();
            //builder.Plugins.AddFromType<Microsoft.SemanticKernel.Plugins.Core.TextPlugin>();
            //builder.Plugins.AddFromType<Microsoft.SemanticKernel.Plugins.Core.TimePlugin>();
            //builder.Plugins.AddFromType<Microsoft.SemanticKernel.Plugins.Core.HttpPlugin>();
            //builder.Plugins.AddFromType<Microsoft.SemanticKernel.Plugins.Core.ConversationSummaryPlugin>();
            // Istanze plugin registrate nel kernel. If allowedPlugins is provided, only register
            // the plugins whose alias is present in the list (best-effort matching using lowercase).
            System.Func<string, bool> allowed = (alias) =>
            {
                if (allowedPlugins == null) return true;
                try { foreach (var a in allowedPlugins) { if (string.Equals(a?.Trim(), alias, StringComparison.OrdinalIgnoreCase)) return true; } } catch { }
                return false;
            };

            var registeredAliases = new System.Collections.Generic.List<string>();
            if (allowed("text")) { builder.Plugins.AddFromObject(TextPlugin, "text"); _logger?.LogDebug("Registered plugin: {plugin}", TextPlugin?.GetType().FullName); registeredAliases.Add("text"); }
            if (allowed("math")) { builder.Plugins.AddFromObject(MathPlugin, "math"); _logger?.LogDebug("Registered plugin: {plugin}", MathPlugin?.GetType().FullName); registeredAliases.Add("math"); }
            if (allowed("time")) { builder.Plugins.AddFromObject(TimePlugin, "time"); _logger?.LogDebug("Registered plugin: {plugin}", TimePlugin?.GetType().FullName); registeredAliases.Add("time"); }
            if (allowed("filesystem")) { builder.Plugins.AddFromObject(FileSystemPlugin, "filesystem"); _logger?.LogDebug("Registered plugin: {plugin}", FileSystemPlugin?.GetType().FullName); registeredAliases.Add("filesystem"); }
            if (allowed("http")) { builder.Plugins.AddFromObject(HttpPlugin, "http"); _logger?.LogDebug("Registered plugin: {plugin}", HttpPlugin?.GetType().FullName); registeredAliases.Add("http"); }
            if (allowed("memory")) { builder.Plugins.AddFromObject(MemorySkill, "memory"); _logger?.LogDebug("Registered plugin: {plugin}", MemorySkill?.GetType().FullName); registeredAliases.Add("memory"); }
            if (allowed("audiocraft")) { builder.Plugins.AddFromObject(AudioCraftSkill, "audiocraft"); _logger?.LogDebug("Registered plugin: {plugin}", AudioCraftSkill?.GetType().FullName); registeredAliases.Add("audiocraft"); }
            if (allowed("tts")) { builder.Plugins.AddFromObject(TtsApiSkill, "tts"); _logger?.LogDebug("Registered plugin: {plugin}", TtsApiSkill?.GetType().FullName); registeredAliases.Add("tts"); }
            // Register the StoryEvaluatorSkill which exposes evaluation functions used by texteval tests
            if (allowed("evaluator")) { builder.Plugins.AddFromObject(StoryEvaluatorSkill, "evaluator"); _logger?.LogDebug("Registered plugin: {plugin}", StoryEvaluatorSkill?.GetType().FullName); registeredAliases.Add("evaluator"); }

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

            return kernel;
        }
    }
}
