using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using TinyGenerator.Services;
using ModelRecord = TinyGenerator.Models.ModelInfo;

namespace TinyGenerator.Pages
{
    [IgnoreAntiforgeryToken]
    public class ModelsModel : PageModel
    {
    private readonly DatabaseService _database;
    private readonly PersistentMemoryService _memory;
    private readonly IKernelFactory _kernelFactory;
    private readonly ProgressService _progress;
    private readonly CostController _costController;

        public ModelsModel( DatabaseService database, PersistentMemoryService memory, IKernelFactory kernelFactory, ProgressService progress, CostController costController)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _memory = memory;
            _kernelFactory = kernelFactory;
            _progress = progress;
            _costController = costController ?? throw new ArgumentNullException(nameof(costController));
        }

        private void AppendProgress(string runId, string message)
        {
            try
            {
                _progress?.Append(runId, message);
                Console.WriteLine($"[ModelsModel] {runId}: {message}");
            }
            catch { }
        }

    public List<ModelRecord> Models { get; set; } = new();

        public void OnGet()
        {
            // Read models directly from the database service for listing
            // Sort by FunctionCallingScore descending (higher first). For equal scores, prefer faster models (lower TestDurationSeconds).
            Models = _database.ListModels()
                .Where(m => m.Enabled)
                .OrderByDescending(m => m.FunctionCallingScore)
                .ThenBy(m => m.TestDurationSeconds.HasValue ? m.TestDurationSeconds.Value : double.PositiveInfinity)
                .ThenBy(m => m.Name ?? string.Empty)
                .ToList();
        }

        public class TestResultItem { public string name = ""; public bool ok; public string? message; public double durationSeconds; }
            public class TestResponse { public int functionCallingScore; public List<TestResultItem> results = new(); public double durationSeconds; }

    // POST handler to run function-calling tests for a model

