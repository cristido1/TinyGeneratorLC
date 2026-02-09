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

// REMOVED: StartupTasks.TryRestartOllama() - Linux/Mac only, replaced by OllamaMonitorService

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

// Scene renderer API client (image generation can take a while)
builder.Services.AddHttpClient<ImageService>(c =>
{
    c.Timeout = TimeSpan.FromMinutes(3);
});

// AudioCraft health check client (used by ServiceHealthMonitor)
builder.Services.AddHttpClient("AudioCraft", c =>
{
    c.Timeout = TimeSpan.FromSeconds(5);
});

// Service health monitor (for checking external services like AudioCraft)
builder.Services.AddSingleton<IServiceHealthMonitor, ServiceHealthMonitor>();

// Tokenizer (try to use local tokenizer library if installed; fallback inside service)
builder.Services.AddSingleton<ITokenizer>(sp => new TokenizerService("cl100k_base"));

// === LangChain Orchestration + memoria SQLite ===
// LangChain orchestrators managed by LangChainKernelFactory for all agents

// Ollama monitor service (used for discovering/running local Ollama models)
// Instantiate without ICustomLogger to avoid circular dependency (DatabaseService -> IOllamaMonitorService -> ICustomLogger -> DatabaseService)
builder.Services.AddSingleton<IOllamaMonitorService>(sp => new OllamaMonitorService(null));

// Database access service + cost controller (sqlite) - register early so other services can depend on it
builder.Services.AddSingleton(sp => new TinyGenerator.Services.DatabaseService(
    "data/storage.db",
    sp.GetService<IOllamaMonitorService>(),
    sp));

// Monotonic id allocator for logs (threadid) and story correlation (story_id)
builder.Services.AddSingleton<NumeratorService>();

// === Entity Framework Core DbContext ===
// Register EF Core DbContext with SQLite (same database as Dapper)
builder.Services.AddDbContext<TinyGenerator.Data.TinyGeneratorDbContext>(options =>
    options.UseSqlite("Data Source=data/storage.db"));

// Register generic repository pattern
builder.Services.AddScoped(typeof(TinyGenerator.Data.IRepository<>), typeof(TinyGenerator.Data.Repository<>));

// Register specific repositories
builder.Services.AddScoped<TinyGenerator.Data.Repositories.IStoryRepository, TinyGenerator.Data.Repositories.StoryRepository>();

