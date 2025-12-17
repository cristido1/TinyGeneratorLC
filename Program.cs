using System.IO;
using System.Diagnostics;
using TinyGenerator;
using TinyGenerator.Services;
using Microsoft.AspNetCore.SignalR;
using TinyGenerator.Hubs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

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
// Session state for chat conversations
builder.Services.AddSession();
// Controllers for API endpoints (e.g. /api/stories/..)
builder.Services.AddControllers();

// SignalR for live progress updates
builder.Services.AddSignalR();
// Register a named HttpClient "default" and rely on IHttpClientFactory elsewhere.
builder.Services.AddHttpClient("default");

// Tokenizer (try to use local tokenizer library if installed; fallback inside service)
builder.Services.AddSingleton<ITokenizer>(sp => new TokenizerService("cl100k_base"));

// === Semantic Kernel + memoria SQLite ===
// RIMOSSA la registrazione di IKernel: ora si usa solo Kernel reale tramite KernelFactory

// Ollama monitor service (used for discovering/running local Ollama models)
// Instantiate without ICustomLogger to avoid circular dependency (DatabaseService -> IOllamaMonitorService -> ICustomLogger -> DatabaseService)
builder.Services.AddSingleton<IOllamaMonitorService>(sp => new OllamaMonitorService(null));

// Database access service + cost controller (sqlite) - register early so other services can depend on it
builder.Services.AddSingleton(sp => new TinyGenerator.Services.DatabaseService(
    "data/storage.db",
    sp.GetService<IOllamaMonitorService>(),
    sp));

// === Entity Framework Core DbContext ===
// Register EF Core DbContext with SQLite (same database as Dapper)
builder.Services.AddDbContext<TinyGenerator.Data.TinyGeneratorDbContext>(options =>
    options.UseSqlite("Data Source=data/storage.db"));

// Register generic repository pattern
builder.Services.AddScoped(typeof(TinyGenerator.Data.IRepository<>), typeof(TinyGenerator.Data.Repository<>));

// Register specific repositories
builder.Services.AddScoped<TinyGenerator.Data.Repositories.IStoryRepository, TinyGenerator.Data.Repositories.StoryRepository>();

// Persistent memory service (sqlite) using consolidated storage DB
builder.Services.AddSingleton<PersistentMemoryService>(sp =>
    new PersistentMemoryService(
        "data/storage.db",
        sp.GetService<IOptions<MemoryEmbeddingOptions>>(),
        sp.GetService<ICustomLogger>(),
        sp.GetRequiredService<TinyGenerator.Services.DatabaseService>()));
builder.Services.Configure<MemoryEmbeddingOptions>(builder.Configuration.GetSection("Memory:Embeddings"));
builder.Services.AddSingleton<IMemoryEmbeddingGenerator, OllamaEmbeddingGenerator>();
builder.Services.AddSingleton<MemoryEmbeddingBackfillService>();
builder.Services.AddSingleton<IMemoryEmbeddingBackfillScheduler>(sp => sp.GetRequiredService<MemoryEmbeddingBackfillService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<MemoryEmbeddingBackfillService>());
// Command dispatcher (background command queue with configurable parallelism)
builder.Services.Configure<CommandDispatcherOptions>(builder.Configuration.GetSection("CommandDispatcher"));
builder.Services.AddSingleton<CommandDispatcher>(sp =>
    new CommandDispatcher(
        sp.GetService<IOptions<CommandDispatcherOptions>>(),
        sp.GetService<ICustomLogger>(),
        sp.GetService<IHubContext<TinyGenerator.Hubs.ProgressHub>>()
    ));
builder.Services.AddSingleton<ICommandDispatcher>(sp => sp.GetRequiredService<CommandDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CommandDispatcher>());

// Sentiment mapping service (embedding-based + agent fallback)
builder.Services.AddSingleton<SentimentMappingService>(sp => new SentimentMappingService(
    sp.GetRequiredService<DatabaseService>(),
    sp.GetService<ICustomLogger>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<IHttpClientFactory>()));

