using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    public class ModelInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public string Processor { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
        public string Until { get; set; } = string.Empty;
    }

    // Simple monitor: stores last prompt per model and can run `ollama ps` to list running models.
    public static class OllamaMonitorService
    {
        private static readonly ConcurrentDictionary<string, (string Prompt, DateTime Ts)> _lastPrompt = new();

        public static void RecordPrompt(string model, string prompt)
        {
            try
            {
                _lastPrompt[model ?? string.Empty] = (prompt ?? string.Empty, DateTime.UtcNow);
            }
            catch { }
        }

        public static (string Prompt, DateTime Ts)? GetLastPrompt(string model)
        {
            if (model == null) return null;
            if (_lastPrompt.TryGetValue(model, out var v)) return v;
            return null;
        }

        public static async Task<List<ModelInfo>> GetRunningModelsAsync()
        {
            return await Task.Run(() =>
            {
                var list = new List<ModelInfo>();
                try
                {
                    var psi = new ProcessStartInfo("ollama", "ps") { RedirectStandardOutput = true, UseShellExecute = false };
                    using var p = Process.Start(psi);
                    if (p == null) return list;
                    p.WaitForExit(3000);
                    var outp = p.StandardOutput.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(outp)) return list;
                    var lines = outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length <= 1) return list;
                    // skip header line
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        // split by two or more spaces
                        var parts = Regex.Split(line, "\\s{2,}");
                        if (parts.Length >= 6)
                        {
                            list.Add(new ModelInfo
                            {
                                Name = parts[0].Trim(),
                                Id = parts[1].Trim(),
                                Size = parts[2].Trim(),
                                Processor = parts[3].Trim(),
                                Context = parts[4].Trim(),
                                Until = parts[5].Trim()
                            });
                        }
                        else if (parts.Length >= 5)
                        {
                            // fallback if 'UNTIL' missing
                            list.Add(new ModelInfo
                            {
                                Name = parts[0].Trim(),
                                Id = parts.Length > 1 ? parts[1].Trim() : string.Empty,
                                Size = parts.Length > 2 ? parts[2].Trim() : string.Empty,
                                Processor = parts.Length > 3 ? parts[3].Trim() : string.Empty,
                                Context = parts.Length > 4 ? parts[4].Trim() : string.Empty,
                                Until = string.Empty
                            });
                        }
                    }
                }
                catch { }
                return list;
            });
        }
    }
}
