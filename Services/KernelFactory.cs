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
        private readonly IConfiguration _config;
        private readonly ILogger<KernelFactory>? _logger;
        private readonly ILoggerFactory? _loggerFactory;
        private readonly TinyGenerator.Services.PersistentMemoryService _memoryService;
        private readonly DatabaseService _database;
        private readonly System.Net.Http.HttpClient _httpClient;
    private readonly bool _forceAudioCpu;

        // Proprietà pubbliche per i plugin
        public TinyGenerator.Skills.TextPlugin TextPlugin { get; }
        public TinyGenerator.Skills.MathPlugin MathPlugin { get; }
        public TinyGenerator.Skills.TimePlugin TimePlugin { get; }
        public TinyGenerator.Skills.FileSystemPlugin FileSystemPlugin { get; }
        public TinyGenerator.Skills.HttpPlugin HttpPlugin { get; }
        public TinyGenerator.Skills.MemorySkill MemorySkill { get; }
        public TinyGenerator.Skills.AudioCraftSkill AudioCraftSkill { get; }

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
        }

        public Kernel CreateKernel(string? modelId = null)
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

            // Istanze plugin registrate nel kernel
            builder.Plugins.AddFromObject(TextPlugin, "text");
            _logger?.LogDebug("Registered plugin: {plugin}", TextPlugin?.GetType().FullName);
            builder.Plugins.AddFromObject(MathPlugin, "math");
            _logger?.LogDebug("Registered plugin: {plugin}", MathPlugin?.GetType().FullName);
            builder.Plugins.AddFromObject(TimePlugin, "time");
            _logger?.LogDebug("Registered plugin: {plugin}", TimePlugin?.GetType().FullName);
            builder.Plugins.AddFromObject(FileSystemPlugin, "filesystem");
            _logger?.LogDebug("Registered plugin: {plugin}", FileSystemPlugin?.GetType().FullName);
            builder.Plugins.AddFromObject(HttpPlugin, "http");
            _logger?.LogDebug("Registered plugin: {plugin}", HttpPlugin?.GetType().FullName);
            builder.Plugins.AddFromObject(MemorySkill, "memory");
            _logger?.LogDebug("Registered plugin: {plugin}", MemorySkill?.GetType().FullName);
            builder.Plugins.AddFromObject(AudioCraftSkill, "audiocraft");
            _logger?.LogDebug("Registered plugin: {plugin}", AudioCraftSkill?.GetType().FullName);

            var kernel = builder.Build();

            // Best-effort verification: log that kernel was created and which plugin instances we attached.
            try
            {
                _logger?.LogInformation("Kernel created for model {model}. Plugins attached: {plugins}", model, string.Join(", ", new[] {
                    TextPlugin?.GetType().Name,
                    MathPlugin?.GetType().Name,
                    TimePlugin?.GetType().Name,
                    FileSystemPlugin?.GetType().Name,
                    HttpPlugin?.GetType().Name,
                    MemorySkill?.GetType().Name,
                    AudioCraftSkill?.GetType().Name
                }));
            }
            catch
            {
                // ignore any logging/inspection failures
            }

            return kernel;
        }
    }
}
