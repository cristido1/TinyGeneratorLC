using System.IO;
using System.Diagnostics;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Memory;
using TinyGenerator.Services;
using TinyGenerator.Hubs;
using Microsoft.Extensions.Logging;

// Attempt to restart local Ollama with higher priority before app startup (best-effort).
static void TryRestartOllama()
{
    try
    {
        var scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "scripts", "restart_ollama.sh");
        if (!File.Exists(scriptPath))
        {
            // also try relative to executable folder
            scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "restart_ollama.sh");
        }
        if (!File.Exists(scriptPath))
        {
            Console.WriteLine("[Startup] restart_ollama.sh not found, skipping");
            return;
        }

        var psi = new ProcessStartInfo("/bin/bash", $"-lc \"\'{scriptPath}\'\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p != null)
        {
            // wait a short time but don't block startup indefinitely
            p.WaitForExit(15000);
            var outText = p.StandardOutput.ReadToEnd();
            var errText = p.StandardError.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(outText)) Console.WriteLine("[Startup] restart_ollama stdout: " + outText);
            if (!string.IsNullOrWhiteSpace(errText)) Console.WriteLine("[Startup] restart_ollama stderr: " + errText);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("[Startup] TryRestartOllama failure: " + ex.Message);
    }
}

try
{
    TryRestartOllama();
}
catch (Exception ex)
{
    Console.WriteLine("[Startup] TryRestartOllama failed: " + ex.Message);
}

var builder = WebApplication.CreateBuilder(args);

// Load secrets file (kept out of source control) if present.
var secretsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.secrets.json");
if (File.Exists(secretsPath))
{
    builder.Configuration.AddJsonFile(secretsPath, optional: false, reloadOnChange: true);
}

// The old sqlite logger provider has been replaced by a single async Database-backed logger.
// Remove the legacy registration to avoid duplicate writes.

// === Razor Pages ===
builder.Services.AddRazorPages();

// SignalR for live progress updates
builder.Services.AddSignalR();

// Tokenizer (try to use local tokenizer library if installed; fallback inside service)
builder.Services.AddSingleton<ITokenizer>(sp => new TokenizerService("cl100k_base"));

// === Semantic Kernel + memoria SQLite ===
// RIMOSSA la registrazione di IKernel: ora si usa solo Kernel reale tramite KernelFactory

// === Servizio di generazione storie ===
// Stories persistence service
builder.Services.AddSingleton<StoriesService>();

// Persistent memory service (sqlite)
var memory = new PersistentMemoryService("Data/memory.sqlite");
builder.Services.AddSingleton(memory);
// Progress tracking for live UI updates (will broadcast over SignalR)
builder.Services.AddSingleton<ProgressService>();
// Notification service (broadcast to clients via SignalR)
builder.Services.AddSingleton<NotificationService>();

// Kernel factory (nuova DI)
builder.Services.AddSingleton<IKernelFactory, KernelFactory>();
builder.Services.AddTransient<StoryGeneratorService>();
builder.Services.AddTransient<PlannerExecutor>();
// Test execution service (per-step execution encapsulation)
builder.Services.AddTransient<ITestService, TestService>();

