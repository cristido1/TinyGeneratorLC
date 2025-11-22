using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Models;
using ModelRecord = TinyGenerator.Models.ModelInfo;

namespace TinyGenerator.Services
{
    public class OllamaManagementService : IOllamaManagementService
    {
        private readonly DatabaseService _database;

        public OllamaManagementService(DatabaseService database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public async Task<List<object>> PurgeDisabledModelsAsync()
        {
            var disabled = _database.ListModels()
                .Where(m => string.Equals(m.Provider, "ollama", StringComparison.OrdinalIgnoreCase) && !m.Enabled)
                .ToList();

            var results = new List<object>();

            // Query installed models first (uses `ollama list`) and only attempt deletion for models that are present locally
            var installed = await OllamaMonitorService.GetInstalledModelsAsync();
            var installedSet = new HashSet<string>(
                (installed ?? new List<OllamaModelInfo>()).Select(x => x.Name ?? string.Empty),
                StringComparer.OrdinalIgnoreCase
            );

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

            return results;
        }

        public async Task<int> RefreshRunningContextsAsync()
        {
            var running = await OllamaMonitorService.GetRunningModelsAsync();
            if (running == null || running.Count == 0)
            {
                return 0;
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
                            Metadata = JsonSerializer.Serialize(r),
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

            return updated;
        }

        /// <summary>
        /// Load a model into memory by sending a simple generate request to Ollama.
        /// This ensures the model is loaded and ready before actual tests run.
        /// No response text is processed; we just wait for the request to complete.
        /// </summary>
        public async Task<bool> WarmupModelAsync(string model, int timeoutSeconds = 60)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
                
                var payload = new
                {
                    model = model,
                    prompt = "ok",
                    stream = false
                };

                var jsonContent = new System.Net.Http.StringContent(
                    JsonSerializer.Serialize(payload),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var response = await client.PostAsync("http://localhost:11434/api/generate", jsonContent);
                
                // Just check if we got a response; don't care about content
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                // Warmup is best-effort; log but don't fail
                System.Console.WriteLine($"[Warmup] Failed to warmup model '{model}': {ex.Message}");
                return false;
            }
        }
    }
}
