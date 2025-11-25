using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Custom LangChain-inspired TestService (replaces deprecated SK TestService)
    /// 
    /// NOTE: This is a custom implementation inspired by LangChain architecture,
    /// NOT using the official LangChain C# SDK (which is still in beta/dev).
    /// 
    /// Features:
    /// - Manual ReAct loop implementation with full control
    /// - OpenAI-compatible tool schema (works with Ollama, OpenAI, etc.)
    /// - Robust tool call parsing (handles JSON objects as arguments)
    /// - Comprehensive error handling, retry logic, and timeout management
    /// - Real-time progress tracking via SignalR
    /// - Database-backed logging and test result persistence
    /// - Selective tool loading per test (0-N tools)
    /// - Ollama structured output support (format parameter)
    /// 
    /// This custom implementation provides more control and stability than
    /// the current LangChain C# SDK (0.17.x-dev) for our specific use cases.
    /// </summary>
    public class LangChainTestService
    {
        private readonly DatabaseService _database;
        private readonly ProgressService _progress;
        private readonly StoriesService _stories;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly LangChainAgentService _agentService;
        private readonly ICustomLogger _customLogger;
        private readonly IOllamaManagementService _ollamaService;

        public LangChainTestService(
            DatabaseService database,
            ProgressService progress,
            StoriesService stories,
            ILangChainKernelFactory kernelFactory,
            LangChainAgentService agentService,
            ICustomLogger customLogger,
            IOllamaManagementService ollamaService)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
            _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
            _ollamaService = ollamaService ?? throw new ArgumentNullException(nameof(ollamaService));
        }

        /// <summary>
        /// Run a group of tests for a specific model
        /// </summary>
        public async Task<object?> RunGroupAsync(string model, string group)
        {
            var modelInfo = _database.GetModelInfo(model);
            if (modelInfo == null) return null;

            var tests = _database.GetPromptsByGroup(group) ?? new List<TestDefinition>();
            
            // Setup test folder if needed
            string? testFolder = SetupTestFolder(model, group, tests);
            
            var runId = _database.CreateTestRun(
                modelInfo.Name,
                group,
                $"Group run {group} (LangChain)",
                false,
                null,
                "Started from LangChainTestService.RunGroupAsync",
                testFolder);

            _progress?.Start(runId.ToString());
            LogTestMessage(runId.ToString(), $"[{model}] Starting test group: {group}");

            // Warmup for Ollama models - just load the model into memory
            if (modelInfo.Provider?.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    LogTestMessage(runId.ToString(), $"[{model}] Loading model into memory...");
                    var warmupSuccess = await _ollamaService.WarmupModelAsync(model, timeoutSeconds: 60);
                    if (warmupSuccess)
                    {
                        LogTestMessage(runId.ToString(), $"[{model}] Model loaded successfully");
                    }
                    else
                    {
                        LogTestMessage(runId.ToString(), $"[{model}] Model warmup inconclusive (continuing anyway)", "Warning");
                    }
                }
                catch (Exception ex)
                {
                    LogTestMessage(runId.ToString(), $"[{model}] Warmup error (continuing): {ex.Message}", "Warning");
                }
            }

            var testStartUtc = DateTime.UtcNow;

            int idx = 0;
            foreach (var test in tests.OrderBy(t => t.Priority).ToList())
            {
                idx++;
                await ExecuteTestAsync(runId, idx, test, model, modelInfo, string.Empty, null, testFolder);
            }

            var testEndUtc = DateTime.UtcNow;
            var durationMs = (long?)(testEndUtc - testStartUtc).TotalMilliseconds;
            var counts = _database.GetRunStepCounts(runId);
            var passedCount = counts.passed;
            var steps = counts.total;
            var score = steps > 0 ? (int)Math.Round((double)passedCount / steps * 10) : 0;
            var passedFlag = steps > 0 && passedCount == steps;

            _database.UpdateTestRunResult(runId, passedFlag, durationMs);
            _database.UpdateModelTestResults(
                modelInfo.Name,
                score,
                new Dictionary<string, bool?>(),
                durationMs.HasValue ? (double?)(durationMs.Value / 1000.0) : null);

            _database.RecalculateModelScore(modelInfo.Name);

            if (passedFlag)
            {
                LogTestMessage(runId.ToString(), $"✅ [{model}] Test completato: {passedCount}/{steps} passati, score {score}/10, durata {durationMs/1000.0:0.##}s");
            }
            else
            {
                LogTestMessage(runId.ToString(), $"❌ [{model}] Test fallito: {passedCount}/{steps} passati, score {score}/10", "Error");
            }

            return new { runId, score, steps, passed = passedCount, duration = durationMs };
        }

        /// <summary>
        /// Run tests for all enabled models in a group
        /// </summary>
        public async Task<List<object>> RunAllEnabledModelsAsync(string? group)
        {
            var groups = _database.GetTestGroups() ?? new List<string>();
            var selectedGroup = !string.IsNullOrWhiteSpace(group) ? group : groups.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(selectedGroup))
                return new List<object>();

            var models = _database.ListModels().Where(m => m.Enabled).ToList();
            var runResults = new List<object>();

            foreach (var modelInfo in models)
            {
                try
                {
                    var result = await RunGroupAsync(modelInfo.Name, selectedGroup);
                    if (result != null)
                        runResults.Add(result);
                }
                catch (Exception ex)
                {
                    LogTestMessage("all_models", $"Error running {modelInfo.Name}: {ex.Message}", "Error");
                }
            }

            return runResults;
        }

        /// <summary>
        /// Execute a single test
        /// </summary>
        public async Task ExecuteTestAsync(
            int runId,
            int idx,
            TestDefinition test,
            string model,
            ModelInfo modelInfo,
            string agentInstructions,
            object? defaultAgent = null,
            string? testFolder = null)
        {
            var prompt = test.Prompt ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(testFolder) && prompt.Contains("[test_folder]"))
            {
                prompt = prompt.Replace("[test_folder]", testFolder);
            }
            
            var planInfo = string.IsNullOrWhiteSpace(test.ExecutionPlan) 
                ? "(no plan)" 
                : $"plan={Path.GetFileName(test.ExecutionPlan)}";
            
            var stepId = _database.AddTestStep(
                runId,
                idx,
                test.FunctionName ?? $"test_{test.Id}",
                JsonSerializer.Serialize(new { prompt, plan = planInfo }));
            
            LogTestMessage(runId.ToString(), $"[{model}] Step {idx}: {planInfo}", "Information");
            if (!string.IsNullOrWhiteSpace(test.ExecutionPlan))
            {
                LogTestMessage(runId.ToString(), $"[{model}] Execution Plan: {test.ExecutionPlan}", "Information");
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var testType = (test.TestType ?? string.Empty).ToLowerInvariant();

                switch (testType)
                {
                    case "question":
                        await ExecuteQuestionTestAsync(test, stepId, sw, runId, idx, model, prompt);
                        break;

                    case "functioncall":
                        await ExecuteFunctionCallTestAsync(test, stepId, sw, runId, idx, model, modelInfo, prompt);
                        break;

                    case "writer":
                        await ExecuteWriterTestAsync(test, stepId, sw, runId, idx, model, modelInfo, prompt);
                        break;

                    case "tts":
                        await ExecuteTtsTestAsync(test, stepId, sw, runId, idx, model, modelInfo, prompt, testFolder);
                        break;

                    default:
                        _database.UpdateTestStepResult(
                            stepId,
                            false,
                            null,
                            $"Unknown test type: {test.TestType}",
                            null);
                        break;
                }
            }
            catch (Exception ex)
            {
                _database.UpdateTestStepResult(stepId, false, null, ex.Message, null);
                LogTestMessage(runId.ToString(), $"[{model}] Step {idx} ERROR: {ex.Message}", "Error");
            }
        }

        // ==================== Test Type Handlers ====================

        private async Task ExecuteQuestionTestAsync(
            TestDefinition test,
            int stepId,
            System.Diagnostics.Stopwatch sw,
            int runId,
            int idx,
            string model,
            string prompt)
        {
            var orchestrator = _kernelFactory.CreateOrchestrator(model, ParseAllowedPlugins(test), null);
            var chatBridge = _kernelFactory.CreateChatBridge(model);
            // Apply per-test sampling parameters if provided
            if (test.Temperature.HasValue) chatBridge.Temperature = test.Temperature.Value;
            if (test.TopP.HasValue) chatBridge.TopP = test.TopP.Value;
            var timeout = test.TimeoutSecs > 0 ? Math.Max(1, test.TimeoutSecs) : 30;

            var instructions = LoadExecutionPlan(test.ExecutionPlan);
            var fullPrompt = !string.IsNullOrWhiteSpace(instructions)
                ? $"{instructions}\n\n{prompt}"
                : prompt;
            var hasTools = orchestrator.GetToolSchemas().Any();
            fullPrompt = AppendResponseFormatToPrompt(fullPrompt, test.JsonResponseFormat, hasTools);

            try
            {
                var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                
                // Create ReAct loop to execute the question test
                var reactLoop = new ReActLoopOrchestrator(orchestrator, _customLogger, maxIterations: 5, progress: _progress, runId: runId.ToString(), modelBridge: chatBridge);
                var reactResult = await reactLoop.ExecuteAsync(fullPrompt, cts.Token);
                sw.Stop();
                
                var response = reactResult.FinalResponse;
                var responseText = response ?? string.Empty;
                bool passed = ValidateResponse(responseText, test, out var failReason);

                var resultJson = JsonSerializer.Serialize(new
                {
                    response = responseText,
                    expected = test.ExpectedPromptValue,
                    range = test.ValidScoreRange
                });

                _database.UpdateTestStepResult(
                    stepId,
                    passed,
                    resultJson,
                    passed ? null : failReason,
                    (long?)sw.ElapsedMilliseconds);

                _progress?.Append(runId.ToString(),
                    $"[{model}] Step {idx} {(passed ? "PASSED" : "FAILED")}: {test.FunctionName ?? $"id_{test.Id}"} ({sw.ElapsedMilliseconds}ms)");
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    $"Timeout after {timeout}s",
                    (long?)sw.ElapsedMilliseconds);
                _progress?.Append(runId.ToString(), $"[{model}] Step {idx} TIMEOUT");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    $"Error: {ex.Message}",
                    (long?)sw.ElapsedMilliseconds);
                _progress?.Append(runId.ToString(), $"[{model}] Step {idx} ERROR: {ex.Message}");
            }
        }

        private async Task ExecuteFunctionCallTestAsync(
            TestDefinition test,
            int stepId,
            System.Diagnostics.Stopwatch sw,
            int runId,
            int idx,
            string model,
            ModelInfo modelInfo,
            string prompt)
        {
            var orchestrator = _kernelFactory.CreateOrchestrator(model, ParseAllowedPlugins(test), null);
            var chatBridge = _kernelFactory.CreateChatBridge(model);
            if (test.Temperature.HasValue) chatBridge.Temperature = test.Temperature.Value;
            if (test.TopP.HasValue) chatBridge.TopP = test.TopP.Value;
            var timeout = test.TimeoutSecs > 0 ? Math.Max(1, test.TimeoutSecs) : 30;

            var instructions = LoadExecutionPlan(test.ExecutionPlan);
            var fullPrompt = !string.IsNullOrWhiteSpace(instructions)
                ? $"{instructions}\n\n{prompt}"
                : prompt;
            var hasTools = orchestrator.GetToolSchemas().Any();
            fullPrompt = AppendResponseFormatToPrompt(fullPrompt, test.JsonResponseFormat, hasTools);

            try
            {
                var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                
                // Create ReAct loop to execute function calls
                var reactLoop = new ReActLoopOrchestrator(orchestrator, _customLogger, maxIterations: 5, progress: _progress, runId: runId.ToString(), modelBridge: chatBridge);
                var reactResult = await reactLoop.ExecuteAsync(fullPrompt, cts.Token);
                sw.Stop();

                var responseText = reactResult.FinalResponse ?? string.Empty;
                bool passed = ValidateFunctionCallResponse(responseText, test, out var failReason);

                var resultJson = JsonSerializer.Serialize(new
                {
                    response = responseText,
                    functionCalled = test.ExpectedBehavior,
                    toolsExecuted = reactResult.ExecutedTools.Select(t => new { t.ToolName, t.Input, t.Output }),
                    iterations = reactResult.IterationCount,
                    success = reactResult.Success
                });

                _database.UpdateTestStepResult(
                    stepId,
                    passed,
                    resultJson,
                    passed ? null : (failReason ?? reactResult.Error),
                    (long?)sw.ElapsedMilliseconds);

                _progress?.Append(runId.ToString(),
                    $"[{model}] Step {idx} {(passed ? "PASSED" : "FAILED")}: {test.FunctionName ?? $"id_{test.Id}"} ({sw.ElapsedMilliseconds}ms) - {reactResult.ExecutedTools.Count} tools called");
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    $"Timeout after {timeout}s",
                    (long?)sw.ElapsedMilliseconds);
            }
            catch (ModelNoToolsSupportException ex)
            {
                sw.Stop();
                // Mark model as not supporting tools
                modelInfo.NoTools = true;
                _database.UpsertModel(modelInfo);
                _progress?.Append(runId.ToString(), $"[{model}] Model does not support tools - marked NoTools=true");
                
                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    $"Model does not support tool calling: {ex.Message}",
                    (long?)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Fallback: check message content for "does not support tools"
                if (ex.Message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
                {
                    modelInfo.NoTools = true;
                    _database.UpsertModel(modelInfo);
                    _progress?.Append(runId.ToString(), $"[{model}] Model does not support tools - marked NoTools=true");
                }

                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    ex.Message,
                    (long?)sw.ElapsedMilliseconds);
            }
        }

        private async Task ExecuteWriterTestAsync(
            TestDefinition test,
            int stepId,
            System.Diagnostics.Stopwatch sw,
            int runId,
            int idx,
            string model,
            ModelInfo modelInfo,
            string prompt)
        {
            var orchestrator = _kernelFactory.CreateOrchestrator(model, ParseAllowedPlugins(test), null);
            var chatBridge = _kernelFactory.CreateChatBridge(model);
            if (test.Temperature.HasValue) chatBridge.Temperature = test.Temperature.Value;
            if (test.TopP.HasValue) chatBridge.TopP = test.TopP.Value;
            var timeout = test.TimeoutSecs > 0 ? Math.Max(1, test.TimeoutSecs) : 120;

            var instructions = LoadExecutionPlan(test.ExecutionPlan) ?? 
@"You are a professional storyteller. Write detailed, engaging stories of at least 2000 words IN ITALIAN. 
Include rich descriptions, well-developed characters, multiple scenes, and a complete narrative arc. 
DO NOT rush or summarize. Take your time to develop the story fully.
IMPORTANT: Write the story in Italian language.";

            var fullPrompt = $"{instructions}\n\n{prompt}";
            var hasTools = orchestrator.GetToolSchemas().Any();
            fullPrompt = AppendResponseFormatToPrompt(fullPrompt, test.JsonResponseFormat, hasTools);

            try
            {
                var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                
                // Create ReAct loop to execute story generation
                var reactLoop = new ReActLoopOrchestrator(orchestrator, _customLogger, maxIterations: 10, progress: _progress, runId: runId.ToString(), modelBridge: chatBridge);
                var reactResult = await reactLoop.ExecuteAsync(fullPrompt, cts.Token);
                sw.Stop();

                var storyText = reactResult.FinalResponse ?? string.Empty;

                if (string.IsNullOrWhiteSpace(storyText))
                {
                    _database.UpdateTestStepResult(
                        stepId,
                        false,
                        string.Empty,
                        "No story text returned",
                        (long?)sw.ElapsedMilliseconds);
                    return;
                }

                var cleanStoryText = ExtractJsonContent(storyText) ?? storyText;

                var storyId = _stories.InsertSingleStory(
                    test.Prompt ?? string.Empty,
                    cleanStoryText,
                    null,
                    null,
                    0.0,
                    null,
                    0,
                    "generated",
                    null);

                _database.AddTestAsset(
                    stepId,
                    "story",
                    $"/stories/{storyId}",
                    "Generated story",
                    durationSec: sw.Elapsed.TotalSeconds,
                    sizeBytes: cleanStoryText.Length,
                    storyId: storyId);

                var evaluationScore = await EvaluateStoryWithAgentsAsync(storyId, cleanStoryText, runId, model);
                var passed = evaluationScore >= 4.0;
                
                _database.UpdateTestStepResult(
                    stepId,
                    passed,
                    JsonSerializer.Serialize(new { storyId, length = cleanStoryText.Length, evaluationScore }),
                    passed ? null : $"Evaluation score too low: {evaluationScore:F1}/10",
                    (long?)sw.ElapsedMilliseconds);

                _progress?.Append(runId.ToString(),
                    $"[{model}] Step {idx} {(passed ? "PASSED" : "FAILED")}: story ID {storyId}, score {evaluationScore:F1}/10");
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    $"Timeout after {timeout}s",
                    (long?)sw.ElapsedMilliseconds);
            }
            catch (ModelNoToolsSupportException ex)
            {
                sw.Stop();
                // Mark model as not supporting tools
                modelInfo.NoTools = true;
                _database.UpsertModel(modelInfo);
                _progress?.Append(runId.ToString(), $"[{model}] Model does not support tools - marked NoTools=true");
                
                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    $"Model does not support tool calling: {ex.Message}",
                    (long?)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    ex.Message,
                    (long?)sw.ElapsedMilliseconds);
            }
        }

        private async Task ExecuteTtsTestAsync(
            TestDefinition test,
            int stepId,
            System.Diagnostics.Stopwatch sw,
            int runId,
            int idx,
            string model,
            ModelInfo modelInfo,
            string prompt,
            string? testFolder)
        {
            _progress?.Append(runId.ToString(), $"[{model}] === TTS TEST ===");
            
            var ttsStoryText = prompt;
            if (!string.IsNullOrWhiteSpace(ttsStoryText))
            {
                _progress?.Append(runId.ToString(), $"[{model}] Provided TTS prompt ({ttsStoryText.Length} chars)");
            }

            const int maxRetries = 3;
            var chatBridge = _kernelFactory.CreateChatBridge(model);
            if (test.Temperature.HasValue) chatBridge.Temperature = test.Temperature.Value;
            if (test.TopP.HasValue) chatBridge.TopP = test.TopP.Value;
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                var orchestrator = _kernelFactory.CreateOrchestrator(model, ParseAllowedPlugins(test), null, testFolder, ttsStoryText);
                var timeout = test.TimeoutSecs > 0 ? Math.Max(1, test.TimeoutSecs) : 60;

                var executionPlan = LoadExecutionPlan(test.ExecutionPlan);
                var fullPrompt = prompt;

                var hasTools = orchestrator.GetToolSchemas().Any();
                fullPrompt = AppendResponseFormatToPrompt(fullPrompt, test.JsonResponseFormat, hasTools);

                if (attempt > 1)
                {
                    fullPrompt += $"\n\n[RETRY {attempt}/{maxRetries}] The schema file was not generated. Call ConfirmSchema() to save it.";
                }

                try
                {
                    var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                    
                    // Create ReAct loop to execute TTS schema generation
                    // Pass execution plan as system message instead of concatenating to user prompt
                    var reactLoop = new ReActLoopOrchestrator(orchestrator, _customLogger, maxIterations: 5, progress: _progress, runId: runId.ToString(), modelBridge: chatBridge, systemMessage: executionPlan);
                    var reactResult = await reactLoop.ExecuteAsync(fullPrompt, cts.Token);
                    sw.Stop();
                    
                    var response = reactResult.FinalResponse;
                    var responsePreview = string.IsNullOrEmpty(response) ? "empty" : response;
                    _progress?.Append(runId.ToString(), $"[{model}] Model Response (attempt {attempt}): {responsePreview}");

                    string? generatedFile = null;
                    if (!string.IsNullOrWhiteSpace(testFolder))
                    {
                        var jsonFiles = Directory.GetFiles(testFolder, "*.json")
                            .Where(f => !f.EndsWith("_expected_result.json"))
                            .OrderByDescending(f => File.GetCreationTimeUtc(f))
                            .ToList();
                        
                        generatedFile = jsonFiles.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(generatedFile))
                        {
                            _progress?.Append(runId.ToString(), $"[{model}] Found generated file: {Path.GetFileName(generatedFile)}");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(generatedFile))
                    {
                        return; // Success
                    }
                    else if (attempt < maxRetries)
                    {
                        _progress?.Append(runId.ToString(), $"[{model}] No file generated, retrying... ({attempt}/{maxRetries})");
                        continue;
                    }
                    else
                    {
                        _database.UpdateTestStepResult(stepId, false, response, "No JSON file generated after retries", (int)sw.ElapsedMilliseconds);
                        _progress?.Append(runId.ToString(), $"[{model}] Step {idx} FAILED: No file generated");
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    if (attempt < maxRetries)
                    {
                        _progress?.Append(runId.ToString(), $"[{model}] Timeout on attempt {attempt}, retrying...");
                        continue;
                    }
                    else
                    {
                        _database.UpdateTestStepResult(stepId, false, null, $"Timeout after {timeout}s on all {maxRetries} attempts", (int)sw.ElapsedMilliseconds);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    if (attempt < maxRetries)
                    {
                        _progress?.Append(runId.ToString(), $"[{model}] Error on attempt {attempt}: {ex.Message}, retrying...");
                        continue;
                    }
                    else
                    {
                        _database.UpdateTestStepResult(stepId, false, null, $"{ex.Message} after {maxRetries} attempts", (int)sw.ElapsedMilliseconds);
                        return;
                    }
                }
            }
        }

        // ==================== Helper Methods ====================

        private string? SetupTestFolder(string model, string group, List<TestDefinition> tests)
        {
            var testsWithFiles = tests.Where(t => !string.IsNullOrWhiteSpace(t.FilesToCopy)).ToList();
            if (!testsWithFiles.Any())
                return null;

            var now = DateTime.Now;
            var timestamp = now.ToString("yyyyMMdd_HHmmssfff");
            var folderName = $"{timestamp}_{model}_{group}";
            var testFolder = Path.Combine(Directory.GetCurrentDirectory(), "test_run_folders", folderName);
            
            try
            {
                Directory.CreateDirectory(testFolder);
                
                foreach (var test in testsWithFiles)
                {
                    var filesToCopy = test.FilesToCopy!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => f.Trim())
                        .ToList();
                    
                    foreach (var fileName in filesToCopy)
                    {
                        var sourceFile = Path.Combine(Directory.GetCurrentDirectory(), "test_source_files", fileName);
                        var destFile = Path.Combine(testFolder, fileName);
                        
                        if (File.Exists(sourceFile))
                        {
                            File.Copy(sourceFile, destFile, overwrite: true);
                        }
                    }
                }
                
                return testFolder;
            }
            catch (Exception ex)
            {
                LogTestMessage("setup", $"Failed to setup test folder: {ex.Message}", "Error");
                return null;
            }
        }

        private IEnumerable<string>? ParseAllowedPlugins(TestDefinition test)
        {
            if (string.IsNullOrWhiteSpace(test.AllowedPlugins))
                return null;

            return test.AllowedPlugins
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        private string? LoadExecutionPlan(string? planName)
        {
            if (string.IsNullOrWhiteSpace(planName))
                return null;

            try
            {
                var planPath = Path.Combine(Directory.GetCurrentDirectory(), "execution_plans", planName);
                if (File.Exists(planPath))
                    return File.ReadAllText(planPath);
            }
            catch { }

            return null;
        }

        private string? LoadResponseFormat(string? formatName)
        {
            if (string.IsNullOrWhiteSpace(formatName))
                return null;

            try
            {
                var formatPath = Path.Combine(Directory.GetCurrentDirectory(), "response_formats", formatName);
                if (File.Exists(formatPath))
                    return File.ReadAllText(formatPath);
            }
            catch { }

            return null;
        }

        private string AppendResponseFormatToPrompt(string prompt, string? responseFormat, bool hasTools = false)
        {
            if (string.IsNullOrWhiteSpace(responseFormat))
                return prompt;

            // If we have tools or format will be handled by model (Ollama format parameter), 
            // don't add format instruction to prompt
            if (hasTools)
                return prompt;

            var schemaContent = LoadResponseFormat(responseFormat);
            if (string.IsNullOrWhiteSpace(schemaContent))
                return prompt;

            return $"{prompt}\n\nRISPONDI SEMPRE IN QUESTO FORMATO JSON:\n{schemaContent}";
        }

        private string? ExtractJsonContent(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.Trim().StartsWith("{"))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(text);
                // Try "result" first (tool output format)
                if (doc.RootElement.TryGetProperty("result", out var resultProp))
                {
                    if (resultProp.ValueKind == JsonValueKind.String)
                        return resultProp.GetString();
                }
                // Try "story" for story generation
                if (doc.RootElement.TryGetProperty("story", out var storyProp))
                {
                    if (storyProp.ValueKind == JsonValueKind.String)
                        return storyProp.GetString();
                }
            }
            catch { }

            return null;
        }

        private bool ValidateResponse(string? responseText, TestDefinition test, out string? failReason)
        {
            failReason = null;
            var response = responseText?.Trim() ?? string.Empty;

            // Try to extract "result" field from JSON if present
            var extractedValue = ExtractResultFromJson(response) ?? response;

            if (!string.IsNullOrWhiteSpace(test.ExpectedPromptValue))
            {
                var expected = test.ExpectedPromptValue.Trim();
                if (extractedValue.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;

                failReason = $"Expected '{expected}' but got '{extractedValue}'";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(test.ValidScoreRange))
                return ValidateRange(extractedValue, test.ValidScoreRange, out failReason);

            if (string.IsNullOrWhiteSpace(extractedValue))
            {
                failReason = "Response is empty";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Extract the value from {"result": "value"} or {"score": N} JSON format, or return original text
        /// </summary>
        private string? ExtractResultFromJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.Trim().StartsWith("{"))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(text);
                
                // Try "result" first (tool output format)
                if (doc.RootElement.TryGetProperty("result", out var resultProp))
                {
                    return ExtractJsonValue(resultProp);
                }
                
                // Try "score" for evaluation responses
                if (doc.RootElement.TryGetProperty("score", out var scoreProp))
                {
                    return ExtractJsonValue(scoreProp);
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Extract value from JsonElement handling different types
        /// </summary>
        private string ExtractJsonValue(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number => element.GetInt32().ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.GetRawText()
            };
        }

        private bool ValidateFunctionCallResponse(string? responseText, TestDefinition test, out string? failReason)
        {
            failReason = null;

            if (!string.IsNullOrWhiteSpace(test.JsonResponseFormat))
            {
                try
                {
                    using var doc = JsonDocument.Parse(responseText ?? "{}");
                    return true;
                }
                catch (JsonException ex)
                {
                    failReason = $"Invalid JSON: {ex.Message}";
                    return false;
                }
            }

            return !string.IsNullOrWhiteSpace(responseText);
        }

        private bool ValidateRange(string? value, string validScoreRange, out string? failReason)
        {
            failReason = null;
            var range = validScoreRange.Trim();

            if (range.Contains("-") && !range.Contains(","))
            {
                var parts = range.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var min) &&
                    int.TryParse(parts[1], out var max))
                {
                    if (int.TryParse(value, out var val) && val >= min && val <= max)
                        return true;

                    failReason = $"Value '{value}' not in range {min}-{max}";
                    return false;
                }
            }

            if (range.Contains(","))
            {
                var values = range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (values.Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase)))
                    return true;

                failReason = $"Value '{value}' not in allowed list";
                return false;
            }

            if (value != null && value.Equals(range, StringComparison.OrdinalIgnoreCase))
                return true;

            failReason = $"Value mismatch: expected '{range}' got '{value}'";
            return false;
        }

        private async Task<double> EvaluateStoryWithAgentsAsync(long storyId, string storyText, int runId, string writerModel)
        {
            var allAgents = _database.ListAgents();
            var evaluators = allAgents.Where(a => 
                a.Role?.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) == true && 
                a.IsActive).ToList();

            if (evaluators.Count == 0)
            {
                _progress?.Append(runId.ToString(), $"[{writerModel}] No evaluators found, using default score");
                return 10.0;
            }

            var totalScoreSum = 0;
            var evaluatorCount = 0;

            foreach (var evaluator in evaluators)
            {
                try
                {
                    _progress?.Append(runId.ToString(), $"[{writerModel}] Evaluating with {evaluator.Name}...");
                    var result = await _stories.EvaluateStoryWithAgentAsync(storyId, evaluator.Id);

                    if (result.success)
                    {
                        totalScoreSum += (int)result.score;
                        evaluatorCount++;
                        _progress?.Append(runId.ToString(), $"[{writerModel}] Evaluator score: {result.score}/100");
                    }
                }
                catch (Exception ex)
                {
                    _progress?.Append(runId.ToString(), $"[{writerModel}] Evaluator error: {ex.Message}", "Warning");
                }
            }

            if (evaluatorCount == 0)
                return 10.0;

            var maxPossible = evaluatorCount * 100;
            var finalScore = (double)totalScoreSum / maxPossible * 10.0;

            _progress?.Append(runId.ToString(), $"[{writerModel}] Final score: {finalScore:F2}/10");

            return finalScore;
        }

        private void LogTestMessage(string runId, string message, string level = "Information")
        {
            _progress?.Append(runId, message);
            
            try
            {
                _customLogger?.Log(level, "LangChainTestService", message);
            }
            catch { }
        }
    }
}
