using System.IO;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Memory;
using TinyGenerator.Services;
using TinyGenerator.Hubs;
using Microsoft.Extensions.Logging;

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

// Kernel factory (nuova DI)
builder.Services.AddSingleton<IKernelFactory, KernelFactory>();
builder.Services.AddTransient<StoryGeneratorService>();
builder.Services.AddTransient<PlannerExecutor>();

// Database access service + cost controller (sqlite)
builder.Services.AddSingleton(new DatabaseService("data/storage.db"));
// Configure custom logger options from configuration (section: CustomLogger)
builder.Services.Configure<CustomLoggerOptions>(builder.Configuration.GetSection("CustomLogger"));
// Register the async database-backed logger (ensure DatabaseService is available)
builder.Services.AddSingleton<ICustomLogger>(sp => new CustomLogger(sp.GetRequiredService<DatabaseService>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CustomLoggerOptions>>().Value));
builder.Services.AddSingleton<ILoggerProvider, CustomLoggerProvider>();
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