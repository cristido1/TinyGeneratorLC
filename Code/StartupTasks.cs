using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TinyGenerator.Services;
using TinyGenerator.Services.Commands;

namespace TinyGenerator
{
    public static class StartupTasks
    {
    // REMOVED: TryRestartOllama (Linux/Mac only - not compatible with Windows)
    // Use Ollama service management via OllamaMonitorService instead

    public static void ResetLlamaServer(IConfiguration? configuration, ILogger? logger = null)
    {
        if (configuration == null) return;

        var serverExe = configuration["LlamaCpp:ServerExe"] ?? "llama-server.exe";
        var processName = Path.GetFileNameWithoutExtension(serverExe);
        if (string.IsNullOrWhiteSpace(processName)) return;

        try
        {
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length == 0)
            {
                logger?.LogInformation("[Startup] No existing llama.cpp server processes found ({proc}).", processName);
                return;
            }

            foreach (var proc in procs)
            {
                try
                {
                    logger?.LogInformation("[Startup] Terminating existing llama.cpp server (PID={pid})", proc.Id);
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
                catch (Exception inner)
                {
                    logger?.LogWarning(inner, "[Startup] Failed to terminate llama.cpp server PID={pid}: {msg}", proc.Id, inner.Message);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[Startup] ResetLlamaServer failed: {msg}", ex.Message);
        }
    }

        public static async Task<bool> TryRestartAudioCraftAsync(IServiceHealthMonitor? healthMonitor = null, ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            try
            {
                logger?.LogInformation("[Startup] Tentativo di riavvio del servizio AudioCraft...");
                
                // Check if service is already running
                var isHealthy = healthMonitor != null 
                    ? await healthMonitor.CheckAudioCraftHealthAsync(cancellationToken)
                    : await CheckAudioCraftHealthAsync(logger, cancellationToken);
                if (isHealthy)
                {
                    logger?.LogInformation("[Startup] AudioCraft service is already running");
                    return true;
                }
                
                // Try PowerShell script first (Windows)
                var psScript = Path.Combine(Directory.GetCurrentDirectory(), "start_audiocraft.ps1");
                if (!File.Exists(psScript))
                {
                    psScript = Path.Combine(AppContext.BaseDirectory, "start_audiocraft.ps1");
                }
                
                if (File.Exists(psScript) && Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    logger?.LogInformation("[Startup] Using PowerShell script: {script}", psScript);
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-ExecutionPolicy Bypass -File \"{psScript}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(psScript)
                    };
                    
                    var p = Process.Start(psi);
                    if (p != null)
                    {
                        await p.WaitForExitAsync(cancellationToken);
                        var stdout = await p.StandardOutput.ReadToEndAsync();
                        var stderr = await p.StandardError.ReadToEndAsync();
                        
                        if (!string.IsNullOrWhiteSpace(stdout))
                            logger?.LogInformation("[Startup] PowerShell stdout: {out}", stdout);
                        if (!string.IsNullOrWhiteSpace(stderr))
                            logger?.LogWarning("[Startup] PowerShell stderr: {err}", stderr);
                        
                        return p.ExitCode == 0;
                    }
                }
                
                // Try Python script directly (Linux/Mac or fallback)
                var pythonScript = Path.Combine(Directory.GetCurrentDirectory(), "audiocraft_server.py");
                if (!File.Exists(pythonScript))
                {
                    pythonScript = Path.Combine(AppContext.BaseDirectory, "audiocraft_server.py");
                }
                
                ProcessStartInfo psiPython;
                if (File.Exists(pythonScript))
                {
                    logger?.LogInformation("[Startup] Using Python script: {script}", pythonScript);
                    
                    // Kill existing process if any
                    try
                    {
                        var existingProcs = Process.GetProcessesByName("python");
                        foreach (var proc in existingProcs)
                        {
                            try
                            {
                                var cmdLine = proc.MainModule?.FileName ?? string.Empty;
                                if (cmdLine.Contains("audiocraft", StringComparison.OrdinalIgnoreCase))
                                {
                                    logger?.LogInformation("[Startup] Terminating existing AudioCraft process PID={pid}", proc.Id);
                                    proc.Kill();
                                    proc.WaitForExit(5000);
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning("[Startup] Error killing existing AudioCraft process: {msg}", ex.Message);
                    }

                    // Start new process in background
                    psiPython = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"\"{pythonScript}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(pythonScript)
                    };
                    
                    var p = Process.Start(psiPython);
                    if (p != null)
                    {
                        logger?.LogInformation("[Startup] AudioCraft process started with PID={pid}", p.Id);
                        
                        // Wait for service to be ready (check health endpoint)
                        var maxWaitSeconds = 30;
                        for (int i = 0; i < maxWaitSeconds; i++)
                        {
                            await Task.Delay(1000, cancellationToken);
                            try
                            {
                                var ready = healthMonitor != null
                                    ? await healthMonitor.CheckAudioCraftHealthAsync(cancellationToken)
                                    : await CheckAudioCraftHealthAsync(logger, cancellationToken);
                                if (ready)
                                {
                                    logger?.LogInformation("[Startup] AudioCraft service is ready after {sec} seconds", i + 1);
                                    return true;
                                }
                            }
                            catch
                            {
                                // Keep waiting
                            }
                        }
                        
                        logger?.LogWarning("[Startup] AudioCraft process started but health check failed after 30s");
                        return false;
                    }
                }
                else
                {
                    logger?.LogWarning("[Startup] audiocraft_server.py not found, cannot restart service");
                }
                
                return false;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "[Startup] TryRestartAudioCraft failure: {msg}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// DEPRECATED: Use IServiceHealthMonitor.CheckAudioCraftHealthAsync() instead.
        /// This method is kept for backward compatibility but creates a new HttpClient on each call.
        /// </summary>
        public static async Task<bool> CheckAudioCraftHealthAsync(ILogger? logger = null, CancellationToken cancellationToken = default)
        {
            try
            {
                using var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var response = await httpClient.GetAsync("http://localhost:8003/health", cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                logger?.LogDebug("[Startup] AudioCraft health check failed: {msg}", ex.Message);
                return false;
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

                // If models table is empty, check for a seed SQL file and apply it automatically.
                try
                {
                    var modelCount = db.ListModels().Count;
                    if (modelCount == 0)
                    {
                        var seedPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "models_seed.sql");
                        if (File.Exists(seedPath))
                        {
                            logger?.LogInformation("[Startup] Models table empty — applying seed file {path}", seedPath);
                            db.ExecuteSqlScript(seedPath);
                        }
                        else
                        {
                            logger?.LogInformation("[Startup] Models table empty and no seed file found at {path}", seedPath);
                        }
                    }
                }
                catch (Exception exSeed)
                {
                    logger?.LogWarning(exSeed, "[Startup] Failed applying models seed: {msg}", exSeed.Message);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Startup] Database initialization failed: {msg}", ex.Message);
            }
        }

        public static async Task PopulateLocalOllamaModelsIfNeededAsync(DatabaseService? db, IConfiguration? config = null, ILogger? logger = null, IOllamaMonitorService? monitor = null, CancellationToken cancellationToken = default)
        {
            if (db == null) return;
            try
            {
                // Imposta l'endpoint Ollama da configurazione se disponibile
                if (config != null)
                {
                    var ollamaEndpoint = config["Ollama:endpoint"];
                    if (!string.IsNullOrWhiteSpace(ollamaEndpoint))
                    {
                        logger?.LogInformation("[Startup] Setting Ollama endpoint to: {endpoint}", ollamaEndpoint);
                        monitor?.SetOllamaEndpoint(ollamaEndpoint);
                    }
                }

                logger?.LogInformation("[Startup] Populating local Ollama models...");
                var added = await db.AddLocalOllamaModelsAsync();
                logger?.LogInformation("[Startup] Populated {count} local ollama models into models", added);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Startup] PopulateLocalOllamaModelsIfNeededAsync failed: {msg}", ex.Message);
            }
        }

        public static async Task SeedTtsVoicesIfNeededAsync(DatabaseService? db, TtsService? tts, IConfiguration config, ILogger? logger = null, CancellationToken cancellationToken = default)
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

                // Bound the seeding call so startup cannot hang if TTS is unreachable
                var seedTask = db.AddOrUpdateTtsVoicesAsync(tts);
                var completed = await Task.WhenAny(seedTask, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
                var added = 0;
                if (completed == seedTask)
                {
                    added = seedTask.Result;
                    logger?.LogInformation("[Startup] Added/Updated {count} TTS voices into tts_voices table", added);
                }
                else
                {
                    logger?.LogWarning("[Startup] TTS voice seeding timed out after 10s; skipping service seed");
                }

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

        /// <summary>
        /// Append an example evaluate_full_story call to evaluator agents' instructions if missing.
        /// </summary>
        public static void EnsureEvaluatorInstructions(DatabaseService? db, ILogger? logger = null)
        {
            if (db == null) return;
            const string snippet = @"Esempio di chiamata valida a evaluate_full_story:
evaluate_full_story({
  ""narrative_coherence_score"": 8,
  ""narrative_coherence_defects"": """",
  ""originality_score"": 7,
  ""originality_defects"": """",
  ""emotional_impact_score"": 6,
  ""emotional_impact_defects"": """",
  ""action_score"": 7,
  ""action_defects"": """"
});";

            try
            {
                var agents = db.ListAgents()?.Where(a =>
                    !string.IsNullOrWhiteSpace(a.Role)
                    && a.Role.Contains("evaluat", StringComparison.OrdinalIgnoreCase)
                    && !a.Role.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase))
                    .ToList() ?? new List<Models.Agent>();
                foreach (var agent in agents)
                {
                    var instr = agent.Instructions ?? string.Empty;
                    if (instr.IndexOf("evaluate_full_story", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        instr.IndexOf("action_score", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue; // already has an example or detailed guidance

                    agent.Instructions = string.IsNullOrWhiteSpace(instr)
                        ? snippet
                        : instr.TrimEnd() + "\n\n" + snippet;

                    db.UpdateAgent(agent);
                    logger?.LogInformation("[Startup] Added evaluate_full_story example to agent {Id} ({Name})", agent.Id, agent.Name);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Startup] Unable to append evaluator instructions: {msg}", ex.Message);
            }
        }

        /// <summary>
        /// Removes the legacy ResponseValidation rules snippet from agent instructions.
        /// Runtime validation is handled by the response_checker pipeline and must not be injected in requests.
        /// </summary>
        public static void EnsureResponseValidationRulesInAgentInstructions(DatabaseService? db, IConfiguration? configuration, ILogger? logger = null)
        {
            if (db == null) return;

            const string marker = "REGOLE DI VALIDAZIONE (response_checker)";
            const string note = "Nota: queste regole valgono sempre; se qualche istruzione sopra le contraddice, IGNORALA.";

            try
            {
                static string RemoveLegacyValidationSnippet(string text, string markerText, string noteText)
                {
                    if (string.IsNullOrWhiteSpace(text))
                        return text;

                    var output = text;
                    while (true)
                    {
                        var markerIdx = output.IndexOf(markerText, StringComparison.OrdinalIgnoreCase);
                        if (markerIdx < 0)
                            break;

                        var endIdx = -1;
                        var noteIdx = output.IndexOf(noteText, markerIdx, StringComparison.OrdinalIgnoreCase);
                        if (noteIdx >= 0)
                        {
                            endIdx = noteIdx + noteText.Length;
                            while (endIdx < output.Length && (output[endIdx] == '\r' || output[endIdx] == '\n'))
                            {
                                endIdx++;
                            }
                        }
                        else
                        {
                            var doubleNlIdx = output.IndexOf("\n\n", markerIdx, StringComparison.Ordinal);
                            endIdx = doubleNlIdx >= 0 ? doubleNlIdx + 2 : output.Length;
                        }

                        output = output.Remove(markerIdx, Math.Max(0, endIdx - markerIdx)).TrimEnd();
                    }
                    return output;
                }

                var agents = db.ListAgents()?.Where(a => a.IsActive).ToList() ?? new List<Models.Agent>();
                var updated = 0;

                foreach (var agent in agents)
                {
                    var instr = agent.Instructions ?? string.Empty;
                    var cleaned = RemoveLegacyValidationSnippet(instr, marker, note);
                    if (string.Equals(cleaned, instr, StringComparison.Ordinal))
                        continue;

                    agent.Instructions = cleaned;
                    db.UpdateAgent(agent);
                    updated++;
                }

                logger?.LogInformation("[Startup] EnsureResponseValidationRulesInAgentInstructions: removed legacy snippet from {count} agents", updated);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Startup] EnsureResponseValidationRulesInAgentInstructions failed: {msg}", ex.Message);
            }
        }

        /// <summary>
        /// Ensures serie_* agents have prompt templates with expected placeholders.
        /// Existing custom prompts are preserved; missing placeholders are appended.
        /// </summary>
        public static void EnsureSeriesAgentPromptTemplates(DatabaseService? db, ILogger? logger = null)
        {
            if (db == null) return;

            try
            {
                var agents = db.ListAgents()?.Where(a =>
                        a.IsActive &&
                        !string.IsNullOrWhiteSpace(a.Role) &&
                        SeriesPromptTemplates.RequiredPlaceholdersByRole.ContainsKey(a.Role!))
                    .ToList() ?? new List<Models.Agent>();

                var updated = 0;
                foreach (var agent in agents)
                {
                    var role = agent.Role!;
                    var requiredPlaceholders = SeriesPromptTemplates.RequiredPlaceholdersByRole[role];
                    var prompt = agent.Prompt ?? string.Empty;

                    var hasAllPlaceholders = requiredPlaceholders.All(p =>
                        prompt.IndexOf($"{{{{{p}}}}}", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hasAllPlaceholders)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(prompt))
                    {
                        agent.Prompt = SeriesPromptTemplates.GetDefaultTemplate(role);
                        db.UpdateAgent(agent);
                        updated++;
                        continue;
                    }

                    var sb = new StringBuilder(prompt.TrimEnd());
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.AppendLine("=== INPUT PLACEHOLDERS ===");
                    foreach (var placeholder in requiredPlaceholders)
                    {
                        if (prompt.IndexOf($"{{{{{placeholder}}}}}", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            continue;
                        }

                        sb.AppendLine($"{{{{{placeholder}}}}}");
                    }

                    agent.Prompt = sb.ToString().TrimEnd();
                    db.UpdateAgent(agent);
                    updated++;
                }

                if (updated > 0)
                {
                    logger?.LogInformation("[Startup] EnsureSeriesAgentPromptTemplates: updated {count} serie agent prompts", updated);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[Startup] EnsureSeriesAgentPromptTemplates failed: {msg}", ex.Message);
            }
        }

        // REMOVED: EnsureKernelsForActiveAgents - Deprecated Semantic Kernel (SK) infrastructure
        // All SK-based functionality has been replaced with LangChain integration
        // See: LangChainChatBridge.cs for current LLM orchestration
    }
}
