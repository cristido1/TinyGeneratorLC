using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Sqlite;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using TinyGenerator.Skills;

namespace TinyGenerator.Services
{
    public class KernelWithPlugins
    {
        public Kernel? Kernel { get; set; }
        public TinyGenerator.Skills.TextPlugin? TextPlugin { get; set; }
        public TinyGenerator.Skills.MathSkill? MathSkill { get; set; }
        public TinyGenerator.Skills.TimeSkill? TimeSkill { get; set; }
        public TinyGenerator.Skills.FileSystemSkill? FileSystemSkill { get; set; }
        public TinyGenerator.Skills.HttpSkill? HttpSkill { get; set; }
        public TinyGenerator.Skills.MemorySkill? MemorySkill { get; set; }
        public TinyGenerator.Skills.StoryWriterSkill? StoryWriterSkill { get; set; }
        public TinyGenerator.Skills.AudioEvaluatorSkill? AudioEvaluatorSkill { get; set; }
        public TinyGenerator.Skills.StoryEvaluatorSkill? StoryEvaluatorSkill { get; set; }
        public TinyGenerator.Skills.TtsSchemaSkill? TtsSchemaSkill { get; set; }
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
        public TinyGenerator.Skills.MathSkill MathSkill { get; }
        public TinyGenerator.Skills.TimeSkill TimeSkill { get; }
        public TinyGenerator.Skills.FileSystemSkill FileSystemSkill { get; }
        public TinyGenerator.Skills.HttpSkill HttpSkill { get; }
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
            var customLogger = _serviceProvider?.GetService<ICustomLogger>();
            TextPlugin = new TinyGenerator.Skills.TextPlugin(customLogger);
            MathSkill = new TinyGenerator.Skills.MathSkill(customLogger);
            TimeSkill = new TinyGenerator.Skills.TimeSkill(customLogger);
            FileSystemSkill = new TinyGenerator.Skills.FileSystemSkill(customLogger);
            HttpSkill = new TinyGenerator.Skills.HttpSkill(customLogger);
            MemorySkill = new TinyGenerator.Skills.MemorySkill(_memoryService, null, null, customLogger);
            AudioCraftSkill = new TinyGenerator.Skills.AudioCraftSkill(_httpClient, _forceAudioCpu, customLogger);
            AudioEvaluatorSkill = new TinyGenerator.Skills.AudioEvaluatorSkill(_httpClient, customLogger);
            TtsApiSkill = new TinyGenerator.Skills.TtsApiSkill(_ttsHttpClient, customLogger);
            StoryEvaluatorSkill = new TinyGenerator.Skills.StoryEvaluatorSkill(_database, null, null, customLogger);
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
                            var modelInfo = _database?.GetModelInfoById(agent.ModelId.Value);
                            resolvedModel = modelInfo?.Name;
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

