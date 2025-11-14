using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Reflection;

// Compatibility shim for Semantic Kernel used for development/testing only.
// See README for guidance. This file implements a minimal compatibility
// surface and additional diagnostics (raw dumps) to help debugging provider
// responses. It supports a strict mode via the environment variable
// TINYGENERATOR_STRICT_SK which disables fallbacks and forces fail-fast.

namespace Microsoft.SemanticKernel
{
    // Minimal kernel interface used by the application.
    public interface IKernel
    {
        Task<KernelRunResult> RunAsync(string prompt);
        IMemoryStore Memory { get; }
    }

    public class KernelRunResult
    {
        public string Result { get; set; } = string.Empty;
    }

    public interface IMemoryStore
    {
        Task SaveInformationAsync(string collection, string content, string id);
    }

    public class SimpleKernel : IKernel
    {
        private readonly SimpleMemoryStore _mem = new SimpleMemoryStore();
        private readonly string _notice;

        public SimpleKernel(string notice = "[SimpleKernel: no external model configured]")
        {
            _notice = notice;
        }

        public IMemoryStore Memory => _mem;

        public Task<KernelRunResult> RunAsync(string prompt)
        {
            var shortPrompt = prompt?.Length > 300 ? prompt.Substring(0, 300) + "..." : prompt;
            var resultText = $"{_notice}\nPrompt (truncated): {shortPrompt}";
            return Task.FromResult(new KernelRunResult { Result = resultText });
        }
    }

    public class SimpleMemoryStore : IMemoryStore
    {
        public Task SaveInformationAsync(string collection, string content, string id)
        {
            try
            {
                var safe = Path.Combine("data", "sk_memory.log");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(safe) ?? "data");
                    File.AppendAllText(safe, $"[{DateTime.UtcNow:O}] {collection} {id}: {content}\n");
                }
                catch { }
            }
            catch { }
            return Task.CompletedTask;
        }
    }
}

namespace Microsoft.SemanticKernel.Agents
{
    // Lightweight compatibility ChatCompletionAgent used by the existing codebase.
    // It wraps either the minimal IKernel (SimpleKernel) or a real Semantic Kernel
    // instance. It adds diagnostics and a strict-mode toggle.
    public class ChatCompletionAgent
    {
        private readonly object _kernel;
        public string Name { get; }
        public string? Model { get; }
        public string? Instructions { get; set; }
        private readonly KernelArguments _args;

        private static readonly bool StrictMode;

        static ChatCompletionAgent()
        {
            try
            {
                var v = Environment.GetEnvironmentVariable("TINYGENERATOR_STRICT_SK") ?? Environment.GetEnvironmentVariable("TINYGENERATOR_STRICT") ?? "false";
                v = v.Trim().ToLowerInvariant();
                StrictMode = v == "1" || v == "true" || v == "yes";
            }
            catch { StrictMode = false; }
        }

        public ChatCompletionAgent(string name, object kernel, string description, string model)
        {
            Name = name;
            _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
            Model = model;
            Instructions = description;
            var execSettings = new PromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            };
            _args = new KernelArguments(execSettings);
        }

