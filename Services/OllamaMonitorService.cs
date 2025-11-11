using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TinyGenerator.Services
{
    public class OllamaModelInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public string Processor { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
        public string Until { get; set; } = string.Empty;
    }

    // NOTE: do not define a `ModelInfo` type in this namespace to avoid collision
    // with the canonical POCO in TinyGenerator.Models.ModelInfo. Use
    // `OllamaModelInfo` for Ollama-specific monitoring data.

    // Simple monitor: stores last prompt per model and can run `ollama ps` to list running models.
    public static class OllamaMonitorService
    {
        private static readonly ConcurrentDictionary<string, (string Prompt, DateTime Ts)> _lastPrompt = new();

        public static void RecordPrompt(string model, string prompt)
        {
            try
            {
                _lastPrompt[model ?? string.Empty] = (prompt ?? string.Empty, DateTime.UtcNow);

                // Persist recent prompts so they survive restarts and can be inspected later.
                try
                {
                    var dbPath = "data/storage.db";
                    var dir = Path.GetDirectoryName(dbPath) ?? ".";
                    Directory.CreateDirectory(dir);
                    using var conn = new SqliteConnection($"Data Source={dbPath}");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS prompts (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  model TEXT,
  prompt TEXT,
  ts TEXT
);";
                    cmd.ExecuteNonQuery();

                    using var ins = conn.CreateCommand();
                    ins.CommandText = "INSERT INTO prompts(model, prompt, ts) VALUES($m,$p,$ts);";
                    ins.Parameters.AddWithValue("$m", model ?? string.Empty);
                    ins.Parameters.AddWithValue("$p", prompt ?? string.Empty);
                    ins.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
                    ins.ExecuteNonQuery();
                }
                catch { /* non-fatal: best-effort persistence */ }
            }
            catch { }
        }

        public static (string Prompt, DateTime Ts)? GetLastPrompt(string model)
        {
            if (model == null) return null;
            if (_lastPrompt.TryGetValue(model, out var v)) return v;
            return null;
        }

    public static async Task<List<OllamaModelInfo>> GetRunningModelsAsync()
        {
            return await Task.Run(() =>
            {
        var list = new List<OllamaModelInfo>();
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
                            list.Add(new OllamaModelInfo
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
                            list.Add(new OllamaModelInfo
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

        // List installed Ollama models (best-effort) by calling `ollama list` and parsing output.
    public static async Task<List<OllamaModelInfo>> GetInstalledModelsAsync()
        {
            return await Task.Run(() =>
            {
        var list = new List<OllamaModelInfo>();
                try
                {
                    var psi = new ProcessStartInfo("ollama", "list") { RedirectStandardOutput = true, UseShellExecute = false };
                    using var p = Process.Start(psi);
                    if (p == null) return list;
                    p.WaitForExit(3000);
                    var outp = p.StandardOutput.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(outp)) return list;
                    var lines = outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    // skip possible header line(s). Heuristic: lines that contain 'NAME' or 'ID' are headers
                    foreach (var raw in lines)
                    {
                        var line = raw.Trim();
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var lower = line.ToLowerInvariant();
                        if (lower.Contains("name") || lower.Contains("id") || lower.StartsWith("----")) continue;
                        // take first token as model name
                        var parts = System.Text.RegularExpressions.Regex.Split(line, "\\s{2,}");
                        string name = parts.Length > 0 ? parts[0].Trim() : string.Empty;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        list.Add(new OllamaModelInfo { Name = name });
                    }
                }
                catch { }
                return list;
            });
        }

        // Best-effort: stop any running instance for the model and run it with the requested context.
        public static async Task<(bool Success, string Output)> StartModelWithContextAsync(string modelRef, int context)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Run a process and return success flag, combined output and exit code
                    (bool Success, string Output, int ExitCode) RunCommand(string cmd, string args, int timeoutMs = 60000)
                    {
                        try
                        {
                            var psi = new ProcessStartInfo(cmd, args)
                            {
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false
                            };
                            using var p = Process.Start(psi);
                            if (p == null) return (false, "Could not start process", -1);
                            p.WaitForExit(timeoutMs);
                            var outp = p.StandardOutput.ReadToEnd();
                            var err = p.StandardError.ReadToEnd();
                            var combined = outp + (string.IsNullOrEmpty(err) ? string.Empty : "\nERR:" + err);
                            return (p.ExitCode == 0, combined, p.ExitCode);
                        }
                        catch (Exception ex)
                        {
                            return (false, "EX:" + ex.Message, -1);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(modelRef)) return (false, "modelRef empty");
                    var model = modelRef.Trim();
                    var instanceName = model.Replace(':', '-').Replace('/', '-');
                    var nameArg = instanceName + $"-{context}";

                    var stopRes = RunCommand("ollama", $"stop \"{model}\"");

                    // Check if 'ollama run' supports the --context flag by checking run help output
                    var helpRes = RunCommand("ollama", "run --help");
                    var useContextFlag = helpRes.Output != null && helpRes.Output.Contains("--context");

                    string finalOut;
                    (bool Success, string Output, int ExitCode) runRes;
                    if (useContextFlag)
                    {
                        var runArgs = $"run \"{model}\" --context {context} --name \"{nameArg}\" --keep";
                        runRes = RunCommand("ollama", runArgs, 2 * 60 * 1000);
                        finalOut = stopRes.Output + "\n" + runRes.Output;
                        // If the run failed due to unknown flag or non-zero exit code, try fallback
                        if (!runRes.Success && runRes.Output != null && runRes.Output.Contains("unknown flag") )
                        {
                            // fallback: try running without --context
                            var fallbackArgs = $"run \"{model}\" --name \"{nameArg}\" --keep";
                            var fallback = RunCommand("ollama", fallbackArgs, 2 * 60 * 1000);
                            finalOut += "\nFALLBACK:\n" + fallback.Output;
                            runRes = fallback;
                        }
                    }
                    else
                    {
                        // Older ollama doesn't support --context; run without it and report that context couldn't be set
                        var runArgs = $"run \"{model}\" --name \"{nameArg}\" --keep";
                        runRes = RunCommand("ollama", runArgs, 2 * 60 * 1000);
                        finalOut = stopRes.Output + "\n" + helpRes.Output + "\n" + runRes.Output;
                    }

                    return (runRes.Success, finalOut);
                }
                catch (Exception ex)
                {
                    return (false, "EX:" + ex.Message);
                }
            });
        }
    }
}
