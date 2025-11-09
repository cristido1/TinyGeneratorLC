using Microsoft.SemanticKernel.Functions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.SemanticKernel;

using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.Ollama;
using TinyGenerator.Services;

namespace TinyGenerator.Pages
{
    public class ModelsModel : PageModel
    {
    private readonly Services.CostController _cost;
    private readonly PersistentMemoryService _memory;

        public ModelsModel(Services.CostController cost, PersistentMemoryService memory)
        {
            _cost = cost;
            _memory = memory;
        }

        public List<Services.CostController.ModelInfo> Models { get; set; } = new();

        public void OnGet()
        {
            // Mostra solo modelli locali Ollama
            Models = _cost.ListModels().Where(m => m.Provider?.ToLower() == "ollama" && m.IsLocal).ToList();
        }

        public class TestResultItem { public string name = ""; public bool ok; public string? message; }

        public class TestResponse { public int functionCallingScore; public List<TestResultItem> results = new(); }

        // POST handler to run function-calling tests for a model
        public async Task<IActionResult> OnPostTestModelAsync([FromBody] dynamic input)
        {
            try
            {
                string model = (string?)input?.model ?? string.Empty;
                if (string.IsNullOrWhiteSpace(model)) return BadRequest("model required");

                var results = new List<TestResultItem>();

                // Recupera info modello dalla tabella
                var modelInfo = _cost.GetModelInfo(model);
                if (modelInfo == null)
                    return BadRequest($"Modello '{model}' non trovato nella tabella modelli.");

                var provider = modelInfo.Provider ?? "ollama";
                var endpoint = modelInfo.Endpoint ?? string.Empty;

                var configBuilder = new Microsoft.Extensions.Configuration.ConfigurationBuilder();
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    {"provider", provider},
                    {"model", model },
                    {"endpoint", endpoint}
                });
                var config = configBuilder.Build();
                var loggerFactory = HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory)) as Microsoft.Extensions.Logging.ILoggerFactory;
                var logger = loggerFactory?.CreateLogger("KernelFactory");
                var kernelWithPlugins = TinyGenerator.Services.KernelFactory.Create(config, logger) as TinyGenerator.Services.KernelWithPlugins;
                if (kernelWithPlugins == null)
                    return BadRequest("Impossibile creare un kernel reale per il modello selezionato (errore di configurazione o provider non supportato).");
                if (kernelWithPlugins.Kernel is not Microsoft.SemanticKernel.Kernel kernel)
                    return BadRequest("Impossibile creare un kernel reale per il modello selezionato (oggetto kernel non valido).");

                // Create a chat agent for simple interactions (uses compatibility shim)
                var agent = new ChatCompletionAgent("tester", kernel, "Function calling tester", model ?? string.Empty);

                // Test 1: memory save
                try
                {
                    var mk = $"test_{Guid.NewGuid():N}";
                    var key = "fc_test_key";
                    var value = "fc_test_value";
                    var prompt = $"If you were to call a function SaveToMemory(memoryKey, key, value) to persist data, respond EXACTLY with: CALL:SaveToMemory|{mk}|{key}|{value}. Otherwise reply NO_CALL.";
                    var res = await agent.InvokeAsync(prompt);
                    var text = string.Join("\n", res.Select(x => x.Content));
                    if (text.Contains($"CALL:SaveToMemory|{mk}|{key}|{value}"))
                    {
                        // perform the save
                        await _memory.SaveAsync(mk, value);
                        results.Add(new TestResultItem { name = "SaveToMemory", ok = true, message = "Saved to memory." });
                    }
                    else results.Add(new TestResultItem { name = "SaveToMemory", ok = false, message = text });
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "SaveToMemory", ok = false, message = ex.Message }); }

                // Test 2: memory read
                try
                {
                    var mk2 = $"test_{Guid.NewGuid():N}";
                    var key2 = "fc_test_key_read";
                    var value2 = "fc_test_value_read";
                    await _memory.SaveAsync(mk2, value2);
                    var prompt = $"If you were to call a function ReadFromMemory(memoryKey, [keys]) to retrieve data, respond EXACTLY with: CALL:ReadFromMemory|{mk2}|{key2}. Otherwise reply NO_CALL.";
                    var res = await agent.InvokeAsync(prompt);
                    var text = string.Join("\n", res.Select(x => x.Content));
                    if (text.Contains($"CALL:ReadFromMemory|{mk2}|{key2}"))
                    {
                        var found = await _memory.SearchAsync(mk2, value2);
                        var ok = found.Any(x => x == value2);
                        results.Add(new TestResultItem { name = "ReadFromMemory", ok = ok, message = ok ? "Read matches" : ("Read mismatch: " + string.Join(",", found)) });
                    }
                    else results.Add(new TestResultItem { name = "ReadFromMemory", ok = false, message = text });
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "ReadFromMemory", ok = false, message = ex.Message }); }

                // Test 3: DB save (use PersistentMemory as DB proxy)
                try
                {
                    var mk = $"testdb_{Guid.NewGuid():N}";
                    var key = "db_key";
                    var val = "db_val";
                    var prompt = $"If you were to call a function DbSave(memoryKey, key, value) to persist data into DB, respond EXACTLY with: CALL:DbSave|{mk}|{key}|{val}. Otherwise reply NO_CALL.";
                    var res = await agent.InvokeAsync(prompt);
                    var text = string.Join("\n", res.Select(x => x.Content));
                    if (text.Contains($"CALL:DbSave|{mk}|{key}|{val}"))
                    {
                        await _memory.SaveAsync(mk, val);
                        results.Add(new TestResultItem { name = "DbSave", ok = true, message = "Saved (via memory proxy)" });
                    }
                    else results.Add(new TestResultItem { name = "DbSave", ok = false, message = text });
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "DbSave", ok = false, message = ex.Message }); }

                // Test 4: DB read
                try
                {
                    var mk = $"testdbread_{Guid.NewGuid():N}";
                    var key = "db_key_read";
                    var val = "db_val_read";
                    await _memory.SaveAsync(mk, val);
                    var prompt = $"If you were to call a function DbRead(memoryKey, key) to read data previously saved, respond EXACTLY with: CALL:DbRead|{mk}|{key}. Otherwise reply NO_CALL.";
                    var res = await agent.InvokeAsync(prompt);
                    var text = string.Join("\n", res.Select(x => x.Content));
                    if (text.Contains($"CALL:DbRead|{mk}|{key}"))
                    {
                        var found = await _memory.SearchAsync(mk, val);
                        var ok = found.Any(x => x == val);
                        results.Add(new TestResultItem { name = "DbRead", ok = ok, message = ok ? "Read OK" : ("Read mismatch: " + string.Join(",", found)) });
                    }
                    else results.Add(new TestResultItem { name = "DbRead", ok = false, message = text });
                }
                catch (Exception ex) { results.Add(new TestResultItem { name = "DbRead", ok = false, message = ex.Message }); }



                // Skill/plugin già registrate in KernelFactory
                var skillResults = new Dictionary<string, bool?>();

                // Test reali delle skill
                // Ottieni istanze plugin dal kernel (usate per tracking LastCalled)
                var math = kernelWithPlugins.MathPlugin;
                var textPlugin = kernelWithPlugins.TextPlugin;
                var time = kernelWithPlugins.TimePlugin;
                var file = kernelWithPlugins.FileSystemPlugin;
                var http = kernelWithPlugins.HttpPlugin;

                // Prompt e mapping per test LLM-driven
                var skillTestCases = new List<(string name, string skill, string function, string prompt, Func<bool> check)>
                {
                    ("Add", "MathSkill", "add", "Usa la funzione add per sommare 2 e 3.", new Func<bool>(() => math?.LastCalled == "Add")),
                    ("Subtract", "MathSkill", "subtract", "Usa la funzione subtract per sottrarre 7 meno 4.", new Func<bool>(() => math?.LastCalled == "Subtract")),
                    ("Multiply", "MathSkill", "multiply", "Usa la funzione multiply per moltiplicare 6 per 7.", new Func<bool>(() => math?.LastCalled == "Multiply")),
                    ("Divide", "MathSkill", "divide", "Usa la funzione divide per dividere 8 per 2.", new Func<bool>(() => math?.LastCalled == "Divide")),
                    ("ToUpper", "TextSkill", "toupper", "Usa la funzione toupper per convertire 'hello' in maiuscolo.", new Func<bool>(() => textPlugin?.LastCalled == "ToUpper")),
                    ("ToLower", "TextSkill", "tolower", "Usa la funzione tolower per convertire 'CIAO' in minuscolo.", new Func<bool>(() => textPlugin?.LastCalled == "ToLower")),
                    ("Trim", "TextSkill", "trim", "Usa la funzione trim per rimuovere gli spazi da '  spazi  '.", new Func<bool>(() => textPlugin?.LastCalled == "Trim")),
                    ("Length", "TextSkill", "length", "Usa la funzione length per calcolare la lunghezza di '12345'.", new Func<bool>(() => textPlugin?.LastCalled == "Length")),
                    ("Substring", "TextSkill", "substring", "Usa la funzione substring per estrarre 3 caratteri da 'abcdef' a partire da indice 2.", new Func<bool>(() => textPlugin?.LastCalled == "Substring")),
                    ("Join", "TextSkill", "join", "Usa la funzione join per unire ['a','b','c'] con '-'.", new Func<bool>(() => textPlugin?.LastCalled == "Join")),
                    ("Split", "TextSkill", "split", "Usa la funzione split per dividere 'a-b-c' usando '-'.", new Func<bool>(() => textPlugin?.LastCalled == "Split")),
                    ("Now", "TimeSkill", "now", "Usa la funzione now per ottenere la data/ora attuale.", new Func<bool>(() => time?.LastCalled == "Now")),
                    ("Today", "TimeSkill", "today", "Usa la funzione today per ottenere la data di oggi.", new Func<bool>(() => time?.LastCalled == "Today")),
                    ("AddDays", "TimeSkill", "adddays", "Usa la funzione adddays per aggiungere 5 giorni a '2025-01-01'.", new Func<bool>(() => time?.LastCalled == "AddDays")),
                    ("AddHours", "TimeSkill", "addhours", "Usa la funzione addhours per aggiungere 3 ore a '12:00'.", new Func<bool>(() => time?.LastCalled == "AddHours")),
                    ("FileExists", "FileSystem", "file_exists", "Usa la funzione file_exists per verificare se '/etc/hosts' esiste.", new Func<bool>(() => file?.LastCalled == "FileExists")),
                    ("HttpGet", "Http", "http_get", "Usa la funzione http_get per scaricare la pagina https://www.example.com.", new Func<bool>(() => http?.LastCalled == "HttpGetAsync")),
                };

                foreach (var (name, skill, function, prompt, check) in skillTestCases)
                {
                    try
                    {
                        // Azzera LastCalled
                        if (skill == "MathSkill" && math != null) math.LastCalled = null;
                        if (skill == "TextSkill" && textPlugin != null) textPlugin.LastCalled = null;
                        if (skill == "TimeSkill" && time != null) time.LastCalled = null;
                        if (skill == "FileSystem" && file != null) file.LastCalled = null;
                        if (skill == "Http" && http != null) http.LastCalled = null;

                        // Prompt LLM-driven
                        var risposta = await kernel.InvokePromptAsync(prompt);
                        var ok = check();
                        results.Add(new TestResultItem { name = name, ok = ok, message = ok ? $"Skill {name} OK" : $"Skill {name} FAIL" });
                        skillResults[name] = ok;
                    }
                    catch (Exception ex)
                    {
                        results.Add(new TestResultItem { name = name, ok = false, message = ex.Message });
                        skillResults[name] = false;
                    }
                }


                // compute score: number of ok tests / total * 10
                var okCount = results.Count(r => r.ok);
                var score = (int)Math.Round((double)okCount / results.Count * 10);

                // update model record con i risultati delle skill
                try
                {
                    var modelInfoUpdate = _cost.GetModelInfo(model);
                    if (modelInfoUpdate == null)
                        modelInfoUpdate = new Services.CostController.ModelInfo { Name = model, Provider = model.Split(':')[0] };
                    modelInfoUpdate.FunctionCallingScore = score;
                    // Mappa risultati skill su proprietà ModelInfo
                    modelInfoUpdate.SkillToUpper = skillResults.TryGetValue("ToUpper", out var v1) ? v1 : null;
                    modelInfoUpdate.SkillToLower = skillResults.TryGetValue("ToLower", out var v2) ? v2 : null;
                    modelInfoUpdate.SkillTrim = skillResults.TryGetValue("Trim", out var v3) ? v3 : null;
                    modelInfoUpdate.SkillLength = skillResults.TryGetValue("Length", out var v4) ? v4 : null;
                    modelInfoUpdate.SkillSubstring = skillResults.TryGetValue("Substring", out var v5) ? v5 : null;
                    modelInfoUpdate.SkillJoin = skillResults.TryGetValue("Join", out var v6) ? v6 : null;
                    modelInfoUpdate.SkillSplit = skillResults.TryGetValue("Split", out var v7) ? v7 : null;
                    modelInfoUpdate.SkillAdd = skillResults.TryGetValue("Add", out var v8) ? v8 : null;
                    modelInfoUpdate.SkillSubtract = skillResults.TryGetValue("Subtract", out var v9) ? v9 : null;
                    modelInfoUpdate.SkillMultiply = skillResults.TryGetValue("Multiply", out var v10) ? v10 : null;
                    modelInfoUpdate.SkillDivide = skillResults.TryGetValue("Divide", out var v11) ? v11 : null;
                    modelInfoUpdate.SkillSqrt = skillResults.TryGetValue("Sqrt", out var v12) ? v12 : null;
                    modelInfoUpdate.SkillNow = skillResults.TryGetValue("Now", out var v13) ? v13 : null;
                    modelInfoUpdate.SkillToday = skillResults.TryGetValue("Today", out var v14) ? v14 : null;
                    modelInfoUpdate.SkillAddDays = skillResults.TryGetValue("AddDays", out var v15) ? v15 : null;
                    modelInfoUpdate.SkillAddHours = skillResults.TryGetValue("AddHours", out var v16) ? v16 : null;
                    _cost.UpsertModel(modelInfoUpdate);
                }
                catch { }

                var resp = new TestResponse { functionCallingScore = score, results = results };
                return new JsonResult(resp);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