// Model fallback service for agent resilience
builder.Services.AddScoped<ModelFallbackService>();

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
// Command policies/tuning/validation configuration
// Preferred layout: Commands:{ Defaults, ByCommand }
var commandsRoot = builder.Configuration.GetSection("Commands");
var byCommandSection = commandsRoot.GetSection("ByCommand");
if (commandsRoot.Exists() && byCommandSection.Exists())
{
    builder.Services.Configure<CommandPoliciesOptions>(options =>
    {
        commandsRoot.GetSection("Defaults:Policy").Bind(options.Default);
        foreach (var command in byCommandSection.GetChildren())
        {
            var policySection = command.GetSection("Policy");
            var policy = new CommandExecutionPolicy();
            var hasPolicyData = false;
            if (policySection.Exists())
            {
                policySection.Bind(policy);
                hasPolicyData = true;
            }

            // Flat config support under Commands:ByCommand:<key>:*
            // (kept alongside nested Policy for backward compatibility).
            command.Bind(policy);

            var flatTimeout = command.GetValue<int?>("TimeoutSec");
            var directTimeout = command.GetValue<int?>("timeoutSec");
            if (flatTimeout.HasValue)
            {
                policy.TimeoutSec = flatTimeout.Value;
                hasPolicyData = true;
            }
            // Backward compatibility: lowercase timeoutSec
            else if (directTimeout.HasValue)
            {
                policy.TimeoutSec = directTimeout.Value;
                hasPolicyData = true;
            }

            if (!hasPolicyData) continue;
            options.Commands[command.Key] = policy;
        }
    });

    builder.Services.Configure<ResponseValidationOptions>(options =>
    {
        commandsRoot.GetSection("Defaults:ResponseValidation").Bind(options);
        foreach (var command in byCommandSection.GetChildren())
        {
            var rvSection = command.GetSection("ResponseValidation");
            var policy = new ResponseValidationCommandPolicy();
            var hasRvData = false;
            if (rvSection.Exists())
            {
                rvSection.Bind(policy);
                hasRvData = true;
            }

            // Flat config support under Commands:ByCommand:<key>:*
            command.Bind(policy);
            if (command.GetValue<bool?>("EnableChecker").HasValue ||
                command.GetValue<int?>("MaxRetries").HasValue ||
                command.GetValue<bool?>("AskFailureReasonOnFinalFailure").HasValue ||
                command.GetSection("RuleIds").Exists())
            {
                hasRvData = true;
            }

            if (!hasRvData) continue;
            options.CommandPolicies[command.Key] = policy;
        }
    });

    builder.Services.Configure<CommandTuningOptions>(options =>
    {
        void BindTuning(string preferredKey, string legacyKey, object target)
        {
            var preferredRoot = byCommandSection.GetSection(preferredKey);
            var preferred = byCommandSection.GetSection($"{preferredKey}:Tuning");
            if (preferred.Exists())
            {
                preferred.Bind(target);
            }
            if (preferredRoot.Exists())
            {
                preferredRoot.Bind(target);
            }

            // Backward compatibility for legacy keys.
            var legacyRoot = byCommandSection.GetSection(legacyKey);
            var legacy = byCommandSection.GetSection($"{legacyKey}:Tuning");
            if (legacy.Exists())
            {
                legacy.Bind(target);
            }
            if (legacyRoot.Exists())
            {
                legacyRoot.Bind(target);
            }
        }

        BindTuning("add_ambient_tags_to_story", "AmbientExpert", options.AmbientExpert);
        BindTuning("add_fx_tags_to_story", "FxExpert", options.FxExpert);
        BindTuning("add_music_tags_to_story", "MusicExpert", options.MusicExpert);
        BindTuning("transform_story_raw_to_tagged", "TransformStoryRawToTagged", options.TransformStoryRawToTagged);
        BindTuning("generate_next_chunk", "GenerateNextChunk", options.GenerateNextChunk);
        BindTuning("planned_story", "PlannedStory", options.PlannedStory);
        BindTuning("full_story_pipeline", "FullStoryPipeline", options.FullStoryPipeline);
    });
}
else
{
    // Legacy sections
    builder.Services.Configure<CommandTuningOptions>(builder.Configuration.GetSection("CommandTuning"));
    builder.Services.Configure<ResponseValidationOptions>(builder.Configuration.GetSection("ResponseValidation"));
    builder.Services.Configure<CommandPoliciesOptions>(builder.Configuration.GetSection("CommandPolicies"));
}
builder.Services.Configure<StateDrivenStoryGenerationOptions>(builder.Configuration.GetSection("StateDrivenStoryGeneration"));
builder.Services.AddSingleton<TextValidationService>();
// TTS schema generation options (pause/gap between phrases)
builder.Services.Configure<TtsSchemaGenerationOptions>(builder.Configuration.GetSection("TtsSchemaGeneration"));
// Audio mix options (final mix volumes)
builder.Services.Configure<AudioMixOptions>(builder.Configuration.GetSection("AudioMix"));
builder.Services.Configure<SeriesGenerationOptions>(builder.Configuration.GetSection("Serie"));
// Audio generation options (autolaunch followups)
builder.Services.Configure<AudioGenerationOptions>(builder.Configuration.GetSection("AudioGeneration"));
// Automatic operations (auto enqueue when system idle)
builder.Services.Configure<AutomaticOperationsOptions>(builder.Configuration.GetSection("AutomaticOperations"));
builder.Services.Configure<StoryTaggingPipelineOptions>(builder.Configuration.GetSection("StoryTaggingPipeline"));
// Narrator voice options (default voice id)
builder.Services.Configure<NarratorVoiceOptions>(builder.Configuration.GetSection("NarratorVoice"));
// ResponseValidationOptions configured above (Commands:ResponseValidation preferred)
builder.Services.AddSingleton<CommandDispatcher>(sp =>
    new CommandDispatcher(
        sp.GetService<IOptions<CommandDispatcherOptions>>(),
        sp.GetService<IOptions<CommandPoliciesOptions>>(),
        sp.GetService<ICustomLogger>(),
        sp.GetService<IHubContext<TinyGenerator.Hubs.ProgressHub>>(),
        sp.GetService<NumeratorService>(),
        sp
    ));