        public KernelWithPlugins CreateKernel(string? modelId = null, System.Collections.Generic.IEnumerable<string>? allowedPlugins = null, int? agentId = null, string? ttsStoryText = null, string? workingFolder = null)
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
            var customLoggerForKernel = _serviceProvider?.GetService<ICustomLogger>();
            TinyGenerator.Skills.MemorySkill? memSkill = null;
            TinyGenerator.Skills.StoryEvaluatorSkill? evSkill = null;
            TinyGenerator.Skills.StoryWriterSkill? writerSkill = null;
            TinyGenerator.Skills.AudioEvaluatorSkill? audioEvalSkill = null;
            TinyGenerator.Skills.TtsSchemaSkill? ttsSchemaSkill = null;
            if (allowed("text")) { builder.Plugins.AddFromObject(TextPlugin, "text"); _logger?.LogDebug("Registered plugin: {plugin}", TextPlugin?.GetType().FullName); registeredAliases.Add("text"); }
            if (allowed("math")) { var mathSkill = new TinyGenerator.Skills.MathSkill(customLoggerForKernel); ((ITinySkill)mathSkill).ModelId = modelInfo?.Id; ((ITinySkill)mathSkill).ModelName = modelInfo?.Name ?? model; builder.Plugins.AddFromObject(mathSkill, "math"); _logger?.LogDebug("Registered plugin: {plugin}", mathSkill?.GetType().FullName); registeredAliases.Add("math"); }
            if (allowed("time")) { var timeSkill = new TinyGenerator.Skills.TimeSkill(customLoggerForKernel); ((ITinySkill)timeSkill).ModelId = modelInfo?.Id; ((ITinySkill)timeSkill).ModelName = modelInfo?.Name ?? model; builder.Plugins.AddFromObject(timeSkill, "time"); _logger?.LogDebug("Registered plugin: {plugin}", timeSkill?.GetType().FullName); registeredAliases.Add("time"); }
            if (allowed("filesystem")) { var fsSkill = new TinyGenerator.Skills.FileSystemSkill(customLoggerForKernel); ((ITinySkill)fsSkill).ModelId = modelInfo?.Id; ((ITinySkill)fsSkill).ModelName = modelInfo?.Name ?? model; builder.Plugins.AddFromObject(fsSkill, "filesystem"); _logger?.LogDebug("Registered plugin: {plugin}", fsSkill?.GetType().FullName); registeredAliases.Add("filesystem"); }
            if (allowed("http")) { var httpSkill = new TinyGenerator.Skills.HttpSkill(customLoggerForKernel); ((ITinySkill)httpSkill).ModelId = modelInfo?.Id; ((ITinySkill)httpSkill).ModelName = modelInfo?.Name ?? model; builder.Plugins.AddFromObject(httpSkill, "http"); _logger?.LogDebug("Registered plugin: {plugin}", httpSkill?.GetType().FullName); registeredAliases.Add("http"); }
            if (allowed("memory")) { memSkill = new TinyGenerator.Skills.MemorySkill(_memoryService, modelInfo?.Id, agentId, customLoggerForKernel); ((ITinySkill)memSkill).ModelName = modelInfo?.Name ?? model; builder.Plugins.AddFromObject(memSkill, "memory"); _logger?.LogDebug("Registered plugin: {plugin}", memSkill?.GetType().FullName); registeredAliases.Add("memory"); }
            if (allowed("audiocraft")) { var audioSkill = new TinyGenerator.Skills.AudioCraftSkill(_httpClient, _forceAudioCpu, customLoggerForKernel); ((ITinySkill)audioSkill).ModelId = modelInfo?.Id; ((ITinySkill)audioSkill).ModelName = modelInfo?.Name ?? model; builder.Plugins.AddFromObject(audioSkill, "audiocraft"); _logger?.LogDebug("Registered plugin: {plugin}", audioSkill?.GetType().FullName); registeredAliases.Add("audiocraft"); }
            if (allowed("audioevaluator")) { audioEvalSkill = new TinyGenerator.Skills.AudioEvaluatorSkill(_httpClient, customLoggerForKernel); ((ITinySkill)audioEvalSkill).ModelId = modelInfo?.Id; ((ITinySkill)audioEvalSkill).ModelName = modelInfo?.Name ?? model; builder.Plugins.AddFromObject(audioEvalSkill, "audioevaluator"); _logger?.LogDebug("Registered plugin: {plugin}", audioEvalSkill?.GetType().FullName); registeredAliases.Add("audioevaluator"); }
            if (allowed("tts")) { var ttsSkill = new TinyGenerator.Skills.TtsApiSkill(_ttsHttpClient, customLoggerForKernel); ((ITinySkill)ttsSkill).ModelId = modelInfo?.Id; ((ITinySkill)ttsSkill).ModelName = modelInfo?.Name ?? model; builder.Plugins.AddFromObject(ttsSkill, "tts"); _logger?.LogDebug("Registered plugin: {plugin}", ttsSkill?.GetType().FullName); registeredAliases.Add("tts"); }
            if (allowed("ttsschema")) { 
                _logger?.LogInformation("TtsSchemaSkill allowed, registering with allowed={AllowedPlugins}", allowedPlugins == null ? "null" : string.Join(",", allowedPlugins));
                // TtsSchemaSkill uses test_run_folders as working directory for TTS tests, or a specific folder if provided
                var ttsFolderPath = workingFolder ?? Path.Combine(Directory.GetCurrentDirectory(), "test_run_folders");
                Directory.CreateDirectory(ttsFolderPath);
                ttsSchemaSkill = new TinyGenerator.Skills.TtsSchemaSkill(ttsFolderPath, ttsStoryText, customLoggerForKernel);
                ttsSchemaSkill.ModelId = modelInfo?.Id;
                ttsSchemaSkill.ModelName = modelInfo?.Name ?? model;
                
                try
                {
                    builder.Plugins.AddFromObject(ttsSchemaSkill, "ttsschema");
                    _logger?.LogInformation("TtsSchemaSkill successfully added to plugins");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to add TtsSchemaSkill: {Error}", ex.Message);
                }
                
                _logger?.LogInformation("Registered plugin: {plugin}", ttsSchemaSkill?.GetType().FullName);
                registeredAliases.Add("ttsschema");
            }
            else
            {
                _logger?.LogInformation("TtsSchemaSkill NOT allowed, allowedPlugins={AllowedPlugins}", allowedPlugins == null ? "null" : string.Join(",", allowedPlugins));
            }
            // Register the StoryEvaluatorSkill which exposes evaluation functions used by texteval tests
            if (allowed("evaluator")) { evSkill = new TinyGenerator.Skills.StoryEvaluatorSkill(_database!, modelInfo?.Id, agentId, customLoggerForKernel); ((ITinySkill)evSkill).ModelName = modelInfo?.Name ?? model; builder.Plugins.AddFromObject(evSkill, "evaluator"); _logger?.LogDebug("Registered plugin: {plugin}", evSkill?.GetType().FullName); registeredAliases.Add("evaluator"); }
            if (allowed("story")) { 
                // Lazy resolve StoriesService to avoid circular dependency
                var storiesService = _serviceProvider.GetService<StoriesService>();
                writerSkill = new TinyGenerator.Skills.StoryWriterSkill(storiesService, _database, modelInfo?.Id, agentId, modelInfo?.Name ?? model, customLoggerForKernel); 
                builder.Plugins.AddFromObject(writerSkill, "story"); 
                _logger?.LogDebug("Registered plugin: {plugin}", writerSkill?.GetType().FullName); 
                registeredAliases.Add("story"); 
            }

