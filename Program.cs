using System.IO;
using System.Diagnostics;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Memory;
using TinyGenerator;
using TinyGenerator.Services;
using TinyGenerator.Hubs;
using Microsoft.Extensions.Logging;

// Attempt to restart local Ollama with higher priority before app startup (best-effort).
// Small helper methods and more complex startup logic are extracted into Services/StartupTasks.cs

try
{
    // Attempt a best-effort restart early, keep original behaviour (best-effort & non-fatal)
    StartupTasks.TryRestartOllama();
}
catch (Exception ex)
{
    Console.WriteLine("[Startup] TryRestartOllama failed: " + ex.Message);
}

Console.WriteLine($"[Startup] Creating WebApplication builder at {DateTime.UtcNow:o}");
var builder = WebApplication.CreateBuilder(args);
// Enable provider validation in development to catch DI issues during Build
if (builder.Environment.IsDevelopment())
{
    builder.Host.UseDefaultServiceProvider((ctx, opts) => { opts.ValidateOnBuild = true; opts.ValidateScopes = true; });
}
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Debug);

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
// Controllers for API endpoints (e.g. /api/stories/..)
builder.Services.AddControllers();

// SignalR for live progress updates
builder.Services.AddSignalR();

// Tokenizer (try to use local tokenizer library if installed; fallback inside service)
builder.Services.AddSingleton<ITokenizer>(sp => new TokenizerService("cl100k_base"));

// === Semantic Kernel + memoria SQLite ===
// RIMOSSA la registrazione di IKernel: ora si usa solo Kernel reale tramite KernelFactory

// === Servizio di generazione storie ===
// Stories persistence service (requires DatabaseService)
builder.Services.AddSingleton<StoriesService>(sp => new StoriesService(sp.GetRequiredService<DatabaseService>(), sp.GetService<ILogger<StoriesService>>()));

// Persistent memory service (sqlite) using consolidated storage DB
builder.Services.AddSingleton<PersistentMemoryService>(sp => new PersistentMemoryService("data/storage.db"));
// Progress tracking for live UI updates (will broadcast over SignalR)
builder.Services.AddSingleton<ProgressService>();
// Notification service (broadcast to clients via SignalR)
builder.Services.AddSingleton<NotificationService>();

// Kernel factory (nuova DI)
builder.Services.AddSingleton<IKernelFactory, KernelFactory>();
// Also register concrete KernelFactory so services can depend on implementation-specific features
builder.Services.AddSingleton<KernelFactory>(sp => (KernelFactory)sp.GetRequiredService<IKernelFactory>());
builder.Services.AddTransient<StoryGeneratorService>();
builder.Services.AddTransient<PlannerExecutor>();
// Test execution service (per-step execution encapsulation)
builder.Services.AddTransient<ITestService, TestService>();

