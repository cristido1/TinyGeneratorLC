using System;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.SemanticKernel.Agents;
using System.Text.RegularExpressions;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public interface ITestService
    {
        Task ExecuteTestAsync(int runId, int idx, TestDefinition t, string model, ModelInfo modelInfo, string agentInstructions, ChatCompletionAgent? defaultAgent);
    }

    public class TestService : ITestService
    {
        private readonly DatabaseService _database;
        private readonly ProgressService _progress;
        private readonly StoriesService _stories;
        private readonly KernelFactory _factory;

        public TestService(DatabaseService database, ProgressService progress, StoriesService stories, KernelFactory factory)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        private static string? FlattenResultToText(object? result)
        {
            if (result == null) return null;
            try
            {
                // If result is a string, try to parse it as JSON; otherwise return trimmed string
                if (result is string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    var trimmed = s.Length > 16000 ? s.Substring(0, 16000) + "..." : s;
                    try
                    {
                        using var doc = JsonDocument.Parse(trimmed);
                        var extracted = ExtractTextFromElement(doc.RootElement);
                        return string.IsNullOrWhiteSpace(extracted) ? trimmed : extracted;
                    }
                    catch (JsonException)
                    {
                        return trimmed;
                    }
                }

                // If it's a JsonElement
                if (result is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.String) return je.GetString();
                    var extracted = ExtractTextFromElement(je);
                    if (!string.IsNullOrWhiteSpace(extracted)) return extracted;
                    try { return JsonSerializer.Serialize(je); } catch { return je.ToString(); }
                }

                // IEnumerable (array of results)
                if (result is System.Collections.IEnumerable ie && !(result is System.Collections.IDictionary))
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var item in ie)
                    {
                        var part = FlattenResultToText(item);
                        if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
                    }
                    if (parts.Count > 0) return string.Join("\n---\n", parts);
                }

                // If object has a Content property, handle it (Content may itself be a string containing JSON)
                var typ = result.GetType();
                var contentProp = typ.GetProperty("Content");
                if (contentProp != null)
                {
                    var contentVal = contentProp.GetValue(result);
                    if (contentVal != null)
                    {
                        // If Content is a string that contains JSON
                        if (contentVal is string cvs)
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(cvs);
                                var ext = ExtractTextFromElement(doc.RootElement);
                                if (!string.IsNullOrWhiteSpace(ext)) return ext;
                            }
                            catch { /* not JSON, fallthrough */ }
                        }

                        var itemsProp = contentVal.GetType().GetProperty("Items");
                        if (itemsProp != null)
                        {
                            var itemsVal = itemsProp.GetValue(contentVal) as System.Collections.IEnumerable;
                            if (itemsVal != null)
                            {
                                var sb = new System.Text.StringBuilder();
                                foreach (var item in itemsVal)
                                {
                                    var textProp = item.GetType().GetProperty("Text");
                                    if (textProp != null)
                                    {
                                        var textVal = textProp.GetValue(item) as string;
                                        if (!string.IsNullOrWhiteSpace(textVal))
                                        {
                                            // If the text itself is JSON, try to extract known keys
                                            var extracted = TryExtractFromPossiblyJsonText(textVal);
                                            if (!string.IsNullOrWhiteSpace(extracted))
                                            {
                                                if (sb.Length > 0) sb.Append('\n');
                                                sb.Append(extracted);
                                            }
                                            else
                                            {
                                                if (sb.Length > 0) sb.Append('\n');
                                                sb.Append(textVal);
                                            }
                                        }
                                    }
                                }
                                if (sb.Length > 0) return sb.ToString();
                            }
                        }
                    }
                }

                // Direct Text property on object
                var textDirect = typ.GetProperty("Text");
                if (textDirect != null)
                {
                    var tv = textDirect.GetValue(result) as string;
                    if (!string.IsNullOrWhiteSpace(tv)) return tv;
                }

                // Fallback: serialize object
                try { return JsonSerializer.Serialize(result); } catch { return null; }
            }
            catch { return null; }
        }

        private static long? TryExtractIdFromJsonString(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var found = FindIdInJsonElement(doc.RootElement);
                if (found.HasValue) return found.Value;
            }
            catch { }
            try
            {
                // Some providers might double-encode JSON inside a string value. Try to find a JSON substring and parse.
                var m = Regex.Match(raw, @"\{[\s\S]*?\}");
                if (m.Success && !string.IsNullOrWhiteSpace(m.Value))
                {
                    using var doc2 = JsonDocument.Parse(m.Value);
                    var found2 = FindIdInJsonElement(doc2.RootElement);
                    if (found2.HasValue) return found2.Value;
                }
            }
            catch { }
            try
            {
                // Fallback: try regex to find numeric id property
                var m2 = Regex.Match(raw, @"""id""\s*:\s*(\d+)");
                if (m2.Success && long.TryParse(m2.Groups[1].Value, out var v)) return v;
            }
            catch { }
            return null;
        }

        private static long? FindIdInJsonElement(JsonElement el)
        {
            try
            {
                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                    {
                        if (idEl.TryGetInt64(out var v)) return v;
                    }
                    if (el.TryGetProperty("Result", out var resultEl))
                    {
                        var inner = FindIdInJsonElement(resultEl);
                        if (inner.HasValue) return inner.Value;
                    }
                    foreach (var prop in el.EnumerateObject())
                    {
                        var nested = FindIdInJsonElement(prop.Value);
                        if (nested.HasValue) return nested.Value;
                    }
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray())
                    {
                        var nested = FindIdInJsonElement(item);
                        if (nested.HasValue) return nested.Value;
                    }
                }
                else if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(s);
                            return FindIdInJsonElement(doc.RootElement);
                        }
                        catch { }
                    }
                }
                return null;
            }
            catch { return null; }
        }

        private static string? TryExtractFromPossiblyJsonText(string textVal)
        {
            if (string.IsNullOrWhiteSpace(textVal)) return null;
            var t = textVal.Trim();
            if (!t.StartsWith("{") && !t.StartsWith("[")) return null;
            try
            {
                using var doc = JsonDocument.Parse(t);
                // prefer common keys
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("overall_evaluation", out var eval) && eval.ValueKind == JsonValueKind.String) return eval.GetString();
                    if (doc.RootElement.TryGetProperty("score", out var score) && (score.ValueKind == JsonValueKind.Number || score.ValueKind == JsonValueKind.String)) return score.ToString();
                    if (doc.RootElement.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String) return resp.GetString();
                }
                // else fallback to the full text
                return doc.RootElement.ToString();
            }
            catch { return null; }
        }

        private static string ExtractTextFromElement(JsonElement el)
        {
            try
            {
                if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? string.Empty;
                // If object has Content
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("Content", out var contentEl))
                {
                    // Content may be a string containing JSON
                    if (contentEl.ValueKind == JsonValueKind.String)
                    {
                        var inner = contentEl.GetString();
                        if (!string.IsNullOrWhiteSpace(inner))
                        {
                            try
                            {
                                using var innerDoc = JsonDocument.Parse(inner);
                                return ExtractTextFromElement(innerDoc.RootElement);
                            }
                            catch { return inner; }
                        }
                    }
                    // If Content.Items exists
                    if (contentEl.ValueKind == JsonValueKind.Object && contentEl.TryGetProperty("Items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var item in itemsEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Text", out var t) && t.ValueKind == JsonValueKind.String)
                            {
                                if (sb.Length > 0) sb.Append('\n');
                                sb.Append(t.GetString());
                            }
                        }
                        return sb.ToString();
                    }
                }

                // If object has Items at root
                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("Items", out var itemsRoot) && itemsRoot.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var item in itemsRoot.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Text", out var t) && t.ValueKind == JsonValueKind.String)
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append(t.GetString());
                        }
                    }
                    return sb.ToString();
                }

                // Try direct properties
                if (el.ValueKind == JsonValueKind.Object)
                {
                    if (el.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String) return resp.GetString() ?? string.Empty;
                    if (el.TryGetProperty("output", out var outp) && outp.ValueKind == JsonValueKind.Object && outp.TryGetProperty("response", out var resp2) && resp2.ValueKind == JsonValueKind.String) return resp2.GetString() ?? string.Empty;
                }

                return el.ToString();
            }
            catch { return el.ToString(); }
        }

        private async Task<(bool completed, string? error, double elapsedSeconds, string? raw, object? resultObj)> InvokeAgentSafeAsync(ChatCompletionAgent? agent, string prompt, int timeoutMs)
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
                    raw = FlattenResultToText(result);
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
                return (false, ex?.Message ?? ex?.GetType()?.Name ?? string.Empty, 0.0, null, null);
            }
        }

        public async Task ExecuteTestAsync(int runId, int idx, TestDefinition t, string model, ModelInfo modelInfo, string agentInstructions, ChatCompletionAgent? defaultAgent)
        {
            // Execute a single test step based on the TestDefinition type (question, functioncall, writer)
            var stepId = _database.AddTestStep(runId, idx, t.FunctionName ?? ("test_" + t.Id.ToString()), System.Text.Json.JsonSerializer.Serialize(new { prompt = t.Prompt }));
            try
            {
                _progress?.Append(runId.ToString(), $"Running step {idx}: {t.FunctionName ?? ("id_" + t.Id.ToString())}");
                var swStep = System.Diagnostics.Stopwatch.StartNew();

                var testType = (t.TestType ?? string.Empty).ToLowerInvariant();
                switch (testType)
                {
                    case "question":
                        await HandleQuestionAsync(t, stepId, swStep, runId, idx, defaultAgent);
                        break;
                    case "writer":
                    case "functioncall":
                        // Setup for functioncall and writer
                        var stepAllowed = PluginHelpers.NormalizeList((t.AllowedPlugins ?? string.Empty).Split(',').Select(s => (s ?? string.Empty).Trim()).Where(s => !string.IsNullOrWhiteSpace(s))).ToList();
                        if (stepAllowed.Count == 0)
                        {
                            // Do not add any plugins if AllowedPlugins is missing. If the caller intentionally uses 'library' fallback they should set AllowedPlugins explicitly.
                            // However, preserve behavior for texteval group; if this is a texteval and no allowed plugin, use 'evaluator'.
                            var libLower = (t.Library ?? string.Empty).ToLowerInvariant();
                            var evaluatorLibsLocal = new[] { "coerenza_narrativa", "struttura", "caratterizzazione_personaggi", "dialoghi", "ritmo", "originalita", "stile", "worldbuilding", "coerenza_tematica", "impatto_emotivo" };
                            if (string.Equals(t.GroupName, "texteval", StringComparison.OrdinalIgnoreCase) || evaluatorLibsLocal.Contains(libLower))
                            {
                                stepAllowed.Add("evaluator");
                            }
                        }
                        // Ensure writer tests have story plugin available
                        if (testType == "writer" && !stepAllowed.Contains("story")) stepAllowed.Add("story");
                        // Ensure texteval/evaluator tests use the evaluator skill when allowed_plugins is empty or unspecified
                        var libLowerTemp = (t.Library ?? string.Empty).ToLowerInvariant();
                        var evaluatorLibsTemp = new[] { "coerenza_narrativa", "struttura", "caratterizzazione_personaggi", "dialoghi", "ritmo", "originalita", "stile", "worldbuilding", "coerenza_tematica", "impatto_emotivo" };
                        if ((string.Equals(t.GroupName, "texteval", StringComparison.OrdinalIgnoreCase) || evaluatorLibsTemp.Contains(libLowerTemp)) && !stepAllowed.Contains("evaluator")) stepAllowed.Add("evaluator");
                        ChatCompletionAgent? stepAgent = null;
                        KernelWithPlugins? stepKernel = null;
                        try
                        {
                            stepKernel = _factory.CreateKernel(model, stepAllowed);
                            if (stepKernel != null) stepAgent = new ChatCompletionAgent("tester", stepKernel, agentInstructions, model ?? string.Empty);
                        }
                        catch (Exception ex)
                        {
                            // mark no tools if provider complains
                            try { if (ex?.GetType()?.Name == "ModelDoesNotSupportToolsException" || (ex?.Message?.IndexOf("does not support tools", StringComparison.OrdinalIgnoreCase) >= 0)) { modelInfo.NoTools = true; _database.UpsertModel(modelInfo); _progress?.Append(runId.ToString(), "Marked model as NoTools (provider reports no tool support)"); } } catch { }
                        }

                        var useAgent = stepAgent ?? defaultAgent;
                        // Reset evaluator skill state to avoid stale values
                        try { if (_factory?.StoryEvaluatorSkill != null) { _factory.StoryEvaluatorSkill.LastCalled = null; _factory.StoryEvaluatorSkill.LastResult = null; } } catch { }

                        var timeoutFc = t.TimeoutMs > 0 ? t.TimeoutMs : 30000;
                        // If test defines a JSON response format, instruct the agent to use it
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(t.JsonResponseFormat))
                            {
                                var schemaPath = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "response_formats", t.JsonResponseFormat);
                                if (System.IO.File.Exists(schemaPath))
                                {
                                    var schemaJson = System.IO.File.ReadAllText(schemaPath);
                                    try
                                    {
                                        // Prefer a direct method if available
                                        var mi = useAgent?.GetType().GetMethod("SetResponseFormatSchema", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                        if (mi != null)
                                        {
                                            mi.Invoke(useAgent, new object[] { schemaJson });
                                        }
                                        else
                                        {
                                            // Fallback: try reflection into internal KernelArguments
                                            var argsField = useAgent?.GetType().GetField("_args", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                                            var argsVal = argsField?.GetValue(useAgent);
                                            var execProp = argsVal?.GetType().GetProperty("ExecutionSettings");
                                            var execVal = execProp?.GetValue(argsVal);
                                            if (execVal != null)
                                            {
                                                var asm = execVal.GetType().Assembly;
                                                var rfType = asm.GetType("Microsoft.SemanticKernel.Prompt.ResponseFormat") ?? asm.GetType("Microsoft.SemanticKernel.ResponseFormat");
                                                if (rfType != null)
                                                {
                                                    var create = rfType.GetMethod("CreateJsonSchema", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                                                    var rf = create?.Invoke(null, new object[] { schemaJson });
                                                    var rfProp = execVal.GetType().GetProperty("ResponseFormat");
                                                    rfProp?.SetValue(execVal, rf);
                                                }
                                            }
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }

                        // Build prompt to send to agent. If a JSON response schema is defined, append
                        // an explicit instruction forcing the model to return only valid JSON matching
                        // the schema. We keep the original prompt unchanged and use a local variable.
                        var promptToUse = t.Prompt ?? string.Empty;
                        var inv = await InvokeAgentSafeAsync(useAgent, promptToUse, timeoutFc);
                        // If invocation completed but indicates lack of tool support, mark NoTools
                        try { if (!string.IsNullOrWhiteSpace(inv.error)) { if (inv.error.IndexOf("does not support tools", StringComparison.OrdinalIgnoreCase) >= 0) { modelInfo.NoTools = true; _database.UpsertModel(modelInfo); _progress?.Append(runId.ToString(), "Marked model as NoTools (provider reports no tool support)"); } } } catch { }
                        swStep.Stop();

                        if (testType == "writer")
                        {
                            await HandleWriterAsync(t, stepId, swStep, runId, idx, useAgent, inv, stepKernel, timeoutFc, agentInstructions, model ?? string.Empty);
                        }
                        else
                        {
                            await HandleFunctionCallAsync(t, stepId, swStep, runId, idx, inv);
                        }
                        break;
                    default:
                        // Unknown test type, treat as error
                        _database.UpdateTestStepResult(stepId, false, null, $"Unknown test type: {t.TestType}", null);
                        break;
                }
            }
            catch (Exception ex)
            {
                _database.UpdateTestStepResult(stepId, false, null, ex.Message, null);
                _progress?.Append(runId.ToString(), $"Step {idx} ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates if the agent text matches the valid score range criteria.
        /// Supports numeric ranges (min-max), single values, or comma-separated lists.
        /// </summary>
        private static bool ValidateRange(string? agentText, string validScoreRange, out string? failReason)
        {
            failReason = null;
            var range = validScoreRange.Trim();
            // Numeric range: min-max
            if (range.Contains("-") && !range.Contains(","))
            {
                var parts = range.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var min) && int.TryParse(parts[1], out var max))
                {
                    if (int.TryParse(agentText, out var val) && val >= min && val <= max)
                        return true;
                    else
                        failReason = $"Agent response '{agentText}' is not in numeric range {min}-{max}";
                }
                else if (parts.Length == 1 && int.TryParse(parts[0], out var single))
                {
                    if (int.TryParse(agentText, out var val) && val == single)
                        return true;
                    else
                        failReason = $"Agent response '{agentText}' is not equal to {single}";
                }
            }
            // List of values: A,B,C
            else if (range.Contains(","))
            {
                var values = range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var v in values)
                {
                    if (agentText != null && agentText.Equals(v, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                failReason = $"Agent response '{agentText}' is not in allowed list [{string.Join(", ", values)}]";
            }
            return false;
        }

        private async Task HandleWriterAsync(TestDefinition t, int stepId, System.Diagnostics.Stopwatch swStep, int runId, int idx, ChatCompletionAgent? useAgent, (bool completed, string? error, double elapsedSeconds, string? raw, object? resultObj) inv, KernelWithPlugins? stepKernel, int timeoutFc, string agentInstructions, string model)
        {
            try
            {
                // Build prompt from execution plan if provided
                var planText = string.Empty;
                try
                {
                    if (!string.IsNullOrWhiteSpace(t.ExecutionPlan))
                    {
                        var path = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "execution_plans", t.ExecutionPlan);
                        if (System.IO.File.Exists(path)) planText = System.IO.File.ReadAllText(path);
                    }
                }
                catch { }
                var writerPrompt = t.Prompt ?? string.Empty;

                // Append execution plan to agent instructions if present
                var originalInstructions = useAgent?.Instructions;
                try
                {
                    if (!string.IsNullOrWhiteSpace(planText) && useAgent != null)
                    {
                        var baseInstr = (!string.IsNullOrWhiteSpace(useAgent.Instructions) ? useAgent.Instructions : (agentInstructions ?? string.Empty));
                        useAgent.Instructions = baseInstr + "\n\n" + planText;
                    }
                    var writerInv = await InvokeAgentSafeAsync(useAgent, writerPrompt, timeoutFc);
                    // Debug: log basic writer response statuses (truncated)
                    try
                    {
                        var rawPreview = writerInv.raw ?? (writerInv.resultObj?.ToString());
                        if (!string.IsNullOrWhiteSpace(rawPreview) && rawPreview.Length > 1500) rawPreview = rawPreview.Substring(0, 1500) + "...";
                        _progress?.Append(runId.ToString(), $"WriterInv error: {writerInv.error ?? ""} - raw preview: {rawPreview}");
                        _progress?.Append(runId.ToString(), $"Writer skill LastResult: {stepKernel?.StoryWriterSkill?.LastResult}");
                    }
                    catch { }
                    swStep.Stop();
                    var storyText = FlattenResultToText(writerInv.raw) ?? FlattenResultToText(writerInv.resultObj) ?? string.Empty;
                    long savedId = 0;
                    try
                    {
                        var last = stepKernel?.StoryWriterSkill?.LastResult;
                        if (!string.IsNullOrWhiteSpace(last))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(last);
                                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                                {
                                    savedId = idEl.GetInt64();
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                    if (savedId == 0)
                    {
                        try
                        {
                            var resultObj = writerInv.resultObj;
                            if (resultObj != null)
                            {
                                var fcProp = resultObj.GetType().GetProperty("FunctionCalls");
                                if (fcProp != null)
                                {
                                    var fcVal = fcProp.GetValue(resultObj) as System.Collections.IEnumerable;
                                    if (fcVal != null)
                                    {
                                        var enumIt = fcVal.GetEnumerator();
                                        if (enumIt.MoveNext())
                                        {
                                            var call = enumIt.Current;
                                            if (call != null)
                                            {
                                                var callType = call.GetType();
                                                var getArgMI = callType.GetMethods().FirstOrDefault(m => m.Name == "GetArgument" && m.GetParameters().Length == 1);
                                                if (getArgMI != null && getArgMI.IsGenericMethodDefinition)
                                                {
                                                    try
                                                    {
                                                        var mStr = getArgMI.MakeGenericMethod(typeof(string));
                                                        var storyArg = mStr.Invoke(call, new object[] { "story" }) as string;
                                                        if (!string.IsNullOrWhiteSpace(storyArg))
                                                        {
                                                            var genRes2 = new StoryGeneratorService.GenerationResult { StoryC = storyArg ?? string.Empty, ModelC = model ?? string.Empty, ScoreC = 0.0, EvalC = string.Empty };
                                                            savedId = _stories.SaveGeneration(t.Prompt ?? string.Empty, genRes2, Guid.NewGuid().ToString());
                                                        }
                                                    }
                                                    catch { }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    // If we still didn't get an id from LastResult or FunctionCalls, try extracting the id from raw JSON
                    if (savedId == 0)
                    {
                        try
                        {
                            var parsedFromRaw = TryExtractIdFromJsonString(writerInv.raw);
                            if (parsedFromRaw.HasValue)
                            {
                                savedId = parsedFromRaw.Value;
                                _progress?.Append(runId.ToString(), $"Writer: extracted savedId={savedId} from raw JSON");
                            }
                        }
                        catch { }
                    }

                    if (!string.IsNullOrWhiteSpace(storyText))
                    {
                        var genRes = new StoryGeneratorService.GenerationResult { StoryC = storyText ?? string.Empty, ModelC = model ?? string.Empty, ScoreC = 0.0, EvalC = string.Empty };
                        try
                        {
                            if (savedId == 0)
                            {
                                savedId = _stories.SaveGeneration(t.Prompt ?? string.Empty, genRes, Guid.NewGuid().ToString());
                            }
                        }
                        catch { }
                        try { _database.AddTestAsset(stepId, "story", $"/stories/{savedId}", "Generated story", durationSec: swStep.Elapsed.TotalSeconds, sizeBytes: storyText?.Length ?? 0, storyId: savedId); } catch { }
                        try
                        {
                            var topModels = _database.ListModels().Where(m => m.Enabled).OrderByDescending(m => m.FunctionCallingScore).Take(3).Select(m => m.Name).ToList();
                            foreach (var evalModel in topModels)
                            {
                                try
                                {
                                            var evalKw = _factory.CreateKernel(evalModel ?? string.Empty, new[] { "evaluator" });
                                    if (evalKw == null || evalKw.Kernel == null) continue;
                                    var evalAgent = new ChatCompletionAgent("evaluator", evalKw.Kernel, "Valuta la storia e invoca la funzione evaluate_full_story con i parametri e includi story_id.", evalModel ?? string.Empty);
                                    var evalPrompt = $"Valuta questa storia e invoca 'evaluate_full_story' con i valori richiesti. Imposta story_id: {savedId}.\n\nStoria:\n{storyText}";
                                    var invEval = await InvokeAgentSafeAsync(evalAgent, evalPrompt, timeoutFc);
                                    try
                                    {
                                        var raw = invEval.raw ?? invEval.resultObj?.ToString() ?? string.Empty;
                                        _database.UpdateTestStepResult(stepId, true, raw, null, (long)swStep.Elapsed.TotalMilliseconds);
                                    }
                                    catch { }
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        _database.UpdateTestStepResult(stepId, false, "", "No story produced", (long)swStep.Elapsed.TotalMilliseconds);
                    }
                }
                catch (Exception ex)
                {
                    _database.UpdateTestStepResult(stepId, false, null, ex.Message, (long)swStep.Elapsed.TotalMilliseconds);
                }
                finally
                {
                    // restore original instructions
                    try { if (useAgent != null) useAgent.Instructions = originalInstructions; } catch { }
                }
                _progress?.Append(runId.ToString(), $"Step {idx} WRITER executed: {t.FunctionName ?? ("writer step" + t.Id)}");
            }
            catch { }
        }

        private async Task HandleQuestionAsync(TestDefinition t, int stepId, System.Diagnostics.Stopwatch swStep, int runId, int idx, ChatCompletionAgent? defaultAgent)
        {
            var timeout = t.TimeoutMs > 0 ? t.TimeoutMs : 30000;
            var invQ = await InvokeAgentSafeAsync(defaultAgent, t.Prompt ?? string.Empty, timeout);
            swStep.Stop();
            // Always extract clean text from raw or result object
            var agentTextQ = FlattenResultToText(invQ.raw) ?? FlattenResultToText(invQ.resultObj);
            bool passedQ = false;
            string? failReasonQ = null;
            // Verifica ExpectedPromptValue
            if (!string.IsNullOrWhiteSpace(t.ExpectedPromptValue))
            {
                if (agentTextQ != null && agentTextQ == t.ExpectedPromptValue)
                    passedQ = true;
                else
                    failReasonQ = "Agent response does not match ExpectedPromptValue";
            }
            // Verifica ValidScoreRange
            else if (!string.IsNullOrWhiteSpace(t.ValidScoreRange))
            {
                passedQ = ValidateRange(agentTextQ, t.ValidScoreRange, out failReasonQ);
            }
            else
            {
                // Se nessun criterio, passa solo se la risposta non è vuota
                passedQ = !string.IsNullOrWhiteSpace(agentTextQ);
                if (!passedQ) failReasonQ = "Agent response is empty";
            }
            var outJsonQ = System.Text.Json.JsonSerializer.Serialize(new { response = agentTextQ, raw = agentTextQ, expected = t.ExpectedPromptValue, range = t.ValidScoreRange });
            _database.UpdateTestStepResult(stepId, passedQ, outJsonQ, passedQ ? null : failReasonQ, (long?)(swStep.ElapsedMilliseconds));
            // Invio prompt e risposta come messaggio di progresso
            _progress?.Append(runId.ToString(), $"PROMPT: {t.Prompt}\nRESPONSE: {agentTextQ}");
            _progress?.Append(runId.ToString(), $"Step {idx} {(passedQ ? "PASSED" : "FAILED")}: {t.FunctionName ?? ("id_" + t.Id.ToString())} ({swStep.ElapsedMilliseconds}ms)");
        }

        private async Task HandleFunctionCallAsync(TestDefinition t, int stepId, System.Diagnostics.Stopwatch swStep, int runId, int idx, (bool completed, string? error, double elapsedSeconds, string? raw, object? resultObj) inv)
        {
            // Determine which plugin was called via LastCalled mapping (best-effort)
            var calledLocal = string.Empty;
            try
            {
                calledLocal = (t.Library ?? string.Empty).ToLowerInvariant() switch
                {
                    var s when s.Contains("tts") => _factory?.TtsApiSkill?.LastCalled,
                    var s when s.Contains("audiocraft") => _factory?.AudioCraftSkill?.LastCalled,
                    var s when s.Contains("text") => _factory?.TextPlugin?.LastCalled,
                    var s when s.Contains("math") => _factory?.MathPlugin?.LastCalled,
                    var s when s.Contains("time") => _factory?.TimePlugin?.LastCalled,
                    var s when s.Contains("filesystem") => _factory?.FileSystemPlugin?.LastCalled,
                    var s when s.Contains("http") => _factory?.HttpPlugin?.LastCalled,
                    var s when s.Contains("memory") => _factory?.MemorySkill?.LastCalled,
                    _ => _factory?.TextPlugin?.LastCalled
                } ?? string.Empty;
            }
            catch { }

            var libLowerLocal = (t.Library ?? string.Empty).ToLowerInvariant();
            var isTextevalLocal = string.Equals(t.GroupName, "texteval", StringComparison.OrdinalIgnoreCase);
            var evaluatorLibsLocal = new[] { "coerenza_narrativa", "struttura", "caratterizzazione_personaggi", "dialoghi", "ritmo", "originalita", "stile", "worldbuilding", "coerenza_tematica", "impatto_emotivo" };
            if (isTextevalLocal || evaluatorLibsLocal.Contains(libLowerLocal))
            {
                object? resultObj = inv.resultObj;
                bool parsedOk = false;
                int parsedScore = -1;
                string parsedDefects = string.Empty;
                try
                {
                    if (resultObj != null)
                    {
                        var fcProp = resultObj.GetType().GetProperty("FunctionCalls");
                        if (fcProp != null)
                        {
                            var fcVal = fcProp.GetValue(resultObj) as System.Collections.IEnumerable;
                            if (fcVal != null)
                            {
                                var enumIt = fcVal.GetEnumerator();
                                if (enumIt.MoveNext())
                                {
                                    var call = enumIt.Current;
                                    if (call != null)
                                    {
                                        try
                                        {
                                            var callType = call.GetType();
                                            var getArgMI = callType.GetMethods().FirstOrDefault(m => m.Name == "GetArgument" && m.GetParameters().Length == 1);
                                            if (getArgMI != null && getArgMI.IsGenericMethodDefinition)
                                            {
                                                try
                                                {
                                                    var mInt = getArgMI.MakeGenericMethod(typeof(int));
                                                    var val = mInt.Invoke(call, new object[] { "score" });
                                                    if (val is int vi) parsedScore = vi;
                                                    else if (val is long vl) parsedScore = (int)vl;
                                                }
                                                catch { }
                                                try
                                                {
                                                    var mStr = getArgMI.MakeGenericMethod(typeof(string));
                                                    var dv = mStr.Invoke(call, new object[] { "difetti" });
                                                    if (dv == null) dv = mStr.Invoke(call, new object[] { "defects" });
                                                    if (dv == null) dv = mStr.Invoke(call, new object[] { "defect" });
                                                    if (dv is string ds) parsedDefects = ds ?? string.Empty;
                                                }
                                                catch { }
                                                parsedOk = parsedScore >= 0 || !string.IsNullOrWhiteSpace(parsedDefects);
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                if (!parsedOk)
                {
                    var cleanedMissing = FlattenResultToText(inv.raw) ?? FlattenResultToText(inv.resultObj);
                    var outMissing = cleanedMissing != null ? System.Text.Json.JsonSerializer.Serialize(new { response = cleanedMissing, raw = cleanedMissing }) : (inv.raw != null ? System.Text.Json.JsonSerializer.Serialize(new { raw = inv.raw }) : null);
                    _database.UpdateTestStepResult(stepId, false, outMissing, "Model did not invoke evaluator function (required: function-calling with FunctionCalls)", (long?)(swStep.Elapsed.TotalMilliseconds));
                    _progress?.Append(runId.ToString(), $"Step {idx} FAILED: evaluator function not invoked ({t.FunctionName ?? ("id_" + t.Id.ToString())})");
                    return;
                }

                bool passedEval = false;
                if (!string.IsNullOrWhiteSpace(t.ExpectedBehavior) && !string.IsNullOrWhiteSpace(parsedDefects))
                {
                    if (parsedDefects.IndexOf(t.ExpectedBehavior, StringComparison.OrdinalIgnoreCase) >= 0) passedEval = true;
                }
                if (!passedEval && parsedOk && !string.IsNullOrWhiteSpace(t.ValidScoreRange))
                {
                    try
                    {
                        var range = t.ValidScoreRange.Trim();
                        // Range numerico: min-max
                        if (range.Contains("-") && !range.Contains(","))
                        {
                            var parts = range.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            if (parts.Length == 2 && int.TryParse(parts[0], out var min) && int.TryParse(parts[1], out var max))
                            {
                                if (parsedScore >= min && parsedScore <= max) passedEval = true;
                            }
                            else if (parts.Length == 1 && int.TryParse(parts[0], out var single))
                            {
                                if (parsedScore == single) passedEval = true;
                            }
                        }
                        // Lista di valori: A,B,C
                        else if (range.Contains(","))
                        {
                            var values = range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            var scoreStr = parsedScore.ToString();
                            foreach (var v in values)
                            {
                                if (scoreStr.Equals(v, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrWhiteSpace(parsedDefects) && parsedDefects.Equals(v, StringComparison.OrdinalIgnoreCase)))
                                {
                                    passedEval = true;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                var cleaned = FlattenResultToText(inv.raw) ?? FlattenResultToText(inv.resultObj);
                var rawForPersist = cleaned ?? (resultObj != null ? System.Text.Json.JsonSerializer.Serialize(resultObj) : inv.raw);
                var outJson3 = System.Text.Json.JsonSerializer.Serialize(new { parsed = new { score = parsedScore, defects = parsedDefects }, response = cleaned, raw = rawForPersist });
                _database.UpdateTestStepResult(stepId, passedEval, outJson3, passedEval ? null : inv.error, (long?)(swStep.Elapsed.TotalMilliseconds));
                _progress?.Append(runId.ToString(), $"Evaluated texteval step (score={parsedScore})");
                return;
            }

            // Non-evaluator steps: LastCalled checks
            if (!string.IsNullOrWhiteSpace(t.ExpectedBehavior) && t.ExpectedBehavior.Contains("LastCalled") && string.IsNullOrWhiteSpace(calledLocal))
            {
                var err = "Model responded without invoking the expected addin/function" + (inv.error != null ? $": {inv.error}" : string.Empty);
                var cleanedChat = FlattenResultToText(inv.raw) ?? FlattenResultToText(inv.resultObj);
                var outJson = cleanedChat != null ? System.Text.Json.JsonSerializer.Serialize(new { response = cleanedChat, raw = inv.raw, chat = cleanedChat }) : (inv.raw != null ? System.Text.Json.JsonSerializer.Serialize(new { raw = inv.raw }) : null);
                _database.UpdateTestStepResult(stepId, false, outJson, err, (long?)(swStep.Elapsed.TotalMilliseconds));
                _progress?.Append(runId.ToString(), $"Step {idx} FAILED (no addin invocation): {t.FunctionName ?? ("id_" + t.Id.ToString())} ({swStep.ElapsedMilliseconds}ms)");
                return;
            }

            var passed = false;
            if (!string.IsNullOrWhiteSpace(t.ExpectedBehavior) && t.ExpectedBehavior.Contains("LastCalled") && !string.IsNullOrWhiteSpace(calledLocal))
            {
                var expected = t.ExpectedBehavior.Split('"').ElementAtOrDefault(1) ?? string.Empty;
                passed = string.Equals(expected, calledLocal, StringComparison.OrdinalIgnoreCase);
            }

            var cleanedChat2 = FlattenResultToText(inv.raw) ?? FlattenResultToText(inv.resultObj);
            var outJson2 = cleanedChat2 != null ? System.Text.Json.JsonSerializer.Serialize(new { response = cleanedChat2, raw = inv.raw, chat = cleanedChat2 }) : (inv.raw != null ? System.Text.Json.JsonSerializer.Serialize(new { raw = inv.raw }) : null);
            _database.UpdateTestStepResult(stepId, passed, outJson2, passed ? null : inv.error, (long?)(swStep.Elapsed.TotalMilliseconds));
            _progress?.Append(runId.ToString(), $"Step {idx} {(passed ? "PASSED" : "FAILED")}: {t.FunctionName ?? ("id_" + t.Id.ToString())} ({swStep.ElapsedMilliseconds}ms)");
        }
    }
}