builder.Services.AddSingleton<ICommandDispatcher>(sp => sp.GetRequiredService<CommandDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CommandDispatcher>());

// Auto-summarize service (batch summarization at startup and every hour)
builder.Services.AddHostedService<AutoSummarizeService>();
builder.Services.AddHostedService<AutomaticOperationsService>();
builder.Services.AddHostedService<AutoStateDrivenSeriesEpisodeService>();

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
    sp.GetService<SentimentMappingService>(),
    ttsSchemaOptions: sp.GetService<IOptionsMonitor<TtsSchemaGenerationOptions>>(),
    audioGenerationOptions: sp.GetService<IOptionsMonitor<AudioGenerationOptions>>(),
    audioMixOptions: sp.GetService<IOptionsMonitor<AudioMixOptions>>(),
    narratorVoiceOptions: sp.GetService<IOptionsMonitor<NarratorVoiceOptions>>(),
    idleAutoOptions: sp.GetService<IOptionsMonitor<AutomaticOperationsOptions>>(),
    scopeFactory: sp.GetService<IServiceScopeFactory>(),
    storyTaggingOptions: sp.GetService<IOptionsMonitor<StoryTaggingPipelineOptions>>(),
    healthMonitor: sp.GetService<IServiceHealthMonitor>()));
builder.Services.AddSingleton<LogAnalysisService>();
builder.Services.AddSingleton<SystemReportService>();
builder.Services.AddSingleton<CommandModelExecutionService>();

// Test execution service (LangChain-based, replaces deprecated SK TestService)
builder.Services.AddTransient<LangChainTestService>();
builder.Services.AddSingleton<JsonScoreTestService>();
builder.Services.AddSingleton<InstructionScoreTestService>();
builder.Services.AddSingleton<IntelligenceScoreTestService>();

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
builder.Services.AddSingleton<LlamaService>(sp => new LlamaService(
    sp.GetRequiredService<IConfiguration>(),
    sp.GetService<ICustomLogger>()));
builder.Services.AddSingleton<LangChainKernelFactory>(sp => 
{
    var factory = new LangChainKernelFactory(
        builder.Configuration,
        sp.GetRequiredService<DatabaseService>(),
        sp.GetService<ICustomLogger>(),
        sp.GetRequiredService<LangChainToolFactory>(),
        sp.GetService<IOllamaMonitorService>(),
        sp.GetService<LlamaService>(),
        sp);
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
}).AddTypedClient<TtsService>((httpClient, sp) =>
{
    var customLogger = sp.GetService<ICustomLogger>();
    return new TtsService(httpClient, ttsOptions, customLogger);
});

// CostController removed - call tracking/cost controller disabled

// Ollama management service
builder.Services.AddSingleton<IOllamaManagementService, OllamaManagementService>();

Console.WriteLine($"[Startup] About to call builder.Build() at {DateTime.UtcNow:o}");
var app = builder.Build();
Console.WriteLine($"[Startup] builder.Build() completed at {DateTime.UtcNow:o}");

// Get logger early for startup operations
var logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger("Startup");

// Ensure llama.cpp server is terminated at startup so we start from a clean slate.
StartupTasks.ResetLlamaServer(builder.Configuration, logger);