    public async Task<IActionResult> OnPostTestModelAsync()
        {
            try
            {
                string model = string.Empty;
                string runId = string.Empty;

                // Support form posts (from the Models page buttons) or JSON body posts
                if (Request.HasFormContentType)
                {
                    model = Request.Form["model"].ToString() ?? string.Empty;
                    runId = Request.Form["runId"].ToString() ?? string.Empty;
                }
                else
                {
                    // Try read JSON body
                    Request.EnableBuffering();
                    using var sr = new StreamReader(Request.Body, leaveOpen: true);
                    var body = await sr.ReadToEndAsync();
                    Request.Body.Position = 0;
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("model", out var me)) model = me.GetString() ?? string.Empty;
                            if (root.TryGetProperty("runId", out var ri)) runId = ri.GetString() ?? string.Empty;
                        }
                        catch { }
                    }
                }

                if (string.IsNullOrWhiteSpace(runId)) runId = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(model)) return BadRequest("model required");

                // Diagnostic: log arrival of test request
                try { Console.WriteLine($"[ModelsModel] Received TestModel request: model={model}, runId={runId}, remote={HttpContext.Connection.RemoteIpAddress}"); } catch { }

                var results = new List<TestResultItem>();
                string? lastMusicFile = null;
                string? lastSoundFile = null;
                string? lastTtsFile = null;
                double externalSeconds = 0.0; // time spent in external services (downloads, direct synthesize)

                // Recupera info modello dalla tabella
                var modelInfo = _database.GetModelInfo(model);
                if (modelInfo == null)
                    return BadRequest($"Modello '{model}' non trovato nella tabella modelli.");
               
                // Cast esplicito per accedere alle proprietà dei plugin
                var factory = _kernelFactory as TinyGenerator.Services.KernelFactory;
                if (factory == null)
                    return BadRequest("KernelFactory non disponibile per test LastCalled.");

                // Start progress tracking
                try { _progress?.Start(runId); AppendProgress(runId, $"Starting tests for model {model}"); } catch { }

                // Reset LastCalled per tutti i plugin
                factory.MathPlugin.LastCalled = null;
                factory.TextPlugin.LastCalled = null;
                factory.TimePlugin.LastCalled = null;
                factory.FileSystemPlugin.LastCalled = null;
                factory.HttpPlugin.LastCalled = null;
                factory.AudioCraftSkill.LastCalled = null;

                // Measure kernel creation time to help diagnose slow startup overhead
                double kernelCreateSeconds = 0.0;
                Microsoft.SemanticKernel.Kernel? createdKernel = null;
                try
                {
                    var swk = System.Diagnostics.Stopwatch.StartNew();
                    createdKernel = factory.CreateKernel(model);
                    swk.Stop();
                    kernelCreateSeconds = swk.Elapsed.TotalSeconds;
                }
                catch (Exception ex)
                {
                    // Record a failed kernel creation as a test result item so the UI/database shows the issue
                    results.Add(new TestResultItem { name = "Kernel.Create", ok = false, message = ex.Message, durationSeconds = 0.0 });
                }

                if (createdKernel == null)
                {
                    // Persist what we have so the UI shows partial results
                    try
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(results);
                        _database.UpdateModelTestResults(modelInfo.Name, 0, new Dictionary<string, bool?>(), 0.0, json);
                    }
                    catch { }
                    return BadRequest("Impossibile creare un kernel reale per il modello selezionato (errore di configurazione o provider non supportato).");
                }

                // Add kernel creation timing as a test result so it appears in the per-test JSON summary
                results.Add(new TestResultItem { name = "Kernel.Create", ok = true, message = "Kernel created", durationSeconds = kernelCreateSeconds });
            

                // ✅ Crea l’agente e gli passa gli argomenti
                // Instruction: do NOT emit function-invocation JSON or textual function calls in the model response.
                // The agent should rely on the kernel's addins/skills invocation mechanism instead.
                var agentInstructions = "Use the kernel's skill/addin mechanism to invoke functions (do NOT emit JSON or textual function-call syntax in your response). Respond normally otherwise.";
                var agent = new ChatCompletionAgent("tester", createdKernel, agentInstructions, model ?? string.Empty);

                // Safe invoker with timeout to avoid hanging tests. Returns (completedSuccessfully, errorMessage, elapsedSeconds)
                async Task<(bool completed, string? error, double elapsedSeconds)> InvokeAgentSafe(string p, int timeoutMs = 30000)
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var task = agent.InvokeAsync(p);
                        var finished = await Task.WhenAny(task, Task.Delay(timeoutMs));
                        if (finished != task)
                        {
                            sw.Stop();
                            return (false, $"Timeout after {timeoutMs / 1000}s", sw.Elapsed.TotalSeconds);
                        }
                        // Await to observe exceptions
                        await task;
                        sw.Stop();
                        return (true, null, sw.Elapsed.TotalSeconds);
                    }
                    catch (Exception ex)
                    {
                        return (false, ex.Message, 0.0);
                    }
                }

                // Warm up the model with an initial prompt so the provider is activated before we start timing
                try
                {
                    AppendProgress(runId, "Warming up model with 'Pronto'...");
                    var _warm = await InvokeAgentSafe("Pronto", 30000);
                    if (!_warm.completed)
                    {
                        // If warmup times out or errors, record the warmup failure but DO NOT abort the test run.
                        // Some providers may return structured values that occasionally trigger conversion errors during warmup;
                        // record the failure and continue so the rest of the tests can still run and be persisted.
                        results.Add(new TestResultItem { name = "Warmup", ok = false, message = _warm.error, durationSeconds = _warm.elapsedSeconds });
                        AppendProgress(runId, $"Warmup failed (continuing): {_warm.error}");
                        try
                        {
                            var json = System.Text.Json.JsonSerializer.Serialize(results);
                            _database.UpdateModelTestResults(modelInfo.Name, 0, new Dictionary<string, bool?>(), 0.0, json);
                        }
                        catch { }
                        // continue with tests despite warmup failure
                    }
                    else
                    {
                        results.Add(new TestResultItem { name = "Warmup", ok = true, message = "Warmup completed", durationSeconds = _warm.elapsedSeconds });
                        AppendProgress(runId, $"Warmup completed in {_warm.elapsedSeconds:0.###}s");
                    }
                }
                catch (Exception ex)
                {
                    // Record warmup exception but continue with the test sequence. We want test runs to be resilient
                    // to provider conversion quirks so we can collect per-test timings and results.
                    results.Add(new TestResultItem { name = "Warmup", ok = false, message = ex.Message, durationSeconds = 0.0 });
                    AppendProgress(runId, $"Warmup threw exception (continuing): {ex.Message}");
                    try
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(results);
                        _database.UpdateModelTestResults(modelInfo.Name, 0, new Dictionary<string, bool?>(), 0.0, json);
                    }
                    catch { }
                    // do not return; continue with tests
                }

                // Start timing after warmup completes
                var testStart = DateTime.UtcNow;


                //var agent = new ChatCompletionAgent("tester", kernel, "Function calling tester", model ?? string.Empty);
                
                // Test MathPlugin: Add
                try
                {
                    var prompt = "Calcola 2+2 usando il la funzione add del plugin math";
                    AppendProgress(runId, "Running MathPlugin.Add test...");
                    var _resMath = await InvokeAgentSafe(prompt);
                    if (!_resMath.completed)
                    {
                        results.Add(new TestResultItem { name = "MathPlugin.Add", ok = false, message = _resMath.error, durationSeconds = _resMath.elapsedSeconds });
                    }
                    else
                    {
                        var called = factory.MathPlugin.LastCalled;
                        var ok = called == "Add";
                        AppendProgress(runId, $"MathPlugin.Add => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
                        results.Add(new TestResultItem { name = "MathPlugin.Add", ok = ok, message = ok ? "Chiamata Add eseguita" : ($"LastCalled: {called}"), durationSeconds = _resMath.elapsedSeconds });
                    }
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "MathPlugin.Add", ok = false, message = ex.Message }); }

                // Test TextPlugin: ToUpper
                try
                {
                    var prompt = "Converti in maiuscolo la parola test usando la funzione toupper del plugin text";
                    AppendProgress(runId, "Running TextPlugin.ToUpper test...");
                    var _resText = await InvokeAgentSafe(prompt);
                    if (!_resText.completed)
                    {
                        results.Add(new TestResultItem { name = "TextPlugin.ToUpper", ok = false, message = _resText.error, durationSeconds = _resText.elapsedSeconds });
                    }
                    else
                    {
                        var called = factory.TextPlugin.LastCalled;
                        var ok = called == "ToUpper";
                        AppendProgress(runId, $"TextPlugin.ToUpper => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
                        results.Add(new TestResultItem { name = "TextPlugin.ToUpper", ok = ok, message = ok ? "Chiamata ToUpper eseguita" : ($"LastCalled: {called}"), durationSeconds = _resText.elapsedSeconds });
                    }
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "TextPlugin.ToUpper", ok = false, message = ex.Message }); }

                // Test TimePlugin: Now
                try
                {
                    var prompt = "Che ora è? Usa la funzione now";
                    AppendProgress(runId, "Running TimePlugin.Now test...");
                    var _resTime = await InvokeAgentSafe(prompt);
                    if (!_resTime.completed)
                    {
                        results.Add(new TestResultItem { name = "TimePlugin.Now", ok = false, message = _resTime.error, durationSeconds = _resTime.elapsedSeconds });
                    }
                    else
                    {
                        var called = factory.TimePlugin.LastCalled;
                        var ok = called == "Now";
                        AppendProgress(runId, $"TimePlugin.Now => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
                        results.Add(new TestResultItem { name = "TimePlugin.Now", ok = ok, message = ok ? "Chiamata Now eseguita" : ($"LastCalled: {called}"), durationSeconds = _resTime.elapsedSeconds });
                    }
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "TimePlugin.Now", ok = false, message = ex.Message }); }

                // Test FileSystemPlugin: FileExists
                try
                {
                    var prompt = "Controlla se esiste il file /tmp/test.txt usando la funzione file_exists del plugin filesystem";
                    AppendProgress(runId, "Running FileSystemPlugin.FileExists test...");
                    var _resFs = await InvokeAgentSafe(prompt);
                    if (!_resFs.completed)
                    {
                        results.Add(new TestResultItem { name = "FileSystemPlugin.FileExists", ok = false, message = _resFs.error, durationSeconds = _resFs.elapsedSeconds });
                    }
                    else
                    {
                        var called = factory.FileSystemPlugin.LastCalled;
                        var ok = called == "FileExists";
                        AppendProgress(runId, $"FileSystemPlugin.FileExists => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
                        results.Add(new TestResultItem { name = "FileSystemPlugin.FileExists", ok = ok, message = ok ? "Chiamata FileExists eseguita" : ($"LastCalled: {called}"), durationSeconds = _resFs.elapsedSeconds });
                    }
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "FileSystemPlugin.FileExists", ok = false, message = ex.Message }); }

                // Test HttpPlugin: HttpGetAsync
                try
                {
                    var prompt = "Fai una richiesta GET a https://example.com usando la funzione http_get del plugin http";
                    AppendProgress(runId, "Running HttpPlugin.HttpGetAsync test...");
                    var _resHttp = await InvokeAgentSafe(prompt);
                    if (!_resHttp.completed)
                    {
                        results.Add(new TestResultItem { name = "HttpPlugin.HttpGetAsync", ok = false, message = _resHttp.error, durationSeconds = _resHttp.elapsedSeconds });
                    }
                    else
                    {
                        var called = factory.HttpPlugin.LastCalled;
                        var ok = called == "HttpGetAsync";
                        AppendProgress(runId, $"HttpPlugin.HttpGetAsync => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
                        results.Add(new TestResultItem { name = "HttpPlugin.HttpGetAsync", ok = ok, message = ok ? "Chiamata HttpGetAsync eseguita" : ($"LastCalled: {called}"), durationSeconds = _resHttp.elapsedSeconds });
                    }
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "HttpPlugin.HttpGetAsync", ok = false, message = ex.Message }); }

                // Test MemorySkill: Remember

                // Test MemorySkill: Remember, Recall, Forget sequenziale
                try
                {
                    // Reset
                    factory.MemorySkill.LastCalled = null;
                    factory.MemorySkill.LastCollection = null;
                    factory.MemorySkill.LastText = null;

                    var collection = $"test_mem_{Guid.NewGuid():N}";
                    var text = "valore di test memoria";

                    // Remember
                    var promptRemember = $"Ricorda la frase '{text}' nella collezione '{collection}' usando la funzione remember del plugin memory";
                    AppendProgress(runId, "MemorySkill.Remember test...");
                    var _resMemRemember = await InvokeAgentSafe(promptRemember);
                    if (!_resMemRemember.completed)
                    {
                        results.Add(new TestResultItem { name = "MemorySkill.Remember", ok = false, message = _resMemRemember.error, durationSeconds = _resMemRemember.elapsedSeconds });
                    }
                    else
                    {
                        var okRemember = factory.MemorySkill.LastCalled == "RememberAsync" && factory.MemorySkill.LastCollection == collection && factory.MemorySkill.LastText == text;
                        results.Add(new TestResultItem { name = "MemorySkill.Remember", ok = okRemember, message = okRemember ? "Chiamata RememberAsync eseguita" : ($"LastCalled: {factory.MemorySkill.LastCalled}, LastCollection: {factory.MemorySkill.LastCollection}, LastText: {factory.MemorySkill.LastText}"), durationSeconds = _resMemRemember.elapsedSeconds });
                    }

                    // Recall
                    factory.MemorySkill.LastCalled = null;
                    factory.MemorySkill.LastCollection = null;
                    factory.MemorySkill.LastText = null;
                    var promptRecall = $"Recupera la frase '{text}' dalla collezione '{collection}' usando la funzione recall del plugin memory";
                    AppendProgress(runId, "MemorySkill.Recall test...");
                    var _resMemRecall = await InvokeAgentSafe(promptRecall);
                    if (!_resMemRecall.completed)
                    {
                        results.Add(new TestResultItem { name = "MemorySkill.Recall", ok = false, message = _resMemRecall.error, durationSeconds = _resMemRecall.elapsedSeconds });
                    }
                    else
                    {
                        var okRecall = factory.MemorySkill.LastCalled == "RecallAsync" && factory.MemorySkill.LastCollection == collection && factory.MemorySkill.LastText == text;
                        results.Add(new TestResultItem { name = "MemorySkill.Recall", ok = okRecall, message = okRecall ? "Chiamata RecallAsync eseguita" : ($"LastCalled: {factory.MemorySkill.LastCalled}, LastCollection: {factory.MemorySkill.LastCollection}, LastText: {factory.MemorySkill.LastText}"), durationSeconds = _resMemRecall.elapsedSeconds });
                    }

                    // Forget
                    factory.MemorySkill.LastCalled = null;
                    factory.MemorySkill.LastCollection = null;
                    factory.MemorySkill.LastText = null;
                    var promptForget = $"Dimentica la frase '{text}' dalla collezione '{collection}' usando la funzione forget del plugin memory";
                    AppendProgress(runId, "MemorySkill.Forget test...");
                    var _resMemForget = await InvokeAgentSafe(promptForget);
                    if (!_resMemForget.completed)
                    {
                        results.Add(new TestResultItem { name = "MemorySkill.Forget", ok = false, message = _resMemForget.error, durationSeconds = _resMemForget.elapsedSeconds });
                    }
                    else
                    {
                        var okForget = factory.MemorySkill.LastCalled == "ForgetAsync" && factory.MemorySkill.LastCollection == collection && factory.MemorySkill.LastText == text;
                        results.Add(new TestResultItem { name = "MemorySkill.Forget", ok = okForget, message = okForget ? "Chiamata ForgetAsync eseguita" : ($"LastCalled: {factory.MemorySkill.LastCalled}, LastCollection: {factory.MemorySkill.LastCollection}, LastText: {factory.MemorySkill.LastText}"), durationSeconds = _resMemForget.elapsedSeconds });
                    }
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "MemorySkill.MemorySequence", ok = false, message = ex.Message }); }

                // AudioCraft tests: CheckHealth, ListModels, GenerateMusic, GenerateSound, DownloadFile
                try
                {
                    // Helper to run a single audio test safely and append results regardless of exceptions
                    async Task RunAudioTestAsync(string testName, string prompt, Func<bool> successPredicate, string successMessage, int timeoutMs = 30000)
                    {
                        try
                        {
                            AppendProgress(runId, $"Running AudioCraft.{testName} test...");
                            var _res = await InvokeAgentSafe(prompt, timeoutMs);
                            if (!_res.completed)
                            {
                                results.Add(new TestResultItem { name = $"AudioCraft.{testName}", ok = false, message = _res.error, durationSeconds = _res.elapsedSeconds });
                                return;
                            }

                            var called = factory.AudioCraftSkill.LastCalled;
                            var ok = false;
                            try { ok = successPredicate(); } catch { ok = false; }
                            AppendProgress(runId, $"AudioCraft.{testName} => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
                            results.Add(new TestResultItem { name = $"AudioCraft.{testName}", ok = ok, message = ok ? successMessage : ($"LastCalled: {called}"), durationSeconds = _res.elapsedSeconds });

                            // If generation succeeded, attempt to download and save the generated file into wwwroot
                            try
                            {
                                if (ok && string.Equals(testName, "GenerateMusic", StringComparison.OrdinalIgnoreCase))
                                {
                                    var remote = factory.AudioCraftSkill.LastGeneratedMusicFile;
                                    if (!string.IsNullOrWhiteSpace(remote))
                                    {
                                        try
                                        {
                                                var swExt = System.Diagnostics.Stopwatch.StartNew();
                                                var bytes = await factory.AudioCraftSkill.DownloadFileAsync(remote);
                                                swExt.Stop();
                                                externalSeconds += swExt.Elapsed.TotalSeconds;
                                            if (bytes != null && bytes.Length > 0)
                                            {
                                                var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "music_test");
                                                Directory.CreateDirectory(dir);
                                                var ext = Path.GetExtension(remote);
                                                if (string.IsNullOrWhiteSpace(ext)) ext = ".wav";
                                                var fname = $"{modelInfo.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext}";
                                                var full = Path.Combine(dir, fname);
                                                await System.IO.File.WriteAllBytesAsync(full, bytes);
                                                lastMusicFile = Path.Combine("music_test", fname).Replace('\\','/');
                                                AppendProgress(runId, $"Saved generated music to {lastMusicFile}");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            AppendProgress(runId, $"Failed to save generated music: {ex.Message}");
                                        }
                                    }
                                }
                                else if (ok && string.Equals(testName, "GenerateSound", StringComparison.OrdinalIgnoreCase))
                                {
                                    var remote = factory.AudioCraftSkill.LastGeneratedSoundFile;
                                    if (!string.IsNullOrWhiteSpace(remote))
                                    {
                                        try
                                        {
                                                var swExt = System.Diagnostics.Stopwatch.StartNew();
                                                var bytes = await factory.AudioCraftSkill.DownloadFileAsync(remote);
                                                swExt.Stop();
                                                externalSeconds += swExt.Elapsed.TotalSeconds;
                                            if (bytes != null && bytes.Length > 0)
                                            {
                                                var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "audio_test");
                                                Directory.CreateDirectory(dir);
                                                var ext = Path.GetExtension(remote);
                                                if (string.IsNullOrWhiteSpace(ext)) ext = ".wav";
                                                var fname = $"{modelInfo.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext}";
                                                var full = Path.Combine(dir, fname);
                                                await System.IO.File.WriteAllBytesAsync(full, bytes);
                                                lastSoundFile = Path.Combine("audio_test", fname).Replace('\\','/');
                                                AppendProgress(runId, $"Saved generated sound to {lastSoundFile}");
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            AppendProgress(runId, $"Failed to save generated sound: {ex.Message}");
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                        catch (Exception ex)
                        {
                            // Record the exception message and continue with next test
                            results.Add(new TestResultItem { name = $"AudioCraft.{testName}", ok = false, message = ex.Message });
                        }
                    }

                    // Run individual audio tests with safe wrapper
                    await RunAudioTestAsync("CheckHealth", "Verifica se AudioCraft è online usando la funzione check_health del plugin audiocraft", () => string.Equals(factory.AudioCraftSkill.LastCalled, nameof(TinyGenerator.Skills.AudioCraftSkill.CheckHealthAsync), StringComparison.OrdinalIgnoreCase), "Chiamata CheckHealth eseguita");
                    await RunAudioTestAsync("ListModels", "Elenca i modelli AudioCraft usando la funzione list_models del plugin audiocraft", () => string.Equals(factory.AudioCraftSkill.LastCalled, nameof(TinyGenerator.Skills.AudioCraftSkill.ListModelsAsync), StringComparison.OrdinalIgnoreCase), "Chiamata ListModels eseguita");
                    await RunAudioTestAsync("GenerateMusic", "Genera un breve frammento musicale usando la funzione generate_music del plugin audiocraft con durata 1", () => string.Equals(factory.AudioCraftSkill.LastCalled, nameof(TinyGenerator.Skills.AudioCraftSkill.GenerateMusicAsync), StringComparison.OrdinalIgnoreCase), "Chiamata GenerateMusic eseguita", timeoutMs: 180000);
                    await RunAudioTestAsync("GenerateSound", "Genera un breve effetto sonoro usando la funzione generate_sound del plugin audiocraft con durata 1", () => string.Equals(factory.AudioCraftSkill.LastCalled, nameof(TinyGenerator.Skills.AudioCraftSkill.GenerateSoundAsync), StringComparison.OrdinalIgnoreCase), "Chiamata GenerateSound eseguita", timeoutMs: 180000);
                    await RunAudioTestAsync("DownloadFile", "Scarica un file di esempio usando la funzione download_file del plugin audiocraft (nome file di test)", () => string.Equals(factory.AudioCraftSkill.LastCalled, nameof(TinyGenerator.Skills.AudioCraftSkill.DownloadFileAsync), StringComparison.OrdinalIgnoreCase), "Chiamata DownloadFile eseguita");
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "AudioCraft.Sequence", ok = false, message = ex.Message }); }

                // TTS tests: CheckHealth, ListVoices, Synthesize
                try
                {
                    async Task RunTtsTestAsync(string testName, string prompt, Func<bool> successPredicate, string successMessage)
                    {
                        try
                        {
                            AppendProgress(runId, $"Running TTS.{testName} test...");
                            var _res = await InvokeAgentSafe(prompt);
                            if (!_res.completed)
                            {
                                results.Add(new TestResultItem { name = $"Tts.{testName}", ok = false, message = _res.error, durationSeconds = _res.elapsedSeconds });
                                return;
                            }

                            var called = factory.TtsApiSkill.LastCalled;
                            var ok = false;
                            try { ok = successPredicate(); } catch { ok = false; }
                            AppendProgress(runId, $"Tts.{testName} => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
                            results.Add(new TestResultItem { name = $"Tts.{testName}", ok = ok, message = ok ? successMessage : ($"LastCalled: {called}"), durationSeconds = _res.elapsedSeconds });

                            // If synthesize succeeded, fetch bytes directly from the TTS client and save to wwwroot/tts_test
                            if (ok && string.Equals(testName, "Synthesize", StringComparison.OrdinalIgnoreCase))
                            {
                                    try
                                    {
                                        var swExt = System.Diagnostics.Stopwatch.StartNew();
                                        var bytes = await factory.TtsApiSkill.SynthesizeAsync("Prova TTS da TinyGenerator", "voice_templates", "template_alien", null, -1, null, "it", "neutral", 1.0, "wav");
                                        swExt.Stop();
                                        externalSeconds += swExt.Elapsed.TotalSeconds;
                                        if (bytes != null && bytes.Length > 0)
                                        {
                                            var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "tts_test");
                                            Directory.CreateDirectory(dir);
                                            var fname = $"{modelInfo.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.wav";
                                            var full = Path.Combine(dir, fname);
                                            await System.IO.File.WriteAllBytesAsync(full, bytes);
                                            lastTtsFile = Path.Combine("tts_test", fname).Replace('\\','/');
                                            AppendProgress(runId, $"Saved synthesized TTS to {lastTtsFile}");
                                        }
                                    }
                                catch (Exception ex)
                                {
                                    AppendProgress(runId, $"Failed to synthesize/save TTS: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            results.Add(new TestResultItem { name = $"Tts.{testName}", ok = false, message = ex.Message });
                        }
                    }

                    await RunTtsTestAsync("CheckHealth", "Verifica se il servizio TTS è online usando la funzione check_health del plugin tts", () => string.Equals(factory.TtsApiSkill.LastCalled, nameof(TinyGenerator.Skills.TtsApiSkill.CheckHealthAsync), StringComparison.OrdinalIgnoreCase), "Chiamata CheckHealth eseguita");
                    await RunTtsTestAsync("ListVoices", "Elenca le voci TTS usando la funzione list_voices del plugin tts", () => string.Equals(factory.TtsApiSkill.LastCalled, nameof(TinyGenerator.Skills.TtsApiSkill.ListVoicesAsync), StringComparison.OrdinalIgnoreCase), "Chiamata ListVoices eseguita");
                    await RunTtsTestAsync("Synthesize", "Sintetizza un breve testo usando la funzione synthesize del plugin tts", () => string.Equals(factory.TtsApiSkill.LastCalled, nameof(TinyGenerator.Skills.TtsApiSkill.SynthesizeAsync), StringComparison.OrdinalIgnoreCase), "Chiamata Synthesize eseguita");
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "Tts.Sequence", ok = false, message = ex.Message }); }

                // compute score: number of ok tests / total * 10 (guard divide by zero)
                var okCount = results.Count(r => r.ok);
                var score = results.Count > 0 ? (int)Math.Round((double)okCount / results.Count * 10) : 0;
                var skillFlagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MathPlugin.Add"] = "SkillAdd",
                    ["TextPlugin.ToUpper"] = "SkillToUpper",
                    ["TimePlugin.Now"] = "SkillNow",
                    ["FileSystemPlugin.FileExists"] = "SkillFileExists",
                    ["HttpPlugin.HttpGetAsync"] = "SkillHttpGet",
                    ["MemorySkill.Remember"] = "SkillRemember",
                    ["MemorySkill.Recall"] = "SkillRecall",
                    ["MemorySkill.Forget"] = "SkillForget"
                };
                // Add AudioCraft mappings
                skillFlagMap["AudioCraft.CheckHealth"] = "SkillAudioCheckHealth";
                skillFlagMap["AudioCraft.ListModels"] = "SkillAudioListModels";
                skillFlagMap["AudioCraft.GenerateMusic"] = "SkillAudioGenerateMusic";
                skillFlagMap["AudioCraft.GenerateSound"] = "SkillAudioGenerateSound";
                skillFlagMap["AudioCraft.DownloadFile"] = "SkillAudioDownloadFile";
                // TTS mappings
                skillFlagMap["Tts.CheckHealth"] = "SkillTtsCheckHealth";
                skillFlagMap["Tts.ListVoices"] = "SkillTtsListVoices";
                skillFlagMap["Tts.Synthesize"] = "SkillTtsSynthesize";
                var flagUpdates = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in skillFlagMap)
                {
                    var entry = results.FirstOrDefault(r => string.Equals(r.name, pair.Key, StringComparison.OrdinalIgnoreCase));
                    flagUpdates[pair.Value] = entry?.ok;
                }

                // Duration: compute total wall time and subtract external service time (downloads, synthesize)
                var testEnd = DateTime.UtcNow;
                var totalSeconds = (testEnd - testStart).TotalSeconds;
                var adjustedSeconds = totalSeconds - externalSeconds;
                if (adjustedSeconds < 0) adjustedSeconds = 0;

                // Persist adjusted duration (excluding external service time)
                _database.UpdateModelTestResults(modelInfo.Name, score, flagUpdates, adjustedSeconds, null, lastMusicFile, lastSoundFile, lastTtsFile);
                // Persist per-test results JSON so UI can show details after redirect
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(results);
                    _database.UpdateModelTestResults(modelInfo.Name, score, flagUpdates, adjustedSeconds, json, lastMusicFile, lastSoundFile, lastTtsFile);
                }
                catch { /* ignore persistence failures */ }

                // If the request was a form post (user clicked Test in the UI), set TempData so the result (including duration)
                // is shown after the redirect back to the Models page.
                try
                {
                    // Show both total wall time and adjusted time (excluding external services) for transparency
                    TempData["TestResultMessage"] = $"Model {modelInfo.Name}: Score {score}/10 — Duration: {adjustedSeconds:0.##}s (wall: {totalSeconds:0.##}s, external: {externalSeconds:0.###}s)";
                }
                catch { }

                try { AppendProgress(runId, $"All tests completed. Score: {score}/10"); _progress?.MarkCompleted(runId, score.ToString()); Console.WriteLine($"[ModelsModel] {runId}: Marked completed with score {score}"); } catch { }

                var resp = new TestResponse { functionCallingScore = score, results = results, durationSeconds = adjustedSeconds };
                var wrapper = new { runId = runId, result = resp };

                // If the request came from the Models page form, redirect back to the Models page
                if (Request.HasFormContentType)
                {
                    // Redirect to the Models page so the user returns to the list
                    return RedirectToPage("/Models");
                }

                return new JsonResult(wrapper);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // Return historical progress lines for a run id so clients can recover after reload
        public IActionResult OnGetProgressMessages(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return BadRequest("runId required");
            try
            {
                var list = _progress?.Get(runId) ?? new List<string>();
                var completed = _progress?.IsCompleted(runId) ?? false;
                var result = _progress?.GetResult(runId);
                return new JsonResult(new { runId = runId, messages = list, completed, result });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // Handler to update the ContextToUse value for a model (used for Ollama/local models inline edit)
        public IActionResult OnPostUpdateContext()
        {
            try
            {
                if (!Request.HasFormContentType) return BadRequest("form required");
                var form = Request.Form;
                var model = form["model"].ToString();
                var ctxStr = form["contextToUse"].ToString();

                if (string.IsNullOrWhiteSpace(model)) return BadRequest("model required");

                if (!int.TryParse(ctxStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ctx))
                {
                    TempData["TestResultMessage"] = $"Invalid context value: '{ctxStr}'";
                    return RedirectToPage("/Models");
                }

                var existing = _database.GetModelInfo(model) ?? new ModelRecord { Name = model };
                existing.ContextToUse = ctx;
                // Also update MaxContext if it was default or lower than submitted value (safe heuristic)
                if (existing.MaxContext <= 0 || existing.MaxContext < ctx) existing.MaxContext = ctx;
                _database.UpsertModel(existing);

                TempData["TestResultMessage"] = $"Updated context for {model} to {ctx} tokens.";
                return RedirectToPage("/Models");
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // Handler to discover local Ollama models and upsert any missing ones into the models table.
        public async Task<IActionResult> OnPostAddOllamaModelsAsync()
        {
            try
            {
                if (_costController == null) return BadRequest("CostController not available");
                var added = await _costController.PopulateLocalOllamaModelsAsync();
                TempData["TestResultMessage"] = $"Discovered and upserted {added} local Ollama model(s).";
                return RedirectToPage("/Models");
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // Handler to refresh contexts from running Ollama instances (uses `ollama ps` parsing)
        public async Task<IActionResult> OnPostRefreshContextsAsync()
        {
            try
            {
                var running = await OllamaMonitorService.GetRunningModelsAsync();
                if (running == null || running.Count == 0)
                {
                    TempData["TestResultMessage"] = "No running Ollama instances detected.";
                    return RedirectToPage("/Models");
                }

                var updated = 0;
                foreach (var r in running)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(r.Name)) continue;
                        // Try extract numeric digits from the Context field (best-effort)
                        var digits = new string((r.Context ?? string.Empty).Where(char.IsDigit).ToArray());
                        if (!int.TryParse(digits, out var ctx))
                        {
                            // If no numeric context found, skip updating context for this instance
                            continue;
                        }

                        var existing = _database.GetModelInfo(r.Name);
                        if (existing == null)
                        {
                            // Create a new model entry for this Ollama model
                            existing = new ModelRecord
                            {
                                Name = r.Name,
                                Provider = "ollama",
                                IsLocal = true,
                                MaxContext = ctx > 0 ? ctx : 4096,
                                ContextToUse = ctx > 0 ? ctx : 4096,
                                CostInPerToken = 0.0,
                                CostOutPerToken = 0.0,
                                LimitTokensDay = 0,
                                LimitTokensWeek = 0,
                                LimitTokensMonth = 0,
                                Metadata = System.Text.Json.JsonSerializer.Serialize(r),
                                Enabled = true
                            };
                            _database.UpsertModel(existing);
                            updated++;
                        }
                        else
                        {
                            // Update ContextToUse and ensure MaxContext is at least that value
                            if (existing.ContextToUse != ctx)
                            {
                                existing.ContextToUse = ctx;
                                if (existing.MaxContext <= 0 || existing.MaxContext < ctx) existing.MaxContext = ctx;
                                _database.UpsertModel(existing);
                                updated++;
                            }
                        }
                    }
                    catch { /* ignore per-model failures */ }
                }

                TempData["TestResultMessage"] = $"Refreshed contexts for {updated} running Ollama model(s).";
                return RedirectToPage("/Models");
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // Inline cost update handler (Cost per 1000 tokens, USD per 1k)
        public IActionResult OnPostUpdateCost()
        {
            try
            {
                if (!Request.HasFormContentType) return BadRequest("form required");
                var form = Request.Form;
                var model = form["model"].ToString();
                var inStr = form["costInPer1k"].ToString();
                var outStr = form["costOutPer1k"].ToString();

                if (string.IsNullOrWhiteSpace(model)) return BadRequest("model required");

                var existing = _database.GetModelInfo(model) ?? new ModelRecord { Name = model };

                if (!string.IsNullOrWhiteSpace(inStr))
                {
                    if (double.TryParse(inStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var vin))
                    {
                        existing.CostInPerToken = vin;
                    }
                }

                if (!string.IsNullOrWhiteSpace(outStr))
                {
                    if (double.TryParse(outStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var vout))
                    {
                        existing.CostOutPerToken = vout;
                    }
                }

                _database.UpsertModel(existing);

                // Redirect back to the Models page for the user
                return RedirectToPage("/Models");
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
