using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TinyGenerator.Services;

namespace TinyGenerator
{
    public static class StartupTasks
    {
        public static void TryRestartOllama(ILogger? logger = null)
        {
            try
            {
                var scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "restart_ollama.sh");
                if (!File.Exists(scriptPath))
                {
                    scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "restart_ollama.sh");
                }
                if (!File.Exists(scriptPath))
                {
                    logger?.LogDebug("[Startup] restart_ollama.sh not found, skipping");
                    return;
                }

                var psi = new ProcessStartInfo("/bin/bash", $"-lc \"'{scriptPath}'\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    p.WaitForExit(15000);
                    var outText = p.StandardOutput.ReadToEnd();
                    var errText = p.StandardError.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(outText)) logger?.LogDebug("[Startup] restart_ollama stdout: {out}", outText);
                    if (!string.IsNullOrWhiteSpace(errText)) logger?.LogWarning("[Startup] restart_ollama stderr: {err}", errText);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning("[Startup] TryRestartOllama failure: {msg}", ex.Message);
            }
        }

        public static void InitializeDatabaseIfNeeded(DatabaseService? db, ILogger? logger = null)
        {
            try
            {
                if (db == null) return;
                logger?.LogInformation("[Startup] Initializing database schema...");
                db.Initialize();
                logger?.LogInformation("[Startup] Database schema initialization completed.");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Startup] Database initialization failed: {msg}", ex.Message);
            }
        }

        public static async Task PopulateLocalOllamaModelsIfNeededAsync(CostController? cost, ILogger? logger = null)
        {
            if (cost == null) return;
            try
            {
                logger?.LogInformation("[Startup] Populating local Ollama models...");
                var added = await cost.PopulateLocalOllamaModelsAsync();
                logger?.LogInformation("[Startup] Populated {count} local ollama models into models", added);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Startup] PopulateLocalOllamaModelsAsync failed: {msg}", ex.Message);
            }
        }

        public static async Task SeedTtsVoicesIfNeededAsync(DatabaseService? db, TtsService? tts, IConfiguration config, ILogger? logger = null)
        {
            try
            {
                if (db == null || tts == null) return;
                logger?.LogInformation("[Startup] Checking TTS voices count...");
                var current = db.GetTtsVoiceCount();
                if (current > 0)
                {
                    logger?.LogInformation("[Startup] Skipping TTS seed: tts_voices already contains {count} entries", current);
                    return;
                }
                logger?.LogInformation("[Startup] Seeding TTS voices from service...");
                var added = await db.AddOrUpdateTtsVoicesAsync(tts);
                logger?.LogInformation("[Startup] Added/Updated {count} TTS voices into tts_voices table", added);
                if (added == 0)
                {
                    var fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "tts_voices.json");
                    if (File.Exists(fallbackPath))
                    {
                        try
                        {
                            logger?.LogInformation("[Startup] Fallback seeding: reading {path}...", fallbackPath);
                            var json = File.ReadAllText(fallbackPath);
                            var added2 = db.AddOrUpdateTtsVoicesFromJsonString(json);
                            logger?.LogInformation("[Startup] Fallback: Added/Updated {count} TTS voices from {path}", added2, fallbackPath);
                        }
                        catch (Exception ex2)
                        {
                            logger?.LogWarning(ex2, "[Startup] Fallback seeding from tts_voices.json failed: {msg}", ex2.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Startup] AddOrUpdateTtsVoicesAsync failed: {msg}", ex.Message);
            }
        }

        public static void NormalizeTestPromptsIfNeeded(DatabaseService? db, ILogger? logger = null)
        {
            try
            {
                if (db == null) return;
                db.NormalizeTestPrompts();
                logger?.LogInformation("[Startup] Normalized test prompts.");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Startup] NormalizeTestPrompts failed: {msg}", ex.Message);
            }
        }

        public static void EnsureKernelsForActiveAgents(DatabaseService? db, KernelFactory? kernelFactory, PersistentMemoryService? memoryService, ILogger? logger = null)
        {
            if (db == null || kernelFactory == null || memoryService == null) return;
            try
            {
                var agents = db.ListAgents().Where(a => a.IsActive).ToList();
                logger?.LogInformation("[Startup] Found {count} active agents. Initializing kernels and persistent memory.", agents.Count);
                foreach (var a in agents)
                {
                    try
                    {
                        string? modelName = null;
                        if (a.ModelId.HasValue) modelName = db.GetModelNameById(a.ModelId.Value);

                        var aliases = new System.Collections.Generic.List<string>();
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(a.Skills))
                            {
                                var doc = System.Text.Json.JsonDocument.Parse(a.Skills);
                                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var el in doc.RootElement.EnumerateArray())
                                    {
                                        var s = el.GetString() ?? string.Empty;
                                        switch (s.Trim().ToLowerInvariant())
                                        {
                                            case "text": aliases.Add("text"); break;
                                            case "filesystem": aliases.Add("filesystem"); break;
                                            case "file": aliases.Add("filesystem"); break;
                                            case "files": aliases.Add("filesystem"); break;
                                            case "audiocraft": aliases.Add("audiocraft"); break;
                                            case "tts": aliases.Add("tts"); break;
                                            case "evaluator": aliases.Add("evaluator"); break;
                                            case "memory": aliases.Add("memory"); break;
                                            case "planner": aliases.Add("text"); break;
                                            case "textplugin": aliases.Add("text"); break;
                                            case "http": aliases.Add("http"); break;
                                            default:
                                                if (s.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0) aliases.Add("audiocraft");
                                                break;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }

                        if (!aliases.Contains("memory")) aliases.Add("memory");

                        logger?.LogInformation("[Startup] Initializing kernel for agent {agentId} ({name}) with model {model}...", a.Id, a.Name, modelName ?? "(default)");
                        kernelFactory.EnsureKernelForAgent(a.Id, modelName, aliases);
                        logger?.LogInformation("[Startup] Kernel initialized for agent {agentId} ({name}).", a.Id, a.Name);

                        try
                        {
                            var collection = $"agent_{a.Id}";
                            var marker = "agent-initialized";
                            memoryService.SaveAsync(collection, marker, new { agent = a.Name, ts = DateTime.UtcNow.ToString("o") }).GetAwaiter().GetResult();
                            logger?.LogInformation("[Startup] Memory collection ensured for agent {agentId} ({name}).", a.Id, a.Name);
                        }
                        catch (Exception memEx)
                        {
                            logger?.LogWarning(memEx, "[Startup] Failed to initialize memory for agent {name}: {msg}", a.Name, memEx.Message);
                        }
                    }
                    catch (Exception aex)
                    {
                        logger?.LogWarning(aex, "[Startup] Failed to initialize agent {name}: {msg}", a.Name, aex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Startup] Agent kernel initialization failed: {msg}", ex.Message);
            }
        }
    }
}
