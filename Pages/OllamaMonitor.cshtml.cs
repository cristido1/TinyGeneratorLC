using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Pages
{
    [Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
    public class OllamaMonitorModel : PageModel
    {
    private readonly IOllamaMonitorService _monitor;
    private readonly LlamaService _llamaService;
    public List<TinyGenerator.Services.OllamaModelInfo> Models { get; set; } = new();
    public LlamaStatusInfo LlamaStatus { get; set; } = new();
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        public OllamaMonitorModel(IOllamaMonitorService monitor, LlamaService llamaService)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            _llamaService = llamaService ?? throw new ArgumentNullException(nameof(llamaService));
        }

        public void OnGet()
        {
            OnGetRefreshAsync().GetAwaiter().GetResult();
        }

        public async Task OnGetRefreshAsync()
        {
            Models = await _monitor.GetRunningModelsAsync();
            LlamaStatus = await GetLlamaStatusAsync();
        }

        // JSON endpoint for fetching models + last prompt
        public async Task<IActionResult> OnGetModelsAsync()
        {
            var models = await _monitor.GetRunningModelsAsync();
            var list = new List<object>();
            foreach (var m in models)
            {
                var lp = _monitor.GetLastPrompt(m.Name);
                list.Add(new {
                    name = m.Name,
                    id = m.Id,
                    size = m.Size,
                    processor = m.Processor,
                    context = m.Context,
                    until = m.Until,
                    lastPrompt = lp?.Prompt ?? string.Empty,
                    lastPromptTs = lp?.Ts.ToString("o") ?? string.Empty
                });
            }
            return new JsonResult(list);
        }

        public IActionResult OnPostStartWithContextAsync([Microsoft.AspNetCore.Mvc.FromBody] StartContextRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Model)) return new JsonResult(new { success = false, message = "Model missing" });

            // sanitize model and build instance name
            var modelRef = req.Model.Trim();
            var instanceName = modelRef.Replace(':', '-').Replace('/', '-');
            if (req.Context <= 0) req.Context = 8192;
            var nameArg = instanceName + $"-{req.Context}";

            (bool Success, string Output, int ExitCode) RunCommand(string cmd, string args, int timeoutMs = 60000)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(cmd, args)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
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

            // attempt to stop any running instance of this model (best-effort)
            var stopRes = RunCommand("ollama", $"stop \"{modelRef}\"");

            // Check help to see if --context is supported
            var helpRes = RunCommand("ollama", "run --help");
            var useContextFlag = helpRes.Output != null && helpRes.Output.Contains("--context");

            string resultOut;
            (bool Success, string Output, int ExitCode) runRes;
            if (useContextFlag)
            {
                var runArgs = $"run \"{modelRef}\" --context {req.Context} --name \"{nameArg}\" --keep";
                runRes = RunCommand("ollama", runArgs, 2 * 60 * 1000);
                resultOut = stopRes.Output + "\n" + runRes.Output;
                if (!runRes.Success && runRes.Output != null && runRes.Output.Contains("unknown flag"))
                {
                    var fallbackArgs = $"run \"{modelRef}\" --name \"{nameArg}\" --keep";
                    var fallback = RunCommand("ollama", fallbackArgs, 2 * 60 * 1000);
                    resultOut += "\nFALLBACK:\n" + fallback.Output;
                    runRes = fallback;
                }
            }
            else
            {
                var runArgs = $"run \"{modelRef}\" --name \"{nameArg}\" --keep";
                runRes = RunCommand("ollama", runArgs, 2 * 60 * 1000);
                resultOut = stopRes.Output + "\n" + helpRes.Output + "\n" + runRes.Output;
            }

            return new JsonResult(new { success = runRes.Success, stopOutput = stopRes.Output, runOutput = resultOut });
        }

        public class StartContextRequest { public string Model { get; set; } = string.Empty; public int Context { get; set; } = 8192; }

        public IActionResult OnPostKillLlamaAsync()
        {
            try
            {
                _llamaService.StopServer();
            }
            catch { }

            try
            {
                var psi = new ProcessStartInfo("taskkill", "/IM \"llama-server.exe\" /F")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                if (p == null) return new JsonResult(new { success = false, output = "Could not start taskkill" });
                p.WaitForExit(5000);
                var outp = p.StandardOutput.ReadToEnd();
                var err = p.StandardError.ReadToEnd();
                var combined = outp + (string.IsNullOrEmpty(err) ? string.Empty : "\nERR:" + err);
                return new JsonResult(new { success = true, output = combined });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, output = ex.Message });
            }
        }

        public sealed class LlamaStatusInfo
        {
            public string Endpoint { get; set; } = "http://127.0.0.1:11436";
            public bool IsRunning { get; set; }
            public List<string> Models { get; set; } = new();
            public string? Error { get; set; }
        }

        private static async Task<LlamaStatusInfo> GetLlamaStatusAsync()
        {
            var info = new LlamaStatusInfo();
            try
            {
                var healthUrl = info.Endpoint.TrimEnd('/') + "/health";
                var modelsUrl = info.Endpoint.TrimEnd('/') + "/v1/models";

                using var healthRes = await _httpClient.GetAsync(healthUrl);
                if (healthRes.IsSuccessStatusCode)
                {
                    info.IsRunning = true;
                }

                using var modelsRes = await _httpClient.GetAsync(modelsUrl);
                if (modelsRes.IsSuccessStatusCode)
                {
                    var json = await modelsRes.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            {
                                var value = id.GetString();
                                if (!string.IsNullOrWhiteSpace(value)) info.Models.Add(value);
                            }
                        }
                    }
                    info.IsRunning = true;
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            return info;
        }
    }
}
