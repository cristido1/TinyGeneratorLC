using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Memory;
using StoryGeneratorWeb.Services; // se hai messo StoryGeneratorService.cs sotto /Services

var builder = WebApplication.CreateBuilder(args);

// === Razor Pages ===
builder.Services.AddRazorPages();

// === Semantic Kernel + memoria SQLite ===
builder.Services.AddSingleton<IKernel>(sp =>
{
    var kernelBuilder = Kernel.CreateBuilder();
    kernelBuilder.AddOllamaChatCompletion(
        "llama3.1",
        new Uri("http://localhost:11434") // Ollama locale
    );
    kernelBuilder.WithMemoryStorage(new SqliteMemoryStore("memoria_sk.db"));
    return kernelBuilder.Build();
});

// === Servizio di generazione storie ===
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
app.MapRazorPages();

app.Run();