// Stories persistence service (LangChain-first pipeline)
builder.Services.AddSingleton<StoriesService>(sp => new StoriesService(
    sp.GetRequiredService<DatabaseService>(), 
    sp.GetRequiredService<TtsService>(),
    sp.GetService<ILangChainKernelFactory>(),
    sp.GetService<ICustomLogger>(),
    sp.GetService<ILogger<StoriesService>>(),
    sp.GetService<ICommandDispatcher>(),
    sp.GetService<MultiStepOrchestrationService>(),
    sp.GetService<SentimentMappingService>()));
builder.Services.AddSingleton<LogAnalysisService>();

// Test execution service (LangChain-based, replaces deprecated SK TestService)
builder.Services.AddTransient<LangChainTestService>();

// === LangChain Services (NEW) ===
// Tool factory for creating LangChain tools and orchestrators
builder.Services.AddSingleton<LangChainToolFactory>(sp => new LangChainToolFactory(
    sp.GetRequiredService<PersistentMemoryService>(),
    sp.GetRequiredService<DatabaseService>(),
    sp.GetService<ICustomLogger>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("default"),
    () => sp.GetRequiredService<StoriesService>(),
    sp.GetRequiredService<TtsService>(),
    sp.GetService<IMemoryEmbeddingGenerator>(),
    sp.GetService<IMemoryEmbeddingBackfillScheduler>()));

// LangChain kernel factory (creates and caches orchestrators)
builder.Services.AddSingleton<LangChainKernelFactory>(sp => 
{
    var factory = new LangChainKernelFactory(
        builder.Configuration,
        sp.GetRequiredService<DatabaseService>(),
        sp.GetService<ICustomLogger>(),
        sp.GetRequiredService<LangChainToolFactory>());
    return factory;
});

builder.Services.AddSingleton<ILangChainKernelFactory>(sp => sp.GetRequiredService<LangChainKernelFactory>());

// LangChain agent service (retrieves agents and provides orchestrators)
builder.Services.AddSingleton<LangChainAgentService>(sp => new LangChainAgentService(
    sp.GetRequiredService<DatabaseService>(),
    sp.GetRequiredService<ILangChainKernelFactory>(),
    sp.GetService<ICustomLogger>()));

// LangChain test service (new testing framework using HybridLangChainOrchestrator)

// Multi-step task orchestration services (Sequential Multi-Step Prompting with Response Checker)
builder.Services.AddSingleton<ResponseCheckerService>();
builder.Services.AddSingleton<MultiStepOrchestrationService>();

// (DatabaseService already registered earlier)
// Configure custom logger options from configuration (section: AppLog)
builder.Services.Configure<CustomLoggerOptions>(builder.Configuration.GetSection("AppLog"));
// Register the async database-backed logger (ensure DatabaseService is available)
builder.Services.AddSingleton<ICustomLogger>(sp => new CustomLogger(
    sp.GetRequiredService<DatabaseService>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CustomLoggerOptions>>().Value,
    sp.GetService<IHubContext<TinyGenerator.Hubs.ProgressHub>>()));
// Register the CustomLoggerProvider (notifications are emitted through ICustomLogger)
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

// CostController removed - call tracking/cost controller disabled

// Ollama management service
builder.Services.AddSingleton<IOllamaManagementService, OllamaManagementService>();

Console.WriteLine($"[Startup] About to call builder.Build() at {DateTime.UtcNow:o}");
var app = builder.Build();
Console.WriteLine($"[Startup] builder.Build() completed at {DateTime.UtcNow:o}");

// Get logger early for startup operations
var logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger("Startup");

// Apply EF Core migrations automatically at startup
try
{
    logger?.LogInformation("[Startup] Applying EF Core migrations...");
    using (var scope = app.Services.CreateScope())
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<TinyGenerator.Data.TinyGeneratorDbContext>();
        dbContext.Database.Migrate();
        logger?.LogInformation("[Startup] EF Core migrations applied successfully");
    }
}
catch (Exception ex)
{
    logger?.LogError(ex, "[Startup] Failed to apply EF Core migrations: {msg}", ex.Message);
}

