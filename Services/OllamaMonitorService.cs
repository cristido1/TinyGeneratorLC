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
        private static string? _ollamaEndpoint = null;

        public static void SetOllamaEndpoint(string? endpoint)
        {
            _ollamaEndpoint = endpoint;
        }

        private static ProcessStartInfo CreateOllamaProcessStartInfo(string arguments)
        {
            var psi = new ProcessStartInfo("ollama", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            
            // Imposta OLLAMA_HOST se configurato
            if (!string.IsNullOrWhiteSpace(_ollamaEndpoint))
            {
                psi.EnvironmentVariables["OLLAMA_HOST"] = _ollamaEndpoint;
            }
            
            return psi;
        }

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
                    var psi = CreateOllamaProcessStartInfo("ps");
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
                    var psi = CreateOllamaProcessStartInfo("list");
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

        // Attempt to delete/uninstall an installed Ollama model by trying several common CLI verbs.
        // Returns (success, output).
        public static async Task<(bool Success, string Output)> DeleteInstalledModelAsync(string modelName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(modelName)) return (false, "modelName empty");

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

                    var tried = new List<string>();
                    // Primary command supported by modern Ollama
                    var candidates = new[]
                    {
                        $"rm \"{modelName}\"",
                        // Older variants: 'models rm' is sometimes present
                        $"models rm \"{modelName}\""
                    };

                    foreach (var args in candidates)
                    {
                        tried.Add(args);
                        var res = RunCommand("ollama", args);
                        if (res.Success)
                        {
                            return (true, "Command: ollama " + args + "\n" + res.Output);
                        }
                    }

                    // If none succeeded, return attempted commands for diagnostics
                    return (false, "Tried commands: " + string.Join("; ", tried));
                }
                catch (Exception ex)
                {
                    return (false, "EX:" + ex.Message);
                }
            });
        }

        // Stop a running Ollama model instance (best-effort). Returns (success, output).
        public static async Task<(bool Success, string Output)> StopModelAsync(string modelName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(modelName)) return (false, "modelName empty");
                    var psi = new ProcessStartInfo("ollama", $"stop \"{modelName}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    using var p = Process.Start(psi);
                    if (p == null) return (false, "Could not start process");
                    p.WaitForExit(30000);
                    var outp = p.StandardOutput.ReadToEnd();
                    var err = p.StandardError.ReadToEnd();
                    var combined = outp + (string.IsNullOrEmpty(err) ? string.Empty : "\nERR:" + err);
                    return (p.ExitCode == 0, combined);
                }
                catch (Exception ex)
                {
                    return (false, "EX:" + ex.Message);
                }
            });
        }

        /// <summary>
        /// Interroga l'endpoint HTTP /api/ps di Ollama per ottenere informazioni in tempo reale sui modelli in esecuzione.
        /// Ritorna informazioni più dettagliate rispetto a `ollama ps`.
        /// </summary>
        public static async Task<List<OllamaModelInfo>> GetRunningModelsFromHttpAsync()
        {
            var list = new List<OllamaModelInfo>();
            try
            {
                string baseUrl = _ollamaEndpoint?.TrimEnd('/') ?? "http://localhost:11434";
                using var client = new System.Net.Http.HttpClient();
                var response = await client.GetAsync($"{baseUrl}/api/ps");
                if (!response.IsSuccessStatusCode) return list;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("models", out var modelsArray) && modelsArray.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var modelObj in modelsArray.EnumerateArray())
                    {
                        var name = modelObj.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
                        var id = modelObj.TryGetProperty("digest", out var digestProp) ? digestProp.GetString() ?? string.Empty : string.Empty;
                        var size = modelObj.TryGetProperty("size", out var sizeProp) ? FormatBytes(sizeProp.GetInt64()) : string.Empty;
                        var processor = modelObj.TryGetProperty("processor", out var procProp) ? procProp.GetString() ?? string.Empty : string.Empty;
                        var context = modelObj.TryGetProperty("details", out var detailsProp) && 
                                     detailsProp.TryGetProperty("parameter_size", out var paramProp) 
                            ? paramProp.GetString() ?? string.Empty : string.Empty;
                        var until = modelObj.TryGetProperty("expires_at", out var expireProp) ? expireProp.GetString() ?? string.Empty : string.Empty;

                        list.Add(new OllamaModelInfo
                        {
                            Name = name,
                            Id = id,
                            Size = size,
                            Processor = processor,
                            Context = context,
                            Until = until
                        });
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>
        /// Legge i log di Ollama e cerca informazioni su richieste completate.
        /// Ritorna una lista di entry dai log con timestamp, modello, e nota (se presente).
        /// </summary>
        public static async Task<List<OllamaLogEntry>> GetRecentLogsAsync(string? noteFilter = null)
        {
            var entries = new List<OllamaLogEntry>();
            try
            {
                // Prova a leggere i log da ~/.ollama/logs/server.log
                var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var logFile = Path.Combine(homeDir, ".ollama", "logs", "server.log");

                if (!File.Exists(logFile))
                    return entries;

                var lines = await File.ReadAllLinesAsync(logFile);
                // Leggi le ultime 1000 linee per performance
                var recentLines = lines.Length > 1000 ? lines[(lines.Length - 1000)..] : lines;

                foreach (var line in recentLines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Cerca pattern: "time" : "timestamp", "level" : "info", "msg" : "message"
                    // Cerca anche pattern con note come "writing", "evaluating", etc
                    var entry = ParseOllamaLogLine(line);
                    if (entry != null && (string.IsNullOrWhiteSpace(noteFilter) || entry.Note?.Contains(noteFilter, StringComparison.OrdinalIgnoreCase) == true))
                    {
                        entries.Add(entry);
                    }
                }
            }
            catch { }
            return entries;
        }

        /// <summary>
        /// Registra una nota associata a un modello Ollama per il tracciamento.
        /// La nota verrà aggiunta ai log tramite commenti o metadati per il recupero successivo.
        /// </summary>
        public static void RecordModelNote(string model, string note)
        {
            try
            {
                // Aggiunge la nota al dizionario di tracking
                var prompt = _lastPrompt.TryGetValue(model, out var existing) ? existing.Prompt : string.Empty;
                var noteKey = $"[NOTE:{note}]";
                var newPrompt = $"{noteKey} {prompt}".Trim();
                _lastPrompt[model] = (newPrompt, DateTime.UtcNow);
            }
            catch { }
        }

        /// <summary>
        /// Recupera le note associate a un modello Ollama dal tracking interno.
        /// </summary>
        public static string? GetModelNote(string model)
        {
            if (_lastPrompt.TryGetValue(model, out var data))
            {
                var match = Regex.Match(data.Prompt, @"\[NOTE:([^\]]+)\]");
                return match.Success ? match.Groups[1].Value : null;
            }
            return null;
        }

        private static OllamaLogEntry? ParseOllamaLogLine(string line)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;

                var timestamp = root.TryGetProperty("time", out var timeProp) ? timeProp.GetString() ?? string.Empty : string.Empty;
                var level = root.TryGetProperty("level", out var levelProp) ? levelProp.GetString() ?? string.Empty : string.Empty;
                var msg = root.TryGetProperty("msg", out var msgProp) ? msgProp.GetString() ?? string.Empty : string.Empty;

                // Estrae il modello dal messaggio se presente (es: "model 'phi3:mini' loaded")
                var modelMatch = Regex.Match(msg, @"model\s+'([^']+)'", RegexOptions.IgnoreCase);
                var model = modelMatch.Success ? modelMatch.Groups[1].Value : string.Empty;

                // Estrae la nota se presente (es: "[NOTE:writing]" nel messaggio)
                var noteMatch = Regex.Match(msg, @"\[NOTE:([^\]]+)\]");
                var note = noteMatch.Success ? noteMatch.Groups[1].Value : null;

                // Determina lo stato: "completed", "running", "error", etc
                var status = level == "error" ? "error" : (msg.Contains("completed", StringComparison.OrdinalIgnoreCase) ? "completed" : "running");

                return new OllamaLogEntry
                {
                    Timestamp = timestamp,
                    Level = level,
                    Message = msg,
                    Model = model,
                    Note = note,
                    Status = status
                };
            }
            catch { }
            return null;
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    /// <summary>
    /// Rappresenta una entry dai log di Ollama
    /// </summary>
    public class OllamaLogEntry
    {
        public string Timestamp { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string? Note { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}


