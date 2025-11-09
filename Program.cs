using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Memory;
using TinyGenerator.Services;
using TinyGenerator.Hubs;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// register sqlite logger provider so Microsoft logs are also persisted to storage.db
builder.Logging.AddProvider(new SqliteLoggerProvider("data/storage.db"));

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
// Planner executor
builder.Services.AddSingleton<PlannerExecutor>();

// Cost controller (sqlite) - create with tokenizer from DI
builder.Services.AddSingleton<TinyGenerator.Services.CostController>(sp =>
    new TinyGenerator.Services.CostController(sp.GetService<ITokenizer>()));

// Story generator depends on StoriesService
builder.Services.AddScoped<StoryGeneratorService>();

var app = builder.Build();

// Populate local Ollama models into the modelli table (best-effort)
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