// Ensure the global ServiceLocator points to the DI DatabaseService so legacy/static code
// can route database access through the centralized semaphore-protected helpers.
// ServiceLocator removed: DatabaseService is now available via DI (IOllamaMonitorService, DatabaseService, etc.)

// Run expensive initialization (database schema migrations, seeding) after the DI container
// is built to avoid blocking the `builder.Build()` call (which can resolve registered
// singletons/providers during build time). This helps reduce perceived startup time.
// Perform database initialization via helper
var dbInit = app.Services.GetService<TinyGenerator.Services.DatabaseService>();
StartupTasks.InitializeDatabaseIfNeeded(dbInit, logger);

// Startup model actions
// NOTE: The application does NOT run the function-calling capability tests at startup.
// What happens here at startup is a best-effort discovery of locally installed Ollama
// models: we call `PopulateLocalOllamaModelsAsync()` which queries `ollama list` /
// `ollama ps` and upserts basic metadata into the `models` table (name, provider,
// context, metadata). This is only for discovery and does NOT exercise model
// functions or plugins. Capability tests are run manually via the Models admin UI
// (the "Test function-calling" button) or by calling the Models test API endpoint.
// Populate local Ollama models (best-effort) ONLY if the models table is empty to avoid
// overwriting or duplicating an already-populated models table on fresh startup.
var dbForModels = app.Services.GetService<TinyGenerator.Services.DatabaseService>();
try
{
    var modelCount = dbForModels?.ListModels().Count ?? 0;
        if (modelCount == 0)
        {
            logger?.LogInformation("[Startup] Models table empty — attempting to populate local Ollama models...");
            var ollamaMonitor = app.Services.GetService<TinyGenerator.Services.IOllamaMonitorService>();
            StartupTasks.PopulateLocalOllamaModelsIfNeededAsync(dbForModels, builder.Configuration, logger, ollamaMonitor).GetAwaiter().GetResult();
        }
    else
    {
        logger?.LogInformation("[Startup] Models table already contains {count} entries — skipping local model discovery.", modelCount);
    }
}
catch (Exception ex)
{
    logger?.LogWarning(ex, "[Startup] PopulateLocalOllamaModelsAsync failed: {msg}", ex.Message);
}

// Notify clients that the app is ready (best-effort: clients might not yet be connected)
try
{
    var eventLogger = app.Services.GetService<ICustomLogger>();
    if (eventLogger != null)
    {
        _ = Task.Run(async () => { try { await eventLogger.NotifyAllAsync("App ready", "TinyGenerator is ready"); } catch { } });
    }
}
catch { }

// Seed TTS voices by calling the local TTS service and upserting (fire-and-forget to avoid blocking startup)
var db = app.Services.GetService<TinyGenerator.Services.DatabaseService>();
var tts = app.Services.GetService<TinyGenerator.Services.TtsService>();
_ = Task.Run(() => StartupTasks.SeedTtsVoicesIfNeededAsync(db, tts, builder.Configuration, logger));

// Normalize any legacy test prompts at startup so prompts explicitly mention addin/library.function
// Normalize legacy test prompts using helper
StartupTasks.NormalizeTestPromptsIfNeeded(db, logger);
// Ensure evaluator agents have an example evaluate_full_story call in instructions
StartupTasks.EnsureEvaluatorInstructions(db, logger);

// Clean up old logs if log count exceeds threshold
// Automatically delete logs older than 7 days if total count > 1000
try
{
    db?.CleanupOldLogs(daysOld: 7, countThreshold: 1000);
}
catch (Exception ex)
{
    logger?.LogWarning(ex, "[Startup] Log cleanup failed: {msg}", ex.Message);
}

// === Middleware ===
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

// SignalR hubs
app.MapHub<ProgressHub>("/progressHub");

app.MapRazorPages();
app.MapControllers();

// Minimal API endpoint for story evaluations (convenience for AJAX/UI)
app.MapGet("/api/v1/stories/{id:int}/evaluations", (int id, TinyGenerator.Services.StoriesService s) => Results.Json(s.GetEvaluationsForStory(id)));
app.MapGet("/api/v1/models/busy", (ICustomLogger eventLogger) => Results.Json(eventLogger.GetBusyModelsSnapshot()));

app.Run();