// Database access service + cost controller (sqlite) - register as factory to avoid heavy constructor work during registration
builder.Services.AddSingleton(sp => new DatabaseService("data/storage.db"));
// Configure custom logger options from configuration (section: CustomLogger)
builder.Services.Configure<CustomLoggerOptions>(builder.Configuration.GetSection("CustomLogger"));
// Register the async database-backed logger (ensure DatabaseService is available)
builder.Services.AddSingleton<ICustomLogger>(sp => new CustomLogger(sp.GetRequiredService<DatabaseService>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CustomLoggerOptions>>().Value));
// Register the CustomLoggerProvider and inject NotificationService so logs can be broadcast as notifications
// Register logger provider without resolving ICustomLogger immediately to avoid startup cycles.
builder.Services.AddSingleton<ILoggerProvider>(sp => new CustomLoggerProvider(sp));
// TTS service configuration: read HOST/PORT from environment with defaults
// Use localhost as default so HttpClient can reach the local TTS server.
var ttsHost = Environment.GetEnvironmentVariable("TTS_HOST") ?? Environment.GetEnvironmentVariable("HOST") ?? "127.0.0.1";
var ttsPortRaw = Environment.GetEnvironmentVariable("TTS_PORT") ?? Environment.GetEnvironmentVariable("PORT") ?? "8004";
if (!int.TryParse(ttsPortRaw, out var ttsPort)) ttsPort = 8004;
var ttsOptions = new TtsOptions { Host = ttsHost, Port = ttsPort };
// Allow overriding timeout via environment variable TTS_TIMEOUT_SECONDS (seconds)
var ttsTimeoutRaw = Environment.GetEnvironmentVariable("TTS_TIMEOUT_SECONDS");
if (!int.TryParse(ttsTimeoutRaw, out var ttsTimeout)) ttsTimeout = ttsOptions.TimeoutSeconds;
ttsOptions.TimeoutSeconds = ttsTimeout;
builder.Services.AddSingleton(ttsOptions);
builder.Services.AddHttpClient<TtsService>(client =>
{
    client.BaseAddress = new Uri(ttsOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(ttsOptions.TimeoutSeconds);
});

builder.Services.AddSingleton<CostController>(sp =>
    new CostController(
        sp.GetRequiredService<DatabaseService>(),
        sp.GetService<ITokenizer>()));

Console.WriteLine($"[Startup] About to call builder.Build() at {DateTime.UtcNow:o}");
var app = builder.Build();
Console.WriteLine($"[Startup] builder.Build() completed at {DateTime.UtcNow:o}");

// Run expensive initialization (database schema migrations, seeding) after the DI container
// is built to avoid blocking the `builder.Build()` call (which can resolve registered
// singletons/providers during build time). This helps reduce perceived startup time.
// Perform database initialization via helper
var dbInit = app.Services.GetService<TinyGenerator.Services.DatabaseService>();
var logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger("Startup");
StartupTasks.InitializeDatabaseIfNeeded(dbInit, logger);

// Startup model actions
// NOTE: The application does NOT run the function-calling capability tests at startup.
// What happens here at startup is a best-effort discovery of locally installed Ollama
// models: we call `PopulateLocalOllamaModelsAsync()` which queries `ollama list` /
// `ollama ps` and upserts basic metadata into the `models` table (name, provider,
// context, metadata). This is only for discovery and does NOT exercise model
// functions or plugins. Capability tests are run manually via the Models admin UI
// (the "Test function-calling" button) or by calling the Models test API endpoint.
// Populate local Ollama models (best-effort)
var cost = app.Services.GetService<TinyGenerator.Services.CostController>();
StartupTasks.PopulateLocalOllamaModelsIfNeededAsync(cost, logger).GetAwaiter().GetResult();

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
// Seed TTS voices via helper
var db = app.Services.GetService<TinyGenerator.Services.DatabaseService>();
var tts = app.Services.GetService<TinyGenerator.Services.TtsService>();
StartupTasks.SeedTtsVoicesIfNeededAsync(db, tts, builder.Configuration, logger).GetAwaiter().GetResult();

// Normalize any legacy test prompts at startup so prompts explicitly mention addin/library.function
// Normalize legacy test prompts using helper
StartupTasks.NormalizeTestPromptsIfNeeded(db, logger);

// Create a Semantic Kernel instance per active Agent and ensure each has persistent memory
// Ensure kernels for active agents and their memory collections
var kernelFactory = app.Services.GetService<TinyGenerator.Services.IKernelFactory>() as TinyGenerator.Services.KernelFactory;
var memoryService = app.Services.GetService<TinyGenerator.Services.PersistentMemoryService>();
StartupTasks.EnsureKernelsForActiveAgents(db, kernelFactory, memoryService, logger);

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
app.MapControllers();

// Minimal API endpoint for story evaluations (convenience for AJAX/UI)
app.MapGet("/api/v1/stories/{id:int}/evaluations", (int id, TinyGenerator.Services.StoriesService s) => Results.Json(s.GetEvaluationsForStory(id)));

app.Run();