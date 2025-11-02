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
builder.Services.AddSingleton<IKernel>(sp =>
{
    var kernelBuilder = Kernel.CreateBuilder();
    // Use a specific local Ollama model instance (include model variant)
    kernelBuilder.AddOllamaChatCompletion(
        "llama3.1:8b",
        new Uri("http://127.0.0.1:11434") // Ollama locale
    );
    kernelBuilder.WithMemoryStorage(new SqliteMemoryStore("memoria_sk.db"));
    var kernel = kernelBuilder.Build();
    // Diagnostic: log kernel implementation type so we know whether the real SK is in use
    try { Console.WriteLine($"[Startup] Kernel implementation: {kernel.GetType().FullName}"); } catch { }
    return kernel;
});

// === Servizio di generazione storie ===
// Stories persistence service
builder.Services.AddSingleton<StoriesService>();

// Persistent memory service (sqlite)
builder.Services.AddSingleton<PersistentMemoryService>();
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