// Print site URL once the app is fully started
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        var urls = app.Urls;
        string displayed;
        if (urls != null && urls.Count > 0)
        {
            displayed = string.Join(", ", urls);
        }
        else
        {
            var envUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
            displayed = string.IsNullOrWhiteSpace(envUrls) ? "http://localhost:5000" : envUrls;
        }
        Console.WriteLine($"[Startup] App ready at: {displayed}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Startup] App ready (failed to read URLs): {ex.Message}");
    }
});

// Apply EF Core migrations automatically at startup
var enableEfMigrations = builder.Configuration.GetValue<bool?>("Database:EnableEfMigrations") ?? false;
if (enableEfMigrations)
{
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
}
else
{
    logger?.LogInformation("[Startup] EF Core migrations are disabled (Database:EnableEfMigrations=false)");
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

// Apply any pending manual migrations (explicit opt-in)
var enableManualMigrations = builder.Configuration.GetValue<bool?>("Database:EnableManualMigrations") ?? false;
if (enableManualMigrations)
{
    try
    {
        logger?.LogInformation("[Startup] Applying manual migrations...");
        dbInit?.ApplyPendingManualMigrations();
        logger?.LogInformation("[Startup] Manual migrations applied");
    }
    catch (Exception ex)
    {
        logger?.LogWarning(ex, "[Startup] Failed to apply manual migrations: {msg}", ex.Message);
    }
}
else
{
    logger?.LogInformation("[Startup] Manual migrations are disabled (Database:EnableManualMigrations=false)");
}

// Ensure series_folder structure exists and backfill series.folder values (best-effort)
try
{
    var updated = dbInit?.EnsureSeriesFoldersOnDisk(builder.Environment.ContentRootPath) ?? 0;
    if (updated > 0)
    {
        logger?.LogInformation("[Startup] Series folders ensured; updated {count} series folder values", updated);
    }
}
catch (Exception ex)
{
    logger?.LogWarning(ex, "[Startup] Failed to ensure series folders: {msg}", ex.Message);
}

// Initialize monotonic id generators once DB schema is ready.
try
{
    var numerator = app.Services.GetService<NumeratorService>();
    numerator?.InitializeFromDatabase();
}
catch (Exception ex)
{
    logger?.LogWarning(ex, "[Startup] NumeratorService initialization failed: {msg}", ex.Message);
}

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
// Remove legacy ResponseValidation snippet from agent instructions (do not inject checker rules in prompts)
StartupTasks.EnsureResponseValidationRulesInAgentInstructions(db, builder.Configuration, logger);

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
// Serve wwwroot as default static files
app.UseStaticFiles();
// Serve stories_folder as static files under /stories_folder
app.UseStaticFiles(new StaticFileOptions {
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(builder.Environment.ContentRootPath, "stories_folder")
    ),
    RequestPath = "/stories_folder"
});
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

// Optional one-time migration: prefix all existing story folders with their story id
try
{
    var doPrefix = Environment.GetEnvironmentVariable("PREFIX_STORY_FOLDERS");
    if (!string.IsNullOrWhiteSpace(doPrefix) && (doPrefix == "1" || doPrefix.Equals("true", StringComparison.OrdinalIgnoreCase)))
    {
        using (var scope = app.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetService<TinyGenerator.Services.StoriesService>();
            if (svc != null)
            {
                var migrated = svc.PrefixAllStoryFoldersWithIdAsync().GetAwaiter().GetResult();
                logger?.LogInformation("PrefixAllStoryFoldersWithIdAsync completed: {count} folders updated", migrated);
            }
        }
    }
}
catch (Exception ex)
{
    logger?.LogWarning(ex, "PREFIX_STORY_FOLDERS migration failed: {msg}", ex.Message);
}

// Final startup banner before the host begins accepting requests.
try
{
    var urls = app.Urls;
    var displayed = (urls != null && urls.Count > 0)
        ? string.Join(", ", urls)
        : (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000");
    Console.WriteLine($"[Startup] Startup tasks completed. About to listen on: {displayed}");
}
catch (Exception ex)
{
    Console.WriteLine($"[Startup] Startup tasks completed (failed to read URLs): {ex.Message}");
}

app.Run();
