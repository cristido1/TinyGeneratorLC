using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Pages
{
    [Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken]
    public class OllamaMonitorModel : PageModel
    {
        public List<ModelInfo> Models { get; set; } = new();

        public void OnGet()
        {
        }

        public async Task OnGetRefreshAsync()
        {
            Models = await OllamaMonitorService.GetRunningModelsAsync();
        }

        // JSON endpoint for fetching models + last prompt
        public async Task<IActionResult> OnGetModelsAsync()
        {
            var models = await OllamaMonitorService.GetRunningModelsAsync();
            var list = new List<object>();
            foreach (var m in models)
            {
                var lp = OllamaMonitorService.GetLastPrompt(m.Name);
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

    public async Task<IActionResult> OnPostStartWithContextAsync([Microsoft.AspNetCore.Mvc.FromBody] StartContextRequest req)
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
    }
}
