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
        public List<string> TestGroups { get; set; } = new();
        // Whether to show disabled models in the list (controlled by a GET parameter)
        [BindProperty(SupportsGet = true)]
        public bool ShowDisabled { get; set; } = false;
        private readonly DatabaseService _database;
        private readonly PersistentMemoryService _memory;
        private readonly IKernelFactory _kernelFactory;
        private readonly ProgressService _progress;
        private readonly NotificationService _notifications;
        private readonly ITestService _testService;
        private readonly CostController _costController;

        public ModelsModel(DatabaseService database, PersistentMemoryService memory, IKernelFactory kernelFactory, ProgressService progress, CostController costController, ITestService testService, NotificationService notifications)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _memory = memory;
            _kernelFactory = kernelFactory;
            _progress = progress;
            _costController = costController ?? throw new ArgumentNullException(nameof(costController));
            _testService = testService ?? throw new ArgumentNullException(nameof(testService));
            _notifications = notifications;
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

        // Try to extract a readable chat/text response from a raw agent result string.
        // This is best-effort: try JSON parsing first, then common key lookup, then regex extraction.
        private static string? ExtractChatText(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            // Attempt JSON parse
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                // Look for common fields
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) return c.GetString();
                    if (root.TryGetProperty("message", out var m))
                    {
                        if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String) return mc.GetString();
                        if (m.ValueKind == JsonValueKind.String) return m.GetString();
                    }
                    if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
                    if (root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.String) return r.GetString();
                    if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var first = choices[0];
                        if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        {
                            return content.GetString();
                        }
                    }
                }
            }
            catch { /* not JSON */ }

            // Try simple regex patterns for common ToString() representations like "Content = '...'" or "content: '...'"
            try
            {
                var s = raw;
                // Content = "..."
                var m = System.Text.RegularExpressions.Regex.Match(s, @"Content\s*=\s*""(?<c>[^""]+)""");
                if (m.Success) return m.Groups["c"].Value;
                m = System.Text.RegularExpressions.Regex.Match(s, @"content\s*[:=]\s*""(?<c>[^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups["c"].Value;
                // Fallback: if the raw string contains 'ChatMessageContent' try to pull the trailing text
                m = System.Text.RegularExpressions.Regex.Match(s, @"ChatMessageContent\s*\{\s*Content\s*=\s*""(?<c>[^""]+)""", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (m.Success) return m.Groups["c"].Value;
            }
            catch { }

            // As last resort, do NOT return the raw type-name; if we can't extract a chat string, return null
            // This avoids showing meaningless ToString() values like "Microsoft.SemanticKernel.Agents.ChatResult[]".
            return null;
        }

        // Flatten an arbitrary agent result object into a best-effort textual representation.
        // Handles strings, IEnumerable, JsonElement and uses reflection to find common text fields.
        private static string? FlattenResultToText(object? result)
        {
            if (result == null) return null;
            try
            {
                try
                {
                    // If it's already a string
                    if (result is string s)
                    {
                        return string.IsNullOrWhiteSpace(s) ? null : (s.Length > 16000 ? s.Substring(0, 16000) + "..." : s);
                    }

                    // JsonElement handling
                    if (result is System.Text.Json.JsonElement je)
                    {
                        if (je.ValueKind == JsonValueKind.String) return je.GetString();
                        try { return System.Text.Json.JsonSerializer.Serialize(je); } catch { return je.ToString(); }
                    }

                    // IEnumerable (but not string)
                    if (result is System.Collections.IEnumerable ie && !(result is System.Collections.IDictionary))
                    {
                        var parts = new List<string>();
                        foreach (var item in ie)
                        {
                            var part = FlattenResultToText(item);
                            if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
                        }
                        if (parts.Count > 0) return string.Join("\n---\n", parts);
                    }

                    // Estrarre da ChatMessageContent: cerca proprietà 'Content', poi 'Items', poi 'Text'
                    var typ = result.GetType();
                    var contentProp = typ.GetProperty("Content");
                    if (contentProp != null)
                    {
                        var contentVal = contentProp.GetValue(result);
                        if (contentVal != null)
                        {
                            // Se è una collezione, cerca Items[]
                            var itemsProp = contentVal.GetType().GetProperty("Items");
                            if (itemsProp != null)
                            {
                                var itemsVal = itemsProp.GetValue(contentVal) as System.Collections.IEnumerable;
                                if (itemsVal != null)
                                {
                                    foreach (var item in itemsVal)
                                    {
                                        var textProp = item.GetType().GetProperty("Text");
                                        if (textProp != null)
                                        {
                                            var textVal = textProp.GetValue(item) as string;
                                            if (!string.IsNullOrWhiteSpace(textVal))
                                            {
                                                // Se il testo è JSON, estrai solo il valore
                                                if (textVal.TrimStart().StartsWith("{"))
                                                {
                                                    try
                                                    {
                                                        using var doc = System.Text.Json.JsonDocument.Parse(textVal);
                                                        if (doc.RootElement.TryGetProperty("overall_evaluation", out var eval))
                                                            return eval.GetString();
                                                        return textVal;
                                                    }
                                                    catch { return textVal; }
                                                }
                                                return textVal;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Try reflection: common property names that may contain text
                    var candidates = new[] { "Content", "ContentText", "Text", "Message", "Response", "RespPreview", "Preview", "Result", "Value" };
                    foreach (var name in candidates)
                    {
                        var prop = typ.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                        if (prop != null)
                        {
                            try
                            {
                                var val = prop.GetValue(result);
                                var asText = FlattenResultToText(val);
                                if (!string.IsNullOrWhiteSpace(asText)) return asText;
                            }
                            catch { }
                        }
                    }

                    // Try known collection properties (choices/messages)
                    var choicesProp = typ.GetProperty("Choices", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                    if (choicesProp != null)
                    {
                        try
                        {
                            var val = choicesProp.GetValue(result) as System.Collections.IEnumerable;
                            if (val != null)
                            {
                                var parts = new List<string>();
                                foreach (var item in val)
                                {
                                    var part = FlattenResultToText(item);
                                    if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
                                }
                                if (parts.Count > 0) return string.Join("\n---\n", parts);
                            }
                        }
                        catch { }
                    }

                    // Fallback: try ToString if not a type name
                    var toStr = result.ToString();
                    if (!string.IsNullOrWhiteSpace(toStr) && !toStr.Contains(result.GetType().Name)) return toStr;
                }
                catch { }

                return null;
            }
            catch
            {
                // Avoid returning raw ToString() values; if we can't produce a textual representation return null
                return null;
            }
        }

        public List<ModelRecord> Models { get; set; } = new();

        public void OnGet()
        {
            // Read models directly from the database service for listing
            // Sort by FunctionCallingScore descending (higher first). For equal scores, prefer faster models (lower TestDurationSeconds).
            Models = _database.ListModels()
                .Where(m => ShowDisabled || m.Enabled)
                .OrderByDescending(m => m.FunctionCallingScore)
                .ThenBy(m => m.TestDurationSeconds.HasValue ? m.TestDurationSeconds.Value : double.PositiveInfinity)
                .ThenBy(m => m.Name ?? string.Empty)
                .ToList();

            // Load available test groups for UI
            try { TestGroups = _database.GetTestGroups(); } catch { TestGroups = new List<string>(); }

            // Ensure the 'texteval' category is available in the UI (useful when importing evaluation tests)
            try
            {
                if (TestGroups == null) TestGroups = new List<string>();
                if (!TestGroups.Any(g => string.Equals(g, "texteval", StringComparison.OrdinalIgnoreCase)))
                {
                    // Insert at the beginning so it's visible in the compact group list
                    TestGroups.Insert(0, "texteval");
                }
            }
            catch { }

            // For each model, fetch a lightweight summary of the latest normalized test run (if any)
            foreach (var m in Models)
            {
                try
                {
                    var s = _database.GetLatestTestRunSummary(m.Name);
                    if (s.HasValue)
                    {
                        var v = s.Value;
                        var passedStr = v.passed ? "passed" : "failed";
                        var dur = v.durationMs.HasValue ? $"{(v.durationMs.Value / 1000.0):0.##}s" : "n/a";
                        m.LastTestResults = $"Last run: {v.testCode} ({passedStr}) on {v.runDate ?? "?"}, duration {dur}";
                    }

                    // Populate per-group last scores and last results for up to the first 4 test groups.
                    m.LastGroupScores = new System.Collections.Generic.Dictionary<string, int?>(System.StringComparer.OrdinalIgnoreCase);
                    m.LastGroupResultsJson = new System.Collections.Generic.Dictionary<string, string?>(System.StringComparer.OrdinalIgnoreCase);
                    var groups = TestGroups ?? new System.Collections.Generic.List<string>();
                    foreach (var g in groups.Take(4))
                    {
                        try { m.LastGroupScores[g] = _database.GetLatestGroupScore(m.Name, g); } catch { m.LastGroupScores[g] = null; }
                        try { m.LastGroupResultsJson[g] = _database.GetLatestRunStepsJson(m.Name, g); } catch { m.LastGroupResultsJson[g] = null; }
                    }
                }
                catch { }
            }

        }

        // POST handler to disable a model
        public IActionResult OnPostDisableModel()
        {
            try
            {
                if (!Request.HasFormContentType) return BadRequest("form required");
                var model = Request.Form["model"].ToString();
                var showDisabled = Request.Form["showDisabled"].ToString();
                var show = false;
                if (!string.IsNullOrWhiteSpace(showDisabled)) bool.TryParse(showDisabled, out show);
                if (string.IsNullOrWhiteSpace(model)) return BadRequest("model required");
                var existing = _database.GetModelInfo(model);
                if (existing == null) return BadRequest("model not found");
                existing.Enabled = false;
                _database.UpsertModel(existing);
                TempData["TestResultMessage"] = $"Model {model} disabled.";
                return RedirectToPage("/Models", new { showDisabled = show });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // POST handler to enable a model
        public IActionResult OnPostEnableModel()
        {
            try
            {
                if (!Request.HasFormContentType) return BadRequest("form required");
                var model = Request.Form["model"].ToString();
                var showDisabled = Request.Form["showDisabled"].ToString();
                var show = false;
                if (!string.IsNullOrWhiteSpace(showDisabled)) bool.TryParse(showDisabled, out show);
                if (string.IsNullOrWhiteSpace(model)) return BadRequest("model required");
                var existing = _database.GetModelInfo(model);
                if (existing == null) return BadRequest("model not found");
                existing.Enabled = true;
                _database.UpsertModel(existing);
                TempData["TestResultMessage"] = $"Model {model} enabled.";
                return RedirectToPage("/Models", new { showDisabled = show });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        public class TestResultItem { public string name = ""; public bool ok; public string? message; public double durationSeconds; }
        public class TestResponse { public int functionCallingScore; public List<TestResultItem> results = new(); public double durationSeconds; }

        // POST handler to run function-calling tests for a model

        // public async Task<IActionResult> OnPostTestModelAsync()
        // {
        //     try
        //     {
        //         string model = string.Empty;
        //         string runId = string.Empty;
        //         var results = new List<TestResultItem>();

        //         // Support form posts (from the Models page buttons) or JSON body posts
        //         if (Request.HasFormContentType)
        //         {
        //             model = Request.Form["model"].ToString() ?? string.Empty;
        //             runId = Request.Form["runId"].ToString() ?? string.Empty;
        //         }
        //         else
        //         {
        //             // Try read JSON body
        //             Request.EnableBuffering();
        //             using var sr = new StreamReader(Request.Body, leaveOpen: true);
        //             var body = await sr.ReadToEndAsync();
        //             Request.Body.Position = 0;
        //             if (!string.IsNullOrWhiteSpace(body))
        //             {
        //                 try
        //                 {
        //                     using var doc = JsonDocument.Parse(body);
        //                     var root = doc.RootElement;
        //                     if (root.TryGetProperty("model", out var me)) model = me.GetString() ?? string.Empty;
        //                     if (root.TryGetProperty("runId", out var ri)) runId = ri.GetString() ?? string.Empty;
        //                 }
        //                 catch { }
        //             }
        //         }

        //         if (string.IsNullOrWhiteSpace(runId)) runId = Guid.NewGuid().ToString("N");
        //         if (string.IsNullOrWhiteSpace(model)) return BadRequest("model required");

        //         // Diagnostic: log arrival of test request
        //         try { Console.WriteLine($"[ModelsModel] Received TestModel request: model={model}, runId={runId}, remote={HttpContext.Connection.RemoteIpAddress}"); } catch { }


        //         string? lastMusicFile = null;
        //         string? lastSoundFile = null;
        //         string? lastTtsFile = null;
        //         double externalSeconds = 0.0; // time spent in external services (downloads, direct synthesize)

        //         // Recupera info modello dalla tabella
        //         var modelInfo = _database.GetModelInfo(model);
        //         if (modelInfo == null)
        //             return BadRequest($"Modello '{model}' non trovato nella tabella modelli.");

        //         // Cast esplicito per accedere alle proprietà dei plugin
        //         var factory = _kernelFactory as TinyGenerator.Services.KernelFactory;
        //         if (factory == null)
        //             return BadRequest("KernelFactory non disponibile per test LastCalled.");

        //         // Start progress tracking
        //         try { _progress?.Start(runId); AppendProgress(runId, $"Starting tests for model {model}"); } catch { }

        //         // Reset LastCalled per tutti i plugin
        //         factory.MathPlugin.LastCalled = null;
        //         factory.TextPlugin.LastCalled = null;
        //         factory.TimePlugin.LastCalled = null;
        //         factory.FileSystemPlugin.LastCalled = null;
        //         factory.HttpPlugin.LastCalled = null;
        //         factory.AudioCraftSkill.LastCalled = null;

        //         // Measure kernel creation time to help diagnose slow startup overhead
        //         double kernelCreateSeconds = 0.0;
        //         Microsoft.SemanticKernel.Kernel? createdKernel = null;
        //         try
        //         {
        //             // Build allowed plugin list for the builtin TestModel sequence so we only register
        //             // the addins actually exercised by these tests (reduces function schemas sent to provider)
        //             var allowed = new List<string>
        //             {
        //                 "math",
        //                 "text",
        //                 "time",
        //                 "filesystem",
        //                 "http",
        //                 "memory",
        //                 "audiocraft",
        //                 "tts"
        //             };
        //         }
        //         catch (Exception ex)
        //         {
        //             // Kernel creation/warmup failed — record a Kernel.Create test failure and continue.
        //             try
        //             {
        //                 results.Add(new TestResultItem { name = "Kernel.Create", ok = false, message = ex?.Message ?? ex?.GetType()?.Name ?? string.Empty, durationSeconds = 0.0 });
        //             }
        //             catch { }

        //             // If the provider indicates tools are not supported, mark the model defensively and persist
        //             try
        //             {
        //                 if (ex?.GetType()?.Name == "ModelDoesNotSupportToolsException" || (ex?.Message?.IndexOf("does not support tools", StringComparison.OrdinalIgnoreCase) >= 0))
        //                 {
        //                     try
        //                     {
        //                         if (modelInfo != null)
        //                         {
        //                             modelInfo.NoTools = true;
        //                             _database.UpsertModel(modelInfo);
        //                             AppendProgress(runId, "Marked model as NoTools (provider reports no tool support)");
        //                         }
        //                     }
        //                     catch { }
        //                 }
        //             }
        //             catch { }

        //             // Ensure createdKernel remains null so the later null-check returns a friendly BadRequest.
        //             createdKernel = null;
        //         }
        //     }
        //     catch (Exception ex)
        //     {
        //         // Record a failed kernel creation as a test result item so the UI/database shows the issue
        //         results.Add(new TestResultItem { name = "Kernel.Create", ok = false, message = ex.Message, durationSeconds = 0.0 });
        //         try
        //         {
        //             // Detect specific exception by type name to avoid adding a hard dependency on Ollama types
        //             if (ex?.GetType()?.Name == "ModelDoesNotSupportToolsException" || (ex?.Message?.IndexOf("does not support tools", StringComparison.OrdinalIgnoreCase) >= 0))
        //             {
        //                 // Mark model as not supporting tools and persist
        //                 try
        //                 {
        //                     modelInfo.NoTools = true;
        //                     _database.UpsertModel(modelInfo);
        //                     AppendProgress(runId, "Marked model as NoTools (provider reports no tool support)");
        //                 }
        //                 catch { }
        //             }
        //         }
        //         catch { }
        //     }

        //     if (createdKernel == null)
        //     {
        //         // Persist what we have so the UI shows partial results
        //         try
        //         {
        //             var json = System.Text.Json.JsonSerializer.Serialize(results);
        //             _database.UpdateModelTestResults(modelInfo.Name, 0, new Dictionary<string, bool?>(), 0.0);
        //         }
        //         catch { }
        //         return BadRequest("Impossibile creare un kernel reale per il modello selezionato (errore di configurazione o provider non supportato).");
        //     }

        //     // Add kernel creation timing as a test result so it appears in the per-test JSON summary
        //     results.Add(new TestResultItem { name = "Kernel.Create", ok = true, message = "Kernel created", durationSeconds = kernelCreateSeconds });


        //     // ✅ Crea l’agente e gli passa gli argomenti
        //     // Instruction: do NOT emit function-invocation JSON or textual function calls in the model response.
        //     // The agent should rely on the kernel's addins/skills invocation mechanism instead.
        //     var agentInstructions = "Use the kernel's skill/addin mechanism to invoke functions (do NOT emit JSON or textual function-call syntax in your response). Respond normally otherwise.";
        //     var agent = new ChatCompletionAgent("tester", createdKernel, agentInstructions, model ?? string.Empty);

        //     // Safe invoker with timeout to avoid hanging tests. Returns (completedSuccessfully, errorMessage, elapsedSeconds)
        //     async Task<(bool completed, string? error, double elapsedSeconds, string? raw)> InvokeAgentSafe(string p, int timeoutMs = 30000)
        //     {
        //         try
        //         {
        //             var sw = System.Diagnostics.Stopwatch.StartNew();
        //             var task = agent.InvokeAsync(p);
        //             var finished = await Task.WhenAny(task, Task.Delay(timeoutMs));
        //             if (finished != task)
        //             {
        //                 sw.Stop();
        //                 return (false, $"Timeout after {timeoutMs / 1000}s", sw.Elapsed.TotalSeconds, null);
        //             }
        //             // Await to observe exceptions
        //             var result = await task; // observe exceptions
        //             sw.Stop();
        //             string? raw = null;
        //             try
        //             {
        //                 // Best-effort: extract textual content from the result object
        //                 raw = FlattenResultToText(result);
        //                 if (raw == null && result != null)
        //                 {
        //                     try { raw = System.Text.Json.JsonSerializer.Serialize(result); } catch { raw = null; }
        //                 }
        //             }
        //             catch
        //             {
        //                 // If extraction/serialization fails, do not fall back to ToString(); return null
        //                 raw = null;
        //             }
        //             return (true, null, sw.Elapsed.TotalSeconds, raw);
        //         }
        //         catch (Exception ex)
        //         {
        //             // If the provider reports it does not support tools, mark the model and persist
        //             try
        //             {
        //                 if (ex?.GetType()?.Name == "ModelDoesNotSupportToolsException" || (ex?.Message?.IndexOf("does not support tools", StringComparison.OrdinalIgnoreCase) >= 0))
        //                 {
        //                     try { modelInfo.NoTools = true; _database.UpsertModel(modelInfo); AppendProgress(runId.ToString(), "Marked model as NoTools (provider reports no tool support)"); } catch { }
        //                 }
        //             }
        //             catch { }
        //             return (false, ex?.Message ?? ex?.GetType()?.Name ?? string.Empty, 0.0, null);
        //         }
        //     }

        //     // Warm up the model with an initial prompt so the provider is activated before we start timing
        //     try
        //     {
        //         AppendProgress(runId, "Warming up model with 'Pronto'...");
        //         var _warm = await InvokeAgentSafe("Pronto", 30000);
        //         if (!_warm.completed)
        //         {
        //             // If warmup times out or errors, record the warmup failure but DO NOT abort the test run.
        //             // Some providers may return structured values that occasionally trigger conversion errors during warmup;
        //             // record the failure and continue so the rest of the tests can still run and be persisted.
        //             results.Add(new TestResultItem { name = "Warmup", ok = false, message = _warm.error, durationSeconds = _warm.elapsedSeconds });
        //             AppendProgress(runId, $"Warmup failed (continuing): {_warm.error}");
        //             try
        //             {
        //                 var json = System.Text.Json.JsonSerializer.Serialize(results);
        //                 _database.UpdateModelTestResults(modelInfo.Name, 0, new Dictionary<string, bool?>(), 0.0);
        //             }
        //             catch { }
        //             // continue with tests despite warmup failure
        //         }
        //         else
        //         {
        //             results.Add(new TestResultItem { name = "Warmup", ok = true, message = "Warmup completed", durationSeconds = _warm.elapsedSeconds });
        //             AppendProgress(runId, $"Warmup completed in {_warm.elapsedSeconds:0.###}s");
        //         }
        //     }
        //     catch (Exception ex)
        //     {
        //         // Record warmup exception but continue with the test sequence. We want test runs to be resilient
        //         // to provider conversion quirks so we can collect per-test timings and results.
        //         results.Add(new TestResultItem { name = "Warmup", ok = false, message = ex.Message, durationSeconds = 0.0 });
        //         AppendProgress(runId, $"Warmup threw exception (continuing): {ex.Message}");
        //         try
        //         {
        //             var json = System.Text.Json.JsonSerializer.Serialize(results);
        //             _database.UpdateModelTestResults(modelInfo.Name, 0, new Dictionary<string, bool?>(), 0.0);
        //         }
        //         catch { }
        //         // do not return; continue with tests
        //     }

        //     // Start timing after warmup completes
        //     var testStart = DateTime.UtcNow;


        //     //var agent = new ChatCompletionAgent("tester", kernel, "Function calling tester", model ?? string.Empty);

        //     // Test MathPlugin: Add
        //     try
        //     {
        //         var prompt = "Calcola 2+2 usando il la funzione add del plugin math";
        //         AppendProgress(runId, "Running MathPlugin.Add test...");
        //         var _resMath = await InvokeAgentSafe(prompt);
        //         if (!_resMath.completed)
        //         {
        //             results.Add(new TestResultItem { name = "MathPlugin.Add", ok = false, message = _resMath.error, durationSeconds = _resMath.elapsedSeconds });
        //         }
        //         else
        //         {
        //             var called = factory.MathPlugin.LastCalled;
        //             var ok = called == "Add";
        //             AppendProgress(runId, $"MathPlugin.Add => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
        //             results.Add(new TestResultItem { name = "MathPlugin.Add", ok = ok, message = ok ? "Chiamata Add eseguita" : ($"LastCalled: {called}"), durationSeconds = _resMath.elapsedSeconds });
        //         }
        //     }
        //     catch (Exception ex) { results.Add(new TestResultItem { name = "MathPlugin.Add", ok = false, message = ex.Message }); }

        //     // Test TextPlugin: ToUpper
        //     try
        //     {
        //         var prompt = "Converti in maiuscolo la parola test usando la funzione toupper del plugin text";
        //         AppendProgress(runId, "Running TextPlugin.ToUpper test...");
        //         var _resText = await InvokeAgentSafe(prompt);
        //         if (!_resText.completed)
        //         {
        //             results.Add(new TestResultItem { name = "TextPlugin.ToUpper", ok = false, message = _resText.error, durationSeconds = _resText.elapsedSeconds });
        //         }
        //         else
        //         {
        //             var called = factory.TextPlugin.LastCalled;
        //             var ok = called == "ToUpper";
        //             AppendProgress(runId, $"TextPlugin.ToUpper => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
        //             results.Add(new TestResultItem { name = "TextPlugin.ToUpper", ok = ok, message = ok ? "Chiamata ToUpper eseguita" : ($"LastCalled: {called}"), durationSeconds = _resText.elapsedSeconds });
        //         }
        //     }
        //     catch (Exception ex) { results.Add(new TestResultItem { name = "TextPlugin.ToUpper", ok = false, message = ex.Message }); }

        //     // Test TimePlugin: Now
        //     try
        //     {
        //         var prompt = "Che ora è? Usa la funzione now";
        //         AppendProgress(runId, "Running TimePlugin.Now test...");
        //         var _resTime = await InvokeAgentSafe(prompt);
        //         if (!_resTime.completed)
        //         {
        //             results.Add(new TestResultItem { name = "TimePlugin.Now", ok = false, message = _resTime.error, durationSeconds = _resTime.elapsedSeconds });
        //         }
        //         else
        //         {
        //             var called = factory.TimePlugin.LastCalled;
        //             var ok = called == "Now";
        //             AppendProgress(runId, $"TimePlugin.Now => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
        //             results.Add(new TestResultItem { name = "TimePlugin.Now", ok = ok, message = ok ? "Chiamata Now eseguita" : ($"LastCalled: {called}"), durationSeconds = _resTime.elapsedSeconds });
        //         }
        //     }
        //     catch (Exception ex) { results.Add(new TestResultItem { name = "TimePlugin.Now", ok = false, message = ex.Message }); }

        //     // Test FileSystemPlugin: FileExists
        //     try
        //     {
        //         var prompt = "Controlla se esiste il file /tmp/test.txt usando la funzione file_exists del plugin filesystem";
        //         AppendProgress(runId, "Running FileSystemPlugin.FileExists test...");
        //         var _resFs = await InvokeAgentSafe(prompt);
        //         if (!_resFs.completed)
        //         {
        //             results.Add(new TestResultItem { name = "FileSystemPlugin.FileExists", ok = false, message = _resFs.error, durationSeconds = _resFs.elapsedSeconds });
        //         }
        //         else
        //         {
        //             var called = factory.FileSystemPlugin.LastCalled;
        //             var ok = called == "FileExists";
        //             AppendProgress(runId, $"FileSystemPlugin.FileExists => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
        //             results.Add(new TestResultItem { name = "FileSystemPlugin.FileExists", ok = ok, message = ok ? "Chiamata FileExists eseguita" : ($"LastCalled: {called}"), durationSeconds = _resFs.elapsedSeconds });
        //         }
        //     }
        //     catch (Exception ex) { results.Add(new TestResultItem { name = "FileSystemPlugin.FileExists", ok = false, message = ex.Message }); }

        //     // Test HttpPlugin: HttpGetAsync
        //     try
        //     {
        //         var prompt = "Fai una richiesta GET a https://example.com usando la funzione http_get del plugin http";
        //         AppendProgress(runId, "Running HttpPlugin.HttpGetAsync test...");
        //         var _resHttp = await InvokeAgentSafe(prompt);
        //         if (!_resHttp.completed)
        //         {
        //             results.Add(new TestResultItem { name = "HttpPlugin.HttpGetAsync", ok = false, message = _resHttp.error, durationSeconds = _resHttp.elapsedSeconds });
        //         }
        //         else
        //         {
        //             var called = factory.HttpPlugin.LastCalled;
        //             var ok = called == "HttpGetAsync";
        //             AppendProgress(runId, $"HttpPlugin.HttpGetAsync => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
        //             results.Add(new TestResultItem { name = "HttpPlugin.HttpGetAsync", ok = ok, message = ok ? "Chiamata HttpGetAsync eseguita" : ($"LastCalled: {called}"), durationSeconds = _resHttp.elapsedSeconds });
        //         }
        //     }
        //     catch (Exception ex) { results.Add(new TestResultItem { name = "HttpPlugin.HttpGetAsync", ok = false, message = ex.Message }); }

        //     // Test MemorySkill: Remember

        //     // Test MemorySkill: Remember, Recall, Forget sequenziale
        //     try
        //     {
        //         // Reset
        //         factory.MemorySkill.LastCalled = null;
        //         factory.MemorySkill.LastCollection = null;
        //         factory.MemorySkill.LastText = null;

        //         var collection = $"test_mem_{Guid.NewGuid():N}";
        //         var text = "valore di test memoria";

        //         // Remember
        //         var promptRemember = $"Ricorda la frase '{text}' nella collezione '{collection}' usando la funzione remember del plugin memory";
        //         AppendProgress(runId, "MemorySkill.Remember test...");
        //         var _resMemRemember = await InvokeAgentSafe(promptRemember);
        //         if (!_resMemRemember.completed)
        //         {
        //             results.Add(new TestResultItem { name = "MemorySkill.Remember", ok = false, message = _resMemRemember.error, durationSeconds = _resMemRemember.elapsedSeconds });
        //         }
        //         else
        //         {
        //             var okRemember = factory.MemorySkill.LastCalled == "RememberAsync" && factory.MemorySkill.LastCollection == collection && factory.MemorySkill.LastText == text;
        //             results.Add(new TestResultItem { name = "MemorySkill.Remember", ok = okRemember, message = okRemember ? "Chiamata RememberAsync eseguita" : ($"LastCalled: {factory.MemorySkill.LastCalled}, LastCollection: {factory.MemorySkill.LastCollection}, LastText: {factory.MemorySkill.LastText}"), durationSeconds = _resMemRemember.elapsedSeconds });
        //         }

        //         // Recall
        //         factory.MemorySkill.LastCalled = null;
        //         factory.MemorySkill.LastCollection = null;
        //         factory.MemorySkill.LastText = null;
        //         var promptRecall = $"Recupera la frase '{text}' dalla collezione '{collection}' usando la funzione recall del plugin memory";
        //         AppendProgress(runId, "MemorySkill.Recall test...");
        //         var _resMemRecall = await InvokeAgentSafe(promptRecall);
        //         if (!_resMemRecall.completed)
        //         {
        //             results.Add(new TestResultItem { name = "MemorySkill.Recall", ok = false, message = _resMemRecall.error, durationSeconds = _resMemRecall.elapsedSeconds });
        //         }
        //         else
        //         {
        //             var okRecall = factory.MemorySkill.LastCalled == "RecallAsync" && factory.MemorySkill.LastCollection == collection && factory.MemorySkill.LastText == text;
        //             results.Add(new TestResultItem { name = "MemorySkill.Recall", ok = okRecall, message = okRecall ? "Chiamata RecallAsync eseguita" : ($"LastCalled: {factory.MemorySkill.LastCalled}, LastCollection: {factory.MemorySkill.LastCollection}, LastText: {factory.MemorySkill.LastText}"), durationSeconds = _resMemRecall.elapsedSeconds });
        //         }

        //         // Forget
        //         factory.MemorySkill.LastCalled = null;
        //         factory.MemorySkill.LastCollection = null;
        //         factory.MemorySkill.LastText = null;
        //         var promptForget = $"Dimentica la frase '{text}' dalla collezione '{collection}' usando la funzione forget del plugin memory";
        //         AppendProgress(runId, "MemorySkill.Forget test...");
        //         var _resMemForget = await InvokeAgentSafe(promptForget);
        //         if (!_resMemForget.completed)
        //         {
        //             results.Add(new TestResultItem { name = "MemorySkill.Forget", ok = false, message = _resMemForget.error, durationSeconds = _resMemForget.elapsedSeconds });
        //         }
        //         else
        //         {
        //             var okForget = factory.MemorySkill.LastCalled == "ForgetAsync" && factory.MemorySkill.LastCollection == collection && factory.MemorySkill.LastText == text;
        //             results.Add(new TestResultItem { name = "MemorySkill.Forget", ok = okForget, message = okForget ? "Chiamata ForgetAsync eseguita" : ($"LastCalled: {factory.MemorySkill.LastCalled}, LastCollection: {factory.MemorySkill.LastCollection}, LastText: {factory.MemorySkill.LastText}"), durationSeconds = _resMemForget.elapsedSeconds });
        //         }
        //     }
        //     catch (Exception ex) { results.Add(new TestResultItem { name = "MemorySkill.MemorySequence", ok = false, message = ex.Message }); }

        //     // AudioCraft tests: CheckHealth, ListModels, GenerateMusic, GenerateSound, DownloadFile
        //     try
        //     {
        //         // Helper to run a single audio test safely and append results regardless of exceptions
        //         async Task RunAudioTestAsync(string testName, string prompt, Func<bool> successPredicate, string successMessage, int timeoutMs = 30000)
        //         {
        //             try
        //             {
        //                 AppendProgress(runId, $"Running AudioCraft.{testName} test...");
        //                 var _res = await InvokeAgentSafe(prompt, timeoutMs);
        //                 if (!_res.completed)
        //                 {
        //                     results.Add(new TestResultItem { name = $"AudioCraft.{testName}", ok = false, message = _res.error, durationSeconds = _res.elapsedSeconds });
        //                     return;
        //                 }

        //                 var called = factory.AudioCraftSkill.LastCalled;
        //                 var ok = false;
        //                 try { ok = successPredicate(); } catch { ok = false; }
        //                 AppendProgress(runId, $"AudioCraft.{testName} => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
        //                 results.Add(new TestResultItem { name = $"AudioCraft.{testName}", ok = ok, message = ok ? successMessage : ($"LastCalled: {called}"), durationSeconds = _res.elapsedSeconds });

        //                 // If generation succeeded, attempt to download and save the generated file into wwwroot
        //                 try
        //                 {
        //                     if (ok && string.Equals(testName, "GenerateMusic", StringComparison.OrdinalIgnoreCase))
        //                     {
        //                         var remote = factory.AudioCraftSkill.LastGeneratedMusicFile;
        //                         if (!string.IsNullOrWhiteSpace(remote))
        //                         {
        //                             try
        //                             {
        //                                 var swExt = System.Diagnostics.Stopwatch.StartNew();
        //                                 var bytes = await factory.AudioCraftSkill.DownloadFileAsync(remote);
        //                                 swExt.Stop();
        //                                 externalSeconds += swExt.Elapsed.TotalSeconds;
        //                                 if (bytes != null && bytes.Length > 0)
        //                                 {
        //                                     var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "music_test");
        //                                     Directory.CreateDirectory(dir);
        //                                     var ext = Path.GetExtension(remote);
        //                                     if (string.IsNullOrWhiteSpace(ext)) ext = ".wav";
        //                                     var fname = $"{modelInfo.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext}";
        //                                     var full = Path.Combine(dir, fname);
        //                                     await System.IO.File.WriteAllBytesAsync(full, bytes);
        //                                     lastMusicFile = Path.Combine("music_test", fname).Replace('\\', '/');
        //                                     AppendProgress(runId, $"Saved generated music to {lastMusicFile}");
        //                                 }
        //                             }
        //                             catch (Exception ex)
        //                             {
        //                                 AppendProgress(runId, $"Failed to save generated music: {ex.Message}");
        //                             }
        //                         }
        //                     }
        //                     else if (ok && string.Equals(testName, "GenerateSound", StringComparison.OrdinalIgnoreCase))
        //                     {
        //                         var remote = factory.AudioCraftSkill.LastGeneratedSoundFile;
        //                         if (!string.IsNullOrWhiteSpace(remote))
        //                         {
        //                             try
        //                             {
        //                                 var swExt = System.Diagnostics.Stopwatch.StartNew();
        //                                 var bytes = await factory.AudioCraftSkill.DownloadFileAsync(remote);
        //                                 swExt.Stop();
        //                                 externalSeconds += swExt.Elapsed.TotalSeconds;
        //                                 if (bytes != null && bytes.Length > 0)
        //                                 {
        //                                     var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "audio_test");
        //                                     Directory.CreateDirectory(dir);
        //                                     var ext = Path.GetExtension(remote);
        //                                     if (string.IsNullOrWhiteSpace(ext)) ext = ".wav";
        //                                     var fname = $"{modelInfo.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}{ext}";
        //                                     var full = Path.Combine(dir, fname);
        //                                     await System.IO.File.WriteAllBytesAsync(full, bytes);
        //                                     lastSoundFile = Path.Combine("audio_test", fname).Replace('\\', '/');
        //                                     AppendProgress(runId, $"Saved generated sound to {lastSoundFile}");
        //                                 }
        //                             }
        //                             catch (Exception ex)
        //                             {
        //                                 AppendProgress(runId, $"Failed to save generated sound: {ex.Message}");
        //                             }
        //                         }
        //                     }
        //                 }
        //                 catch { }
        //             }
        //             catch (Exception ex)
        //             {
        //                 // Record the exception message and continue with next test
        //                 results.Add(new TestResultItem { name = $"AudioCraft.{testName}", ok = false, message = ex.Message });
        //             }
        //         }

        //         // Run individual audio tests with safe wrapper
        //         await RunAudioTestAsync("CheckHealth", "Verifica se AudioCraft è online usando la funzione check_health del plugin audiocraft", () => string.Equals(factory.AudioCraftSkill.LastCalled, nameof(TinyGenerator.Skills.AudioCraftSkill.CheckHealthAsync), StringComparison.OrdinalIgnoreCase), "Chiamata CheckHealth eseguita");
        //         await RunAudioTestAsync("ListModels", "Elenca i modelli AudioCraft usando la funzione list_models del plugin audiocraft", () => string.Equals(factory.AudioCraftSkill.LastCalled, nameof(TinyGenerator.Skills.AudioCraftSkill.ListModelsAsync), StringComparison.OrdinalIgnoreCase), "Chiamata ListModels eseguita");
        //         await RunAudioTestAsync("GenerateMusic", "Genera un breve frammento musicale usando la funzione generate_music del plugin audiocraft con durata 1", () => string.Equals(factory.AudioCraftSkill.LastCalled, nameof(TinyGenerator.Skills.AudioCraftSkill.GenerateMusicAsync), StringComparison.OrdinalIgnoreCase), "Chiamata GenerateMusic eseguita", timeoutMs: 180000);
        //         await RunAudioTestAsync("GenerateSound", "Genera un breve effetto sonoro usando la funzione generate_sound del plugin audiocraft con durata 1", () => string.Equals(factory.AudioCraftSkill.LastCalled, nameof(TinyGenerator.Skills.AudioCraftSkill.GenerateSoundAsync), StringComparison.OrdinalIgnoreCase), "Chiamata GenerateSound eseguita", timeoutMs: 180000);
        //         await RunAudioTestAsync("DownloadFile", "Scarica un file di esempio usando la funzione download_file del plugin audiocraft (nome file di test)", () => string.Equals(factory.AudioCraftSkill.LastCalled, nameof(TinyGenerator.Skills.AudioCraftSkill.DownloadFileAsync), StringComparison.OrdinalIgnoreCase), "Chiamata DownloadFile eseguita");
        //     }
        //     catch (Exception ex) { results.Add(new TestResultItem { name = "AudioCraft.Sequence", ok = false, message = ex.Message }); }

        //     // TTS tests: CheckHealth, ListVoices, Synthesize
        //     try
        //     {
        //         async Task RunTtsTestAsync(string testName, string prompt, Func<bool> successPredicate, string successMessage)
        //         {
        //             try
        //             {
        //                 AppendProgress(runId, $"Running TTS.{testName} test...");
        //                 var _res = await InvokeAgentSafe(prompt);
        //                 if (!_res.completed)
        //                 {
        //                     results.Add(new TestResultItem { name = $"Tts.{testName}", ok = false, message = _res.error, durationSeconds = _res.elapsedSeconds });
        //                     return;
        //                 }

        //                 var called = factory.TtsApiSkill.LastCalled;
        //                 var ok = false;
        //                 try { ok = successPredicate(); } catch { ok = false; }
        //                 AppendProgress(runId, $"Tts.{testName} => {(ok ? "OK" : "FAIL: " + (called ?? "null"))}");
        //                 results.Add(new TestResultItem { name = $"Tts.{testName}", ok = ok, message = ok ? successMessage : ($"LastCalled: {called}"), durationSeconds = _res.elapsedSeconds });

        //                 // If synthesize succeeded, fetch bytes directly from the TTS client and save to wwwroot/tts_test
        //                 if (ok && string.Equals(testName, "Synthesize", StringComparison.OrdinalIgnoreCase))
        //                 {
        //                     try
        //                     {
        //                         var swExt = System.Diagnostics.Stopwatch.StartNew();
        //                         var bytes = await factory.TtsApiSkill.SynthesizeAsync("Prova TTS da TinyGenerator", "voice_templates", "template_alien", null, -1, null, "it", "neutral", 1.0, "wav");
        //                         swExt.Stop();
        //                         externalSeconds += swExt.Elapsed.TotalSeconds;
        //                         if (bytes != null && bytes.Length > 0)
        //                         {
        //                             var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "tts_test");
        //                             Directory.CreateDirectory(dir);
        //                             var fname = $"{modelInfo.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.wav";
        //                             var full = Path.Combine(dir, fname);
        //                             await System.IO.File.WriteAllBytesAsync(full, bytes);
        //                             lastTtsFile = Path.Combine("tts_test", fname).Replace('\\', '/');
        //                             AppendProgress(runId, $"Saved synthesized TTS to {lastTtsFile}");
        //                         }
        //                     }
        //                     catch (Exception ex)
        //                     {
        //                         AppendProgress(runId, $"Failed to synthesize/save TTS: {ex.Message}");
        //                     }
        //                 }
        //             }
        //             catch (Exception ex)
        //             {
        //                 results.Add(new TestResultItem { name = $"Tts.{testName}", ok = false, message = ex.Message });
        //             }
        //         }

        //         await RunTtsTestAsync("CheckHealth", "Verifica se il servizio TTS è online usando la funzione check_health del plugin tts", () => string.Equals(factory.TtsApiSkill.LastCalled, nameof(TinyGenerator.Skills.TtsApiSkill.CheckHealthAsync), StringComparison.OrdinalIgnoreCase), "Chiamata CheckHealth eseguita");
        //         await RunTtsTestAsync("ListVoices", "Elenca le voci TTS usando la funzione list_voices del plugin tts", () => string.Equals(factory.TtsApiSkill.LastCalled, nameof(TinyGenerator.Skills.TtsApiSkill.ListVoicesAsync), StringComparison.OrdinalIgnoreCase), "Chiamata ListVoices eseguita");
        //         await RunTtsTestAsync("Synthesize", "Sintetizza un breve testo usando la funzione synthesize del plugin tts", () => string.Equals(factory.TtsApiSkill.LastCalled, nameof(TinyGenerator.Skills.TtsApiSkill.SynthesizeAsync), StringComparison.OrdinalIgnoreCase), "Chiamata Synthesize eseguita");
        //     }
        //     catch (Exception ex) { results.Add(new TestResultItem { name = "Tts.Sequence", ok = false, message = ex.Message }); }

        //     // compute score: number of ok tests / total * 10 (guard divide by zero)
        //     var okCount = results.Count(r => r.ok);
        //     var score = results.Count > 0 ? (int)Math.Round((double)okCount / results.Count * 10) : 0;
        //     var skillFlagMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        //     {
        //         ["MathPlugin.Add"] = "SkillAdd",
        //         ["TextPlugin.ToUpper"] = "SkillToUpper",
        //         ["TimePlugin.Now"] = "SkillNow",
        //         ["FileSystemPlugin.FileExists"] = "SkillFileExists",
        //         ["HttpPlugin.HttpGetAsync"] = "SkillHttpGet",
        //         ["MemorySkill.Remember"] = "SkillRemember",
        //         ["MemorySkill.Recall"] = "SkillRecall",
        //         ["MemorySkill.Forget"] = "SkillForget"
        //     };
        //     // Add AudioCraft mappings
        //     skillFlagMap["AudioCraft.CheckHealth"] = "SkillAudioCheckHealth";
        //     skillFlagMap["AudioCraft.ListModels"] = "SkillAudioListModels";
        //     skillFlagMap["AudioCraft.GenerateMusic"] = "SkillAudioGenerateMusic";
        //     skillFlagMap["AudioCraft.GenerateSound"] = "SkillAudioGenerateSound";
        //     skillFlagMap["AudioCraft.DownloadFile"] = "SkillAudioDownloadFile";
        //     // TTS mappings
        //     skillFlagMap["Tts.CheckHealth"] = "SkillTtsCheckHealth";
        //     skillFlagMap["Tts.ListVoices"] = "SkillTtsListVoices";
        //     skillFlagMap["Tts.Synthesize"] = "SkillTtsSynthesize";
        //     var flagUpdates = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        //     foreach (var pair in skillFlagMap)
        //     {
        //         var entry = results.FirstOrDefault(r => string.Equals(r.name, pair.Key, StringComparison.OrdinalIgnoreCase));
        //         flagUpdates[pair.Value] = entry?.ok;
        //     }

        //     // Duration: compute total wall time and subtract external service time (downloads, synthesize)
        //     var testEnd = DateTime.UtcNow;
        //     var totalSeconds = (testEnd - testStart).TotalSeconds;
        //     var adjustedSeconds = totalSeconds - externalSeconds;
        //     if (adjustedSeconds < 0) adjustedSeconds = 0;

        //     // Persist adjusted duration (excluding external service time)
        //     _database.UpdateModelTestResults(modelInfo.Name, score, flagUpdates, adjustedSeconds);
        //     // Persist per-test results JSON so UI can show details after redirect
        //     try
        //     {
        //         var json = System.Text.Json.JsonSerializer.Serialize(results);
        //         _database.UpdateModelTestResults(modelInfo.Name, score, flagUpdates, adjustedSeconds);
        //     }
        //     catch { /* ignore persistence failures */ }

        //     // If the request was a form post (user clicked Test in the UI), set TempData so the result (including duration)
        //     // is shown after the redirect back to the Models page.
        //     try
        //     {
        //         // Show both total wall time and adjusted time (excluding external services) for transparency
        //         TempData["TestResultMessage"] = $"Model {modelInfo.Name}: Score {score}/10 — Duration: {adjustedSeconds:0.##}s (wall: {totalSeconds:0.##}s, external: {externalSeconds:0.###}s)";
        //     }
        //     catch { }

        //     try { AppendProgress(runId, $"All tests completed. Score: {score}/10"); _progress?.MarkCompleted(runId, score.ToString()); Console.WriteLine($"[ModelsModel] {runId}: Marked completed with score {score}"); } catch { }

        //     var resp = new TestResponse { functionCallingScore = score, results = results, durationSeconds = adjustedSeconds };
        //     var wrapper = new { runId = runId, result = resp };

        //     // If the request came from the Models page form, redirect back to the Models page
        //     if (Request.HasFormContentType)
        //     {
        //         // Redirect to the Models page so the user returns to the list
        //         return RedirectToPage("/Models");
        //     }

        //     return new JsonResult(wrapper);
        // }
        //     catch (Exception ex)
        //     {
        //         return BadRequest(new { error = ex?.Message ?? ex?.GetType()?.Name ?? string.Empty });
        //     }
        // }

        // Handler to run a named group of tests for a model (runs tests defined in test_definitions)
        public async Task<IActionResult> OnPostRunGroupAsync()
        {
            try
            {
                var (model, group) = await ReadModelGroupAsync();
                if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(group)) return BadRequest("model and group required");

                var modelInfo = _database.GetModelInfo(model);
                if (modelInfo == null) return BadRequest("model not found");

                var factory = _kernelFactory as TinyGenerator.Services.KernelFactory;
                if (factory == null) return BadRequest("KernelFactory not available");

                var tests = _database.GetPromptsByGroup(group) ?? new List<TinyGenerator.Models.TestDefinition>();
                var runId = _database.CreateTestRun(modelInfo.Name, group, $"Group run {group}", false, null, "Started from UI group runner");

                // Initialize progress tracking and return runId immediately; execute the run in background
                try { _progress?.Start(runId.ToString()); AppendProgress(runId.ToString(), $"Started group run {group} for model {modelInfo.Name}"); } catch { }
                try { _ = _notifications?.NotifyGroupAsync(runId.ToString(), "Started", $"Group {group} started on {modelInfo.Name}", "info"); } catch { }
                try { _ = _notifications?.NotifyAllAsync("Run started", $"Group {group} started on {modelInfo.Name}", "info"); } catch { }

                _ = Task.Run(async () =>
                {
                    var agentInstructions = "Use the kernel's skill/addin mechanism to invoke functions (do NOT emit JSON or textual function-call syntax in your response).";
                    try
                    {
                        var groupAllowed = CollectAllowedPlugins(tests);
                        var defaultAgent = CreateDefaultAgent(factory, model, groupAllowed, agentInstructions);

                        var testStartUtc = await WarmupModelAsync(defaultAgent, runId, modelInfo);

                        await ExecuteGroupTestsAsync(runId, tests, factory, model, modelInfo, agentInstructions, defaultAgent);

                        var _ = FinalizeGroupRun(runId, testStartUtc, modelInfo);
                    }
                    catch (Exception exBg)
                    {
                        try { AppendProgress(runId.ToString(), $"Run failed: {exBg.Message}"); _progress?.MarkCompleted(runId.ToString(), "0"); } catch { }
                    }
                });

                // Respond immediately so the client can attach SignalR and start polling
                return new JsonResult(new { runId = runId });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        private async Task<(string model, string group)> ReadModelGroupAsync()
        {
            string model = string.Empty;
            string group = string.Empty;

            if (Request.HasFormContentType)
            {
                model = Request.Form["model"].ToString() ?? string.Empty;
                group = Request.Form["group"].ToString() ?? string.Empty;
                return (model, group);
            }

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
                    if (root.TryGetProperty("group", out var ge)) group = ge.GetString() ?? string.Empty;
                }
                catch { }
            }

            return (model, group);
        }

        private string[] CollectAllowedPlugins(IEnumerable<TinyGenerator.Models.TestDefinition> tests)
        {
            return tests
                .SelectMany(t => DetermineAllowedPluginsForStep(t))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToArray();
        }

        private string[] DetermineAllowedPluginsForStep(TinyGenerator.Models.TestDefinition t)
        {
            if (!string.IsNullOrWhiteSpace(t.AllowedPlugins))
            {
                try
                {
                    return t.AllowedPlugins
                        .Split(',')
                        .Select(s => (s ?? string.Empty).Trim().ToLowerInvariant())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToArray();
                }
                catch
                {
                    // fall through and use library
                }
            }

            var lib = (t.Library ?? "").Trim();
            if (string.IsNullOrWhiteSpace(lib)) lib = "text";
            // Special-case: tests imported from the "texteval" group or known narrative evaluation
            // categories should use the evaluator skill (function-calling) rather than a plugin
            var evaluatorLibs = new[] { "coerenza_narrativa", "struttura", "caratterizzazione_personaggi", "dialoghi", "ritmo", "originalita", "stile", "worldbuilding", "coerenza_tematica", "impatto_emotivo" };
            if (string.Equals(t.GroupName, "texteval", StringComparison.OrdinalIgnoreCase) || evaluatorLibs.Contains(lib.ToLowerInvariant()))
            {
                return new[] { "evaluator" };
            }

            return new[] { lib.ToLowerInvariant() };
        }

        private ChatCompletionAgent? CreateDefaultAgent(TinyGenerator.Services.KernelFactory factory, string model, string[] allowedPlugins, string agentInstructions)
        {
            try
            {
                var kernel = factory.CreateKernel(model, allowedPlugins);
                if (kernel == null) return null;
                return new ChatCompletionAgent("tester", kernel, agentInstructions, model ?? string.Empty);
            }
            catch
            {
                return null;
            }
        }

    private async Task<DateTime> WarmupModelAsync(ChatCompletionAgent? defaultAgent, int runId, TinyGenerator.Models.ModelInfo modelInfo)
        {
            if (defaultAgent == null)
            {
                AppendProgress(runId.ToString(), "Skipping warmup: no kernel/agent available");
                return DateTime.UtcNow;
            }

            try
            {
                AppendProgress(runId.ToString(), "Warming up model with 'Pronto'...");
                var warm = await InvokeAgentSafeAsync(defaultAgent, "Pronto", 30000, ex => TryMarkModelNoTools(ex, modelInfo, runId));
                // Also detect provider-declared no-tool support encoded in text output (no exception thrown)
                TryMarkModelNoToolsFromText(warm.error, modelInfo, runId);
                TryMarkModelNoToolsFromText(warm.raw, modelInfo, runId);
                if (!warm.completed)
                {
                    AppendProgress(runId.ToString(), $"Warmup failed: {warm.error}");

                    try
                    {
                        // Check if the model is actually loaded (ollama ps)
                        var running = await OllamaMonitorService.GetRunningModelsAsync();
                        var modelLoaded = running != null && running.Any(r => string.Equals(r.Name, modelInfo.Name, StringComparison.OrdinalIgnoreCase));
                        if (!modelLoaded)
                        {
                            // If model not loaded, attempt to stop another running model to free resources
                            var other = running?.FirstOrDefault(r => !string.Equals(r.Name, modelInfo.Name, StringComparison.OrdinalIgnoreCase));
                            if (other != null && !string.IsNullOrWhiteSpace(other.Name))
                            {
                                AppendProgress(runId.ToString(), $"Model '{modelInfo.Name}' not running. Stopping another instance '{other.Name}' to free resources...");
                                var stopRes = await OllamaMonitorService.StopModelAsync(other.Name);
                                AppendProgress(runId.ToString(), $"Stop '{other.Name}': success={stopRes.Success} output={stopRes.Output?.Split('\n').FirstOrDefault()}");
                                // Wait briefly for system to free resources
                                await Task.Delay(1500);

                                // Retry warmup once
                                AppendProgress(runId.ToString(), "Retrying warmup after stopping another model...");
                                var retry = await InvokeAgentSafeAsync(defaultAgent, "Pronto", 30000, ex => TryMarkModelNoTools(ex, modelInfo, runId));
                                TryMarkModelNoToolsFromText(retry.error, modelInfo, runId);
                                TryMarkModelNoToolsFromText(retry.raw, modelInfo, runId);
                                if (retry.completed)
                                {
                                    AppendProgress(runId.ToString(), $"Warmup completed after retry in {retry.elapsedSeconds:0.###}s");
                                    return DateTime.UtcNow;
                                }
                                AppendProgress(runId.ToString(), $"Retry warmup failed: {retry.error}");
                            }
                            else
                            {
                                AppendProgress(runId.ToString(), "Model not running and no other running instances found to stop.");
                            }
                        }
                        else
                        {
                            AppendProgress(runId.ToString(), "Model is present in 'ollama ps' but warmup failed; not attempting to stop other instances.");
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendProgress(runId.ToString(), $"Warmup recovery attempt failed: {ex.Message}");
                    }

                    return DateTime.UtcNow;
                }

                AppendProgress(runId.ToString(), $"Warmup completed in {warm.elapsedSeconds:0.###}s");
                return DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                AppendProgress(runId.ToString(), $"Warmup threw exception: {ex.Message}");
                return DateTime.UtcNow;
            }
        }

        private async Task ExecuteGroupTestsAsync(
            int runId,
            IEnumerable<TinyGenerator.Models.TestDefinition> tests,
            TinyGenerator.Services.KernelFactory factory,
            string model,
            TinyGenerator.Models.ModelInfo modelInfo,
            string agentInstructions,
            ChatCompletionAgent? defaultAgent)
        {
            int idx = 0;
            foreach (var t in tests)
            {
                idx++;
                try
                {
                    await _testService.ExecuteTestAsync(runId, idx, t, factory, model, modelInfo, agentInstructions, defaultAgent);
                }
                catch (Exception ex)
                {
                    try { AppendProgress(runId.ToString(), $"Step {idx} ERROR executing test service: {ex.Message}"); } catch { }
                }
            }
        }

        // Best-effort detection for providers that surface "does not support tools" only as text
        private void TryMarkModelNoToolsFromText(string? text, TinyGenerator.Models.ModelInfo modelInfo, int runId)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(text) && text.IndexOf("does not support tools", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try
                    {
                        modelInfo.NoTools = true;
                        _database.UpsertModel(modelInfo);
                        AppendProgress(runId.ToString(), "Marked model as NoTools (provider output indicates no tool support)");
                    }
                    catch { }
                }
            }
            catch { }
        }

    private (int steps, int passed, int score, long? durationMs) FinalizeGroupRun(int runId, DateTime testStartUtc, TinyGenerator.Models.ModelInfo modelInfo)
        {
            var counts = _database.GetRunStepCounts(runId);
            var passedCount = counts.passed;
            var steps = counts.total;
            var score = steps > 0 ? (int)Math.Round((double)passedCount / steps * 10) : 0;

            var durationMs = (long?)(DateTime.UtcNow - testStartUtc).TotalMilliseconds;
            var passedFlag = steps > 0 && passedCount == steps;

            try { _database.UpdateTestRunResult(runId, passedFlag, durationMs); } catch { }
            try { _database.UpdateModelTestResults(modelInfo.Name, score, new Dictionary<string, bool?>(), durationMs.HasValue ? (double?)(durationMs.Value / 1000.0) : null); } catch { }

            try { AppendProgress(runId.ToString(), $"Group run completed. Passed {passedCount}/{steps} tests. Score {score}/10. Duration: {(durationMs.HasValue ? (durationMs.Value / 1000.0).ToString("0.###") + "s" : "n/a")}"); _progress?.MarkCompleted(runId.ToString(), score.ToString()); } catch { }
            try { _ = _notifications?.NotifyGroupAsync(runId.ToString(), "Completed", $"Group run completed. Score {score}/10", "success"); } catch { }

            return (steps, passedCount, score, durationMs);
        }

    private async Task<(bool completed, string? error, double elapsedSeconds, string? raw, object? resultObj)> InvokeAgentSafeAsync(ChatCompletionAgent? agent, string prompt, int timeoutMs, Action<Exception>? onException)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (agent == null) return (false, "No kernel/agent", 0.0, null, null);
                var task = agent.InvokeAsync(prompt);
                var finished = await Task.WhenAny(task, Task.Delay(timeoutMs));
                if (finished != task)
                {
                    sw.Stop();
                    return (false, $"Timeout after {timeoutMs / 1000}s", sw.Elapsed.TotalSeconds, null, null);
                }

                var result = await task;
                sw.Stop();

                string? raw = null;
                try
                {
                    // Prova ad estrarre direttamente la proprietà 'Text' se presente
                    if (result != null)
                    {
                        var type = result.GetType();
                        var textProp = type.GetProperty("Text");
                        if (textProp != null)
                        {
                            var val = textProp.GetValue(result);
                            if (val is string s && !string.IsNullOrWhiteSpace(s))
                                raw = s;
                        }
                    }
                    // Se non trovato, fallback su FlattenResultToText
                    if (raw == null)
                        raw = FlattenResultToText(result);
                    // Se ancora null, serializza tutto
                    if (raw == null && result != null)
                    {
                        try { raw = System.Text.Json.JsonSerializer.Serialize(result); } catch { raw = null; }
                    }
                }
                catch { raw = null; }

                return (true, null, sw.Elapsed.TotalSeconds, raw, result);
            }
            catch (Exception ex)
            {
                onException?.Invoke(ex);
            return (false, ex?.Message ?? ex?.GetType()?.Name ?? string.Empty, 0.0, null, null);
            }
        }

    private void TryMarkModelNoTools(Exception ex, TinyGenerator.Models.ModelInfo modelInfo, int runId)
        {
            try
            {
                if (ex?.GetType()?.Name == "ModelDoesNotSupportToolsException" || (ex?.Message?.IndexOf("does not support tools", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    try
                    {
                        modelInfo.NoTools = true;
                        _database.UpsertModel(modelInfo);
                        AppendProgress(runId.ToString(), "Marked model as NoTools (provider reports no tool support)");
                    }
                    catch { }
                }
            }
            catch { }
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

        // Handler to start a group run for all enabled models. The server chooses the first available test group
        // when the request does not specify one. Returns an array of created runIds so the client can subscribe.
        public async Task<IActionResult> OnPostRunAllAsync()
        {
            try
            {
                // Read optional group parameter from request body/form. If not provided, choose first available.
                var (_, providedGroup) = await ReadModelGroupAsync();
                var groups = _database.GetTestGroups() ?? new List<string>();
                string? group = null;
                if (!string.IsNullOrWhiteSpace(providedGroup)) group = providedGroup;
                else group = groups.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(group)) return BadRequest("No test groups configured");

                var models = _database.ListModels().Where(m => m.Enabled).ToList();
                var runIds = new List<int>();

                var factory = _kernelFactory as TinyGenerator.Services.KernelFactory;
                if (factory == null) return BadRequest("KernelFactory not available");

                // Create a mapping of (model,group) -> runId so we can create all run records immediately
                var runMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var modelInfo in models)
                {
                    try
                    {
                        try
                        {
                            var runId = _database.CreateTestRun(modelInfo.Name, group, $"Group run {group}", false, null, "Started from RunAll");
                            runIds.Add(runId);
                            runMap[$"{modelInfo.Name}||{group}"] = runId;
                            try { _progress?.Start(runId.ToString()); AppendProgress(runId.ToString(), $"Queued group run {group} for model {modelInfo.Name}"); } catch { }
                            try { _ = _notifications?.NotifyGroupAsync(runId.ToString(), "Queued", $"Group run {group} queued on {modelInfo.Name}", "info"); } catch { }
                            try { _ = _notifications?.NotifyAllAsync("Run queued", $"Queued run for model {modelInfo.Name}: group {group}", "info"); } catch { }
                        }
                        catch { /* continue with next model */ }
                    }
                    catch { /* ignore per-model failures */ }
                }

                // Run all models sequentially in a single background task to avoid concurrent Ollama startups
                _ = Task.Run(async () =>
                {
                    var agentInstructions = "Use the kernel's skill/addin mechanism to invoke functions (do NOT emit JSON or textual function-call syntax in your response).";
                    try
                    {
                        foreach (var modelInfo in models)
                        {
                            foreach (var grp in groups)
                            {
                                int runId = 0;
                                try
                                {
                                    var key = $"{modelInfo.Name}||{grp}";
                                    if (!runMap.TryGetValue(key, out runId)) continue;

                                    // Load tests for this group and run them sequentially
                                    var tests = _database.GetPromptsByGroup(grp) ?? new List<TinyGenerator.Models.TestDefinition>();
                                    var groupAllowed = CollectAllowedPlugins(tests);
                                    var defaultAgent = CreateDefaultAgent(factory, modelInfo.Name, groupAllowed, agentInstructions);

                                    AppendProgress(runId.ToString(), $"Starting group {grp} on model {modelInfo.Name}");
                                    var testStartUtc = await WarmupModelAsync(defaultAgent, runId, modelInfo);
                                    await ExecuteGroupTestsAsync(runId, tests, factory, modelInfo.Name, modelInfo, agentInstructions, defaultAgent);
                                    var _ = FinalizeGroupRun(runId, testStartUtc, modelInfo);
                                }
                                catch (Exception exBg)
                                {
                                    try { if (runId != 0) { AppendProgress(runId.ToString(), $"Run failed: {exBg.Message}"); _progress?.MarkCompleted(runId.ToString(), "0"); } } catch { }
                                }
                            }
                        }
                    }
                    catch (Exception exAll)
                    {
                        // If the whole sequencer fails, append a global progress message
                        try { AppendProgress(string.Empty, $"RunAll sequencer failed: {exAll.Message}"); } catch { }
                    }
                });

                // Also return a mapping of model -> runId so the client can update per-row progress
                var runMapArray = runMap.Select(kvp => {
                    var parts = kvp.Key.Split(new[] { "||" }, StringSplitOptions.None);
                    var modelName = parts.Length > 0 ? parts[0] : kvp.Key;
                    return new { model = modelName, runId = kvp.Value };
                }).ToArray();
                return new JsonResult(new { runIds = runIds, runMap = runMapArray });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        // Handler to purge disabled models from local Ollama installation.
        public async Task<IActionResult> OnPostPurgeDisabledOllamaAsync()
        {
            try
            {
                var disabled = _database.ListModels().Where(m => string.Equals(m.Provider, "ollama", StringComparison.OrdinalIgnoreCase) && !m.Enabled).ToList();
                var results = new List<object>();

                // Query installed models first (uses `ollama list`) and only attempt deletion for models that are present locally
                var installed = await OllamaMonitorService.GetInstalledModelsAsync();
                var installedSet = new HashSet<string>((installed ?? new System.Collections.Generic.List<OllamaModelInfo>()).Select(x => x.Name ?? string.Empty), StringComparer.OrdinalIgnoreCase);

                foreach (var m in disabled)
                {
                    try
                    {
                        if (!installedSet.Contains(m.Name))
                        {
                            results.Add(new { name = m.Name, deleted = false, output = "Not installed (skipped)" });
                            continue;
                        }

                        var res = await OllamaMonitorService.DeleteInstalledModelAsync(m.Name);
                        if (res.Success)
                        {
                            // remove from DB as well
                            try { _database.DeleteModel(m.Name); } catch { }
                            results.Add(new { name = m.Name, deleted = true, output = res.Output });
                        }
                        else
                        {
                            results.Add(new { name = m.Name, deleted = false, output = res.Output });
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { name = m.Name, deleted = false, output = ex.Message });
                    }
                }

                return new JsonResult(new { results = results });
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
                            // Do not modify existing model records here. Discovery should only add new models,
                            // not update or re-enable models the user already configured.
                            continue;
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