        public async Task<IEnumerable<ChatResult>> InvokeAsync(string prompt)
        {
            var finalPrompt = string.IsNullOrWhiteSpace(Instructions) ? prompt : Instructions + "\n\n" + prompt;

            // If kernel is our minimal in-process kernel, just use it.
            if (_kernel is Microsoft.SemanticKernel.IKernel ikernel)
            {
                var run = await ikernel.RunAsync(finalPrompt);
                return new[] { new ChatResult { Content = run.Result ?? string.Empty } };
            }

            // If kernel is a real Semantic Kernel instance, instrument the call.
            if (_kernel is Microsoft.SemanticKernel.Kernel kernel)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                object? runObj = null;

                // Attempt to capture the outbound request information (final prompt, args,
                // and a best-effort list of registered functions/skills) to help debugging
                // provider-side slowness. This is best-effort and must not throw.
                try
                {
                    var debug = new Dictionary<string, object?>();
                    debug["Timestamp"] = DateTime.UtcNow;
                    debug["Agent"] = Name;
                    debug["Model"] = Model;
                    debug["FinalPromptLength"] = finalPrompt?.Length ?? 0;
                    debug["FinalPromptPreview"] = finalPrompt is null ? null : (finalPrompt.Length > 2000 ? finalPrompt.Substring(0, 2000) + "...[truncated]" : finalPrompt);
                    debug["InstructionsLength"] = Instructions?.Length ?? 0;

                    try
                    {
                        // Try to serialize execution args (may fail for internal types)
                        debug["Args"] = JsonSerializer.Serialize(_args);
                    }
                    catch
                    {
                        debug["Args"] = _args?.ToString();
                    }

                    // Inspect kernel for registered functions/skills (best-effort via reflection)
                    try
                    {
                        var funcs = new List<string>();
                        var ktype = kernel.GetType();
                        debug["KernelType"] = ktype.FullName;

                        // Inspect properties and fields for enumerable containers that may hold skills/functions
                        var members = new List<object?>();
                        try
                        {
                            foreach (var p in ktype.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                            {
                                object? val = null;
                                try { val = p.GetValue(kernel); } catch { val = null; }
                                if (val == null) continue;
                                members.Add(val);
                            }
                        }
                        catch { }

                        try
                        {
                            foreach (var f in ktype.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                            {
                                object? val = null;
                                try { val = f.GetValue(kernel); } catch { val = null; }
                                if (val == null) continue;
                                members.Add(val);
                            }
                        }
                        catch { }

                        foreach (var m in members)
                        {
                            try
                            {
                                if (m is System.Collections.IEnumerable en && !(m is string))
                                {
                                    var enu = en.GetEnumerator();
                                    int seen = 0;
                                    while (enu.MoveNext() && seen++ < 20)
                                    {
                                        var item = enu.Current;
                                        if (item == null) continue;
                                        var iname = item.GetType().Name;
                                        string desc = iname;
                                        try
                                        {
                                            var nameProp = item.GetType().GetProperty("Name") ?? item.GetType().GetProperty("Id") ?? item.GetType().GetProperty("SkillName");
                                            if (nameProp != null)
                                            {
                                                var nval = nameProp.GetValue(item);
                                                if (nval != null) desc += ":" + nval.ToString();
                                            }
                                            else
                                            {
                                                var tostr = item.ToString();
                                                if (!string.IsNullOrWhiteSpace(tostr)) desc += ":" + (tostr.Length > 200 ? tostr.Substring(0, 200) + "...[truncated]" : tostr);
                                            }
                                        }
                                        catch { }

                                        funcs.Add(desc);
                                    }
                                }
                                else
                                {
                                    var sval = m?.ToString() ?? string.Empty;
                                    if (!string.IsNullOrEmpty(sval)) funcs.Add(sval.Length > 200 ? sval.Substring(0, 200) + "...[truncated]" : sval);
                                }
                            }
                            catch { }
                        }

                        if (funcs.Count > 0) debug["DiscoveredFunctions"] = funcs;
                    }
                    catch { }

                    try
                    {
                        // Respect configuration flag Debug:EnableOutboundGeneration when present.
                        try
                        {
                            bool enabled = true;
                            try
                            {
                                // Best-effort: load configuration from appsettings and environment variables.
                                var builder = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                                    .SetBasePath(System.IO.Directory.GetCurrentDirectory())
                                    .AddJsonFile("appsettings.json", optional: true)
                                    .AddJsonFile("appsettings.Development.json", optional: true)
                                    .AddJsonFile("appsettings.secrets.json", optional: true)
                                    .AddEnvironmentVariables();
                                var cfg = builder.Build();
                                enabled = cfg.GetValue<bool?>("Debug:EnableOutboundGeneration") ?? true;
                            }
                            catch { enabled = true; }

                            if (enabled)
                            {
                                var rawDir = Path.Combine("data");
                                Directory.CreateDirectory(rawDir);
                                var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                                var fname = Path.Combine(rawDir, $"sk_outbound_{ts}_{Guid.NewGuid():N}.json");
                                var opts = new JsonSerializerOptions { WriteIndented = true };
                                File.WriteAllText(fname, JsonSerializer.Serialize(debug, opts));
                            }
                        }
                        catch { }
                    }
                    catch { }
                }
                catch { }

                try
                {
                    var run = await kernel.InvokePromptAsync(finalPrompt ?? string.Empty, _args);
                    runObj = run;
                    sw.Stop();

                    string content = string.Empty;

                    if (StrictMode)
                    {
                        // Strict mode: obtain raw value without multi-level defensive fallbacks, but
                        // still handle JsonElement explicitly to avoid artificial JsonException when
                        // the underlying value is structured (this is observation, not a semantic fallback).
                        try
                        {
                            var rawObj = run?.GetValue<object>();
                            if (rawObj is JsonElement je)
                            {
                                try { content = je.GetRawText(); } catch { content = je.ToString() ?? string.Empty; }
                            }
                            else if (rawObj is string s)
                            {
                                content = s;
                            }
                            else if (rawObj != null)
                            {
                                // Provide a minimal string representation for logging/tracing.
                                content = rawObj.ToString() ?? string.Empty;
                            }
                            else
                            {
                                content = string.Empty;
                            }
                        }
                        catch
                        {
                            // Preserve original strict semantics: try direct string, let exception bubble if invalid.
                            content = run?.GetValue<string>() ?? string.Empty;
                        }
                    }
                    else
                    {
                        // Compatibility extraction: prefer object extraction and handle JsonElement.
                        try
                        {
                            object? obj = null;
                            try { obj = run?.GetValue<object>(); }
                            catch { try { content = run?.GetValue<string>() ?? string.Empty; } catch { content = run?.ToString() ?? string.Empty; } }

                            if (obj != null)
                            {
                                if (obj is JsonElement je)
                                {
                                    try { content = je.GetRawText(); } catch { content = je.ToString() ?? string.Empty; }
                                }
                                else
                                {
                                    try { content = JsonSerializer.Serialize(obj); } catch { content = obj.ToString() ?? string.Empty; }
                                }
                            }
                        }
                        catch
                        {
                            try { content = run?.ToString() ?? string.Empty; } catch { content = string.Empty; }
                        }
                    }

                    // Write a compact log entry with preview and hash.
                    try
                    {
                        var safeLog = Path.Combine("data", "sk_invoke.log");
                        try { Directory.CreateDirectory(Path.GetDirectoryName(safeLog) ?? "data"); } catch { }

                        var respLen = content?.Length ?? 0;
                        var preview = string.Empty;
                        if (!string.IsNullOrEmpty(content))
                        {
                            preview = content.Replace("\r", " ").Replace("\n", " ");
                            if (preview.Length > 2000) preview = preview.Substring(0, 2000) + "...[truncated]";
                        }

                        string respHash = string.Empty;
                        try { using var sha = SHA256.Create(); var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty); var hash = sha.ComputeHash(bytes); respHash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(); } catch { respHash = string.Empty; }

                        var line = $"[{DateTime.UtcNow:O}] Agent={Name} Model={Model} Elapsed={sw.Elapsed.TotalSeconds:0.###}s RespLen={respLen} RespHash={respHash} RespPreview=\"{preview}\"\n";
                        File.AppendAllText(safeLog, line);
                    }
                    catch { }

                    try { Console.WriteLine($"[SKCompatibility] InvokePromptAsync elapsed={sw.Elapsed.TotalSeconds:0.###}s, respLen={content?.Length ?? 0}, prompt={prompt}"); } catch { }

                    return new[] { new ChatResult { Content = content ?? string.Empty } };
                }
                catch (Exception ex)
                {
                    sw.Stop();

                    try
                    {
                        var safeLog = Path.Combine("data", "sk_invoke.log");
                        try { Directory.CreateDirectory(Path.GetDirectoryName(safeLog) ?? "data"); } catch { }
                        var line = $"[{DateTime.UtcNow:O}] Agent={Name} Model={Model} ERROR Elapsed={sw.Elapsed.TotalSeconds:0.###}s Ex={ex.GetType().Name}:{ex.Message} Stack={ex}\n";
                        File.AppendAllText(safeLog, line);
                    }
                    catch { }

                    // Attempt to dump raw response (via reflection) to a file for debugging.
                    try
                    {
                        if (runObj != null)
                        {
                            object? extracted = null;
                            try
                            {
                                var method = runObj.GetType().GetMethods().FirstOrDefault(mi => mi.Name == "GetValue" && mi.IsGenericMethod);
                                if (method != null)
                                {
                                    var gm = method.MakeGenericMethod(new[] { typeof(object) });
                                    extracted = gm.Invoke(runObj, null);
                                }
                            }
                            catch { }

                            var rawDir = Path.Combine("data");
                            try { Directory.CreateDirectory(rawDir); } catch { }
                            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                            // Only write raw dump files when outbound generation is enabled (same flag as sk_outbound)
                            try
                            {
                                bool enabled = true;
                                try
                                {
                                    var builder = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                                        .SetBasePath(System.IO.Directory.GetCurrentDirectory())
                                        .AddJsonFile("appsettings.json", optional: true)
                                        .AddJsonFile("appsettings.Development.json", optional: true)
                                        .AddJsonFile("appsettings.secrets.json", optional: true)
                                        .AddEnvironmentVariables();
                                    var cfg = builder.Build();
                                    enabled = cfg.GetValue<bool?>("Debug:EnableOutboundGeneration") ?? true;
                                }
                                catch { enabled = true; }

                                if (enabled)
                                {
                                    var fname = Path.Combine(rawDir, $"sk_raw_{ts}_{Guid.NewGuid():N}.json");
                                    try
                                    {
                                        if (extracted is JsonElement je)
                                        {
                                            File.WriteAllText(fname, je.GetRawText());
                                        }
                                        else if (extracted != null)
                                        {
                                            try { File.WriteAllText(fname, JsonSerializer.Serialize(extracted)); }
                                            catch { try { File.WriteAllText(fname, extracted.ToString() ?? string.Empty); } catch { } }
                                        }
                                        else
                                        {
                                            try { File.WriteAllText(fname, runObj?.ToString() ?? ex.ToString()); } catch { }
                                        }
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }

                    Console.WriteLine($"[SKCompatibility] InvokePromptAsync threw {ex.GetType().Name} after {sw.Elapsed.TotalSeconds:0.###}s: {ex}");

                    if (StrictMode)
                    {
                        // In strict mode we want to fail fast and surface the exception to the caller.
                        throw;
                    }

                    // Compatibility fallback: return an error ChatResult so tests can continue in dev mode.
                    return new[] { new ChatResult { Content = $"[SK Error] {ex.GetType().Name}: {ex.Message}" } };
                }
            }

            throw new InvalidOperationException("Kernel type non supportato in ChatCompletionAgent");
        }
    }

    public class ChatResult
    {
        public string Content { get; set; } = string.Empty;
    }
}

namespace Microsoft.SemanticKernel.Planning
{
    public class Plan
    {
        public string? Goal { get; set; }
        public List<PlanStep> Steps { get; set; } = new List<PlanStep>();
    }

    public class PlanStep
    {
        public string Description { get; set; } = string.Empty;
    }

    public class HandlebarsPlanner
    {
        private readonly Microsoft.SemanticKernel.IKernel _kernel;

        public HandlebarsPlanner(Microsoft.SemanticKernel.IKernel kernel)
        {
            _kernel = kernel;
        }

        public Task<Plan> CreatePlanAsync(string prompt)
        {
            var p = new Plan { Goal = prompt };
            p.Steps.Add(new PlanStep { Description = "trama" });
            p.Steps.Add(new PlanStep { Description = "personaggi" });
            p.Steps.Add(new PlanStep { Description = "primo capitolo" });
            p.Steps.Add(new PlanStep { Description = "secondo capitolo" });
            p.Steps.Add(new PlanStep { Description = "terzo capitolo" });
            p.Steps.Add(new PlanStep { Description = "riassunto cumulativo" });
            return Task.FromResult(p);
        }
    }
}

