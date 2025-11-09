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
    public static class KernelFactory
    {
        /// <summary>
        /// Crea un kernel SK reale per OpenAI o Ollama secondo la configurazione.
        /// </summary>
        /// <param name="config">Configurazione contenente provider, modello, chiave API, endpoint</param>
        /// <param name="logger">Logger opzionale</param>
        /// <returns>Oggetto che implementa Microsoft.SemanticKernel.IKernel (o SimpleKernel come fallback)</returns>
    public static KernelWithPlugins? Create(IConfiguration config, ILogger? logger = null)
        {
            // Leggi provider e modello dalla configurazione
            var provider = config["provider"]?.ToLowerInvariant() ?? "";
            var model = config["model"] ?? config["Model"] ?? "";
            var endpoint = config["endpoint"] ?? config["Endpoint"] ?? "";
            var apiKey = config["apiKey"] ?? config["ApiKey"] ?? "";

            try
            {
                var builder = Microsoft.SemanticKernel.Kernel.CreateBuilder();
                // Crea istanze plugin
                var textPlugin = new TinyGenerator.Skills.TextPlugin();
                var mathPlugin = new TinyGenerator.Skills.MathPlugin();
                var timePlugin = new TinyGenerator.Skills.TimePlugin();
                var fileSystemPlugin = new TinyGenerator.Skills.FileSystemPlugin();
                var httpPlugin = new TinyGenerator.Skills.HttpPlugin();
                // Registra le istanze come plugin (tranne MemorySkill, che va creata dopo Build)
                builder.Plugins.AddFromObject(textPlugin, "TextSkill");
                builder.Plugins.AddFromObject(mathPlugin, "MathSkill");
                builder.Plugins.AddFromObject(timePlugin, "TimeSkill");
                builder.Plugins.AddFromObject(fileSystemPlugin, "FileSystem");
                builder.Plugins.AddFromObject(httpPlugin, "Http");

                // Recupera PersistentMemoryService da ServiceProvider globale
                var serviceProvider = builder.Services.BuildServiceProvider();
                var persistentMemory = serviceProvider.GetService(typeof(TinyGenerator.Services.PersistentMemoryService)) as TinyGenerator.Services.PersistentMemoryService;

                if (provider == "openai")
                {
                    // Usa OpenAI connector
                    var chat = !string.IsNullOrWhiteSpace(endpoint)
                        ? new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(model, apiKey, endpoint)
                        : new Microsoft.SemanticKernel.Connectors.OpenAI.OpenAIChatCompletionService(model, apiKey);
                    builder.Services.AddSingleton(chat.GetType(), chat);
                    var kernel = builder.Build();
                    // MemorySkill riceve PersistentMemoryService
                    var memorySkill = new TinyGenerator.Skills.MemorySkill(persistentMemory!);
                    kernel.Plugins.AddFromObject(memorySkill, "MemorySkill");
                    return new KernelWithPlugins {
                        Kernel = kernel,
                        TextPlugin = textPlugin,
                        MathPlugin = mathPlugin,
                        TimePlugin = timePlugin,
                        FileSystemPlugin = fileSystemPlugin,
                        HttpPlugin = httpPlugin,
                        MemorySkill = memorySkill
                    };
                }
                else if (provider == "ollama")
                {
                    var uri = !string.IsNullOrWhiteSpace(endpoint)
                        ? new Uri(endpoint)
                        : new Uri("http://localhost:11434");

                    builder.AddOllamaChatCompletion(
                        modelId: model,
                        endpoint: uri
                    );
                    var kernel = builder.Build();
                    var memorySkill = new TinyGenerator.Skills.MemorySkill(persistentMemory!);
                    kernel.Plugins.AddFromObject(memorySkill, "MemorySkill");
                    return new KernelWithPlugins {
                        Kernel = kernel,
                        TextPlugin = textPlugin,
                        MathPlugin = mathPlugin,
                        TimePlugin = timePlugin,
                        FileSystemPlugin = fileSystemPlugin,
                        HttpPlugin = httpPlugin,
                        MemorySkill = memorySkill
                    };
                }
                else
                {
                    logger?.LogWarning($"Provider '{provider}' non supportato.");
                    return null;
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Errore nella creazione del kernel SK: {Message}", ex.Message);
                return null;
            }
        }
    }
}