            var kernel = builder.Build();
            
            // CRITICAL: Verify that plugins were actually added to the kernel AFTER build
            try
            {
                var pluginsCount = kernel.Plugins.Count;
                var functionsMetadata = kernel.Plugins.GetFunctionsMetadata().ToList();
                _logger?.LogInformation("Kernel built for {model}: {pluginCount} plugins, {functionCount} functions total", 
                    model, pluginsCount, functionsMetadata.Count);
                
                foreach (var func in functionsMetadata)
                {
                    _logger?.LogInformation("  - Function: {plugin}/{name}", func.PluginName, func.Name);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to inspect kernel plugins: {error}", ex.Message);
            }
            
            // Add filters for tracking prompts and function invocations for live monitoring
            try
            {
                var customLogger = _serviceProvider.GetService<ICustomLogger>();
                if (customLogger != null)
                {
                    kernel.PromptRenderFilters.Add(new PromptRenderFilter(customLogger, model));
                    kernel.FunctionInvocationFilters.Add(new FunctionInvocationTrackerFilter(customLogger, model));
                }
            }
            catch
            {
                // best-effort: if filter registration fails, continue without it
            }
            
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
                MathSkill = allowed("math") ? this.MathSkill : null,
                TimeSkill = allowed("time") ? this.TimeSkill : null,
                FileSystemSkill = allowed("filesystem") ? this.FileSystemSkill : null,
                HttpSkill = allowed("http") ? this.HttpSkill : null,
                MemorySkill = memSkill,
                StoryWriterSkill = writerSkill,
                StoryEvaluatorSkill = evSkill,
                AudioEvaluatorSkill = audioEvalSkill,
                TtsSchemaSkill = ttsSchemaSkill,
                
            };
            return kw;
        }

        // Backwards-compatible IKernelFactory implementation that returns the Kernel instance
        Microsoft.SemanticKernel.Kernel IKernelFactory.CreateKernel(string? model, System.Collections.Generic.IEnumerable<string>? allowedPlugins, int? agentId = null, string? ttsStoryText = null, string? workingFolder = null)
        {
            var kw = CreateKernel(model, allowedPlugins, agentId, ttsStoryText, workingFolder);
            return kw?.Kernel!;
        }
    }
}