// Database access service + cost controller (sqlite)
builder.Services.AddSingleton(new DatabaseService("data/storage.db"));
// Configure custom logger options from configuration (section: CustomLogger)
builder.Services.Configure<CustomLoggerOptions>(builder.Configuration.GetSection("CustomLogger"));
// Register the async database-backed logger (ensure DatabaseService is available)
builder.Services.AddSingleton<ICustomLogger>(sp => new CustomLogger(sp.GetRequiredService<DatabaseService>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CustomLoggerOptions>>().Value));
// Register the CustomLoggerProvider and inject NotificationService so logs can be broadcast as notifications
builder.Services.AddSingleton<ILoggerProvider>(sp => new CustomLoggerProvider(sp.GetRequiredService<ICustomLogger>(), sp.GetService<NotificationService>()));
// TTS service configuration: read HOST/PORT from environment with defaults
var ttsHost = Environment.GetEnvironmentVariable("HOST") ?? "0.0.0.0";
var ttsPortRaw = Environment.GetEnvironmentVariable("PORT") ?? Environment.GetEnvironmentVariable("TTS_PORT") ?? "8004";
if (!int.TryParse(ttsPortRaw, out var ttsPort)) ttsPort = 8004;
var ttsOptions = new TtsOptions { Host = ttsHost, Port = ttsPort };
builder.Services.AddSingleton(ttsOptions);
builder.Services.AddHttpClient<TtsService>(client =>
{
    client.BaseAddress = new Uri(ttsOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<CostController>(sp =>
    new CostController(
        sp.GetRequiredService<DatabaseService>(),
        sp.GetService<ITokenizer>()));

var app = builder.Build();

// Startup model actions
// NOTE: The application does NOT run the function-calling capability tests at startup.
// What happens here at startup is a best-effort discovery of locally installed Ollama
// models: we call `PopulateLocalOllamaModelsAsync()` which queries `ollama list` /
// `ollama ps` and upserts basic metadata into the `models` table (name, provider,
// context, metadata). This is only for discovery and does NOT exercise model
// functions or plugins. Capability tests are run manually via the Models admin UI
// (the "Test function-calling" button) or by calling the Models test API endpoint.
try
{
    var cost = app.Services.GetService<TinyGenerator.Services.CostController>();
    if (cost != null)
    {
        try
        {
            var added = cost.PopulateLocalOllamaModelsAsync().GetAwaiter().GetResult();
            Console.WriteLine($"[Startup] Populated {added} local ollama models into modelli");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] PopulateLocalOllamaModelsAsync failed: {ex.Message}");
        }
    }
}
catch { }

// Notify clients that the app is ready (best-effort: clients might not yet be connected)
try
{
    var notifier = app.Services.GetService<TinyGenerator.Services.NotificationService>();
    if (notifier != null)
    {
        _ = Task.Run(async () => { try { await notifier.NotifyAllAsync("App ready", "TinyGenerator is ready"); } catch { } });
    }
}
catch { }

// Seed TTS voices by calling the local TTS service and upserting
try
{
    var db = app.Services.GetService<TinyGenerator.Services.DatabaseService>();
    var tts = app.Services.GetService<TinyGenerator.Services.TtsService>();
        if (db != null && tts != null)
    {
        try
        {
                var current = db.GetTtsVoiceCount();
                if (current > 0)
                {
                    Console.WriteLine($"[Startup] Skipping TTS seed: tts_voices already contains {current} entries");
                }
                else
                {
                    var added = db.AddOrUpdateTtsVoicesAsync(tts).GetAwaiter().GetResult();
                    Console.WriteLine($"[Startup] Added/Updated {added} TTS voices into tts_voices table");
                    if (added == 0)
                    {
                        var fallbackPath = Path.Combine(builder.Environment.ContentRootPath, "data", "tts_voices.json");
                        if (File.Exists(fallbackPath))
                        {
                            try
                            {
                                var json = File.ReadAllText(fallbackPath);
                                var added2 = db.AddOrUpdateTtsVoicesFromJsonString(json);
                                Console.WriteLine($"[Startup] Fallback: Added/Updated {added2} TTS voices from {fallbackPath}");
                            }
                            catch (Exception ex2)
                            {
                                Console.WriteLine($"[Startup] Fallback seeding from tts_voices.json failed: {ex2.Message}");
                            }
                        }
                    }
                }
                // no-op: fallback handled above after AddOrUpdateTtsVoicesAsync
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] AddOrUpdateTtsVoicesAsync failed: {ex.Message}");
        }
    }
}
catch { }

// Normalize any legacy test prompts at startup so prompts explicitly mention addin/library.function
try
{
    var db = app.Services.GetService<TinyGenerator.Services.DatabaseService>();
    if (db != null)
    {
        try { db.NormalizeTestPrompts(); Console.WriteLine("[Startup] Normalized test prompts."); } catch { }
    }
}
catch { }

// Create a Semantic Kernel instance per active Agent and ensure each has persistent memory
try
{
    var db = app.Services.GetService<TinyGenerator.Services.DatabaseService>();
    var kernelFactory = app.Services.GetService<TinyGenerator.Services.IKernelFactory>() as TinyGenerator.Services.KernelFactory;
    var memoryService = app.Services.GetService<TinyGenerator.Services.PersistentMemoryService>();
    if (db != null && kernelFactory != null && memoryService != null)
    {
        try
        {
            var agents = db.ListAgents().Where(a => a.IsActive).ToList();
            Console.WriteLine($"[Startup] Found {agents.Count} active agents. Initializing kernels and persistent memory.");
            foreach (var a in agents)
            {
                try
                {
                    // Determine model name if model_id present
                    string? modelName = null;
                    if (a.ModelId.HasValue)
                    {
                        var mid = a.ModelId.Value;
                        modelName = db.GetModelNameById(mid);
                    }

                    // Parse skills JSON into plugin aliases
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
                                            // try map generic tokens
                                            if (s.IndexOf("audio", System.StringComparison.OrdinalIgnoreCase) >= 0) aliases.Add("audiocraft");
                                            break;
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    // Ensure memory plugin for every agent
                    if (!aliases.Contains("memory")) aliases.Add("memory");

                    kernelFactory.EnsureKernelForAgent(a.Id, modelName, aliases);

                    // Ensure a persistent memory collection exists for the agent
                    try
                    {
                        var collection = $"agent_{a.Id}";
                        var marker = "agent-initialized";
                        memoryService.SaveAsync(collection, marker, new { agent = a.Name, ts = System.DateTime.UtcNow.ToString("o") }).GetAwaiter().GetResult();
                    }
                    catch (Exception memEx)
                    {
                        Console.WriteLine($"[Startup] Failed to initialize memory for agent {a.Name}: {memEx.Message}");
                    }
                }
                catch (Exception aex)
                {
                    Console.WriteLine($"[Startup] Failed to initialize agent {a.Name}: {aex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Startup] Agent kernel initialization failed: {ex.Message}");
        }
    }
}
catch { }

// === Middleware ===
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

// SignalR hubs
app.MapHub<ProgressHub>("/progressHub");

app.MapRazorPages();

app.Run();