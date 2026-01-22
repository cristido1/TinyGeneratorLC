using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Skills;

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
        private readonly StoriesService _stories;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly LangChainAgentService _agentService;
        private readonly ICustomLogger _customLogger;
        private readonly IOllamaManagementService _ollamaService;
        private readonly ICommandDispatcher _commandDispatcher;
        private readonly Dictionary<int, int> _ttsEmotionPenalties = new();
        public const double TtsCoverageThreshold = 0.85;

        public sealed class TestGroupRunContext
        {
            public int RunId { get; init; }
            public string Group { get; init; } = string.Empty;
            public string ModelName { get; init; } = string.Empty;
            public ModelInfo ModelInfo { get; init; } = default!;
            public List<TestDefinition> Tests { get; init; } = new();
            public string? TestFolder { get; init; }
        }

        public sealed class TestGroupRunResult
        {
            public int RunId { get; init; }
            public int Score { get; init; }
            public int Steps { get; init; }
            public int PassedSteps { get; init; }
            public bool PassedAll { get; init; }
            public long? DurationMs { get; init; }
        }

        private static string SanitizeIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "test";
            var chars = value.Trim().ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray();
            var sanitized = new string(chars).Trim('_');
            return string.IsNullOrWhiteSpace(sanitized) ? "test" : sanitized;
        }

        public LangChainTestService(
            DatabaseService database,
            StoriesService stories,
            ILangChainKernelFactory kernelFactory,
            LangChainAgentService agentService,
            ICustomLogger customLogger,
            IOllamaManagementService ollamaService,
            ICommandDispatcher commandDispatcher)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
            _agentService = agentService ?? throw new ArgumentNullException(nameof(agentService));
            _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
            _ollamaService = ollamaService ?? throw new ArgumentNullException(nameof(ollamaService));
            _commandDispatcher = commandDispatcher ?? throw new ArgumentNullException(nameof(commandDispatcher));
        }

        /// <summary>
        /// Run a group of tests for a specific model
        /// </summary>
        public async Task<object?> RunGroupAsync(string model, string group)
        {
            var context = PrepareGroupRun(model, group);
            if (context == null)
                return null;

            var result = await ExecuteGroupRunAsync(context);
            if (result == null)
                return null;

            return new
            {
                runId = result.RunId,
                score = result.Score,
                steps = result.Steps,
                passed = result.PassedSteps,
                duration = result.DurationMs
            };
        }

        public CommandHandle? EnqueueGroupRun(string model, string group)
        {
            var context = PrepareGroupRun(model, group);
            if (context == null) return null;

            var scope = $"tests/{SanitizeIdentifier(group)}/{SanitizeIdentifier(model)}";
            var metadata = new Dictionary<string, string>
            {
                ["runId"] = context.RunId.ToString(),
                ["model"] = context.ModelName,
                ["group"] = context.Group
            };

            return _commandDispatcher.Enqueue(
                $"test_{SanitizeIdentifier(group)}",
                async _ =>
                {
                    var result = await ExecuteGroupRunAsync(context);
                    if (result == null)
                    {
                        return new CommandResult(false, "Test non eseguito");
                    }

                    var msg = result.PassedAll
                        ? $"Test {group} completato ({result.PassedSteps}/{result.Steps})"
                        : $"Test {group} completato con errori ({result.PassedSteps}/{result.Steps})";
                    return new CommandResult(result.PassedAll, msg);
                },
                runId: context.RunId.ToString(),
                threadScope: scope,
                metadata: metadata);
        }

        public TestGroupRunContext? PrepareGroupRun(string model, string group)
        {
            // Resolve model by name locally (DBService no longer exposes name-based GetModelInfo)
            var modelInfo = _database.ListModels().FirstOrDefault(m => string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase));
            if (modelInfo == null) return null;

            var tests = _database.GetPromptsByGroup(group) ?? new List<TestDefinition>();
            string? testFolder = SetupTestFolder(modelInfo.Name, group, tests);

            var runId = _database.CreateTestRun(
                modelInfo.Id != null ? modelInfo.Id.Value : throw new InvalidOperationException("Model ID is null"),
                group,
                $"Group run {group} (LangChain)",
                false,
                null,
                "Started from LangChainTestService.RunGroupAsync",
                testFolder);

            _customLogger?.Start(runId.ToString());
            LogTestMessage(runId.ToString(), $"[{modelInfo.Name}] Starting test group: {group}");

            return new TestGroupRunContext
            {
                RunId = runId,
                Group = group,
                ModelName = modelInfo.Name,
                ModelInfo = modelInfo,
                Tests = tests,
                TestFolder = testFolder
            };
        }

        public async Task<TestGroupRunResult?> ExecuteGroupRunAsync(TestGroupRunContext context)
        {
            if (context == null) return null;

            var modelInfo = context.ModelInfo;
            if (modelInfo == null) return null;

            var model = context.ModelName;
            var group = context.Group;
            var runId = context.RunId;
            var tests = context.Tests ?? new List<TestDefinition>();
            var testFolder = context.TestFolder;

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
            if (_ttsEmotionPenalties.TryGetValue(runId, out var penaltyCount) && penaltyCount > 0)
            {
                var originalScore = score;
                score = Math.Max(0, score - penaltyCount);
                var penaltyMessage = $"[{model}] Penalità TTS: -{penaltyCount} punti per emozioni tutte 'neutral' (score {originalScore} -> {score})";
                _customLogger?.Append(runId.ToString(), penaltyMessage);
                LogTestMessage(runId.ToString(), penaltyMessage, "Warning");
            }
            var passedFlag = steps > 0 && passedCount == steps;

            _database.UpdateTestRunResult(runId, passedFlag, durationMs);
            _database.UpdateModelTestResults(
                modelInfo.Id != null ? modelInfo.Id.Value : throw new InvalidOperationException("Model ID is null"),
                score,
                new Dictionary<string, bool?>(),
                durationMs.HasValue ? (double?)(durationMs.Value / 1000.0) : null);

            _database.RecalculateModelScore(modelInfo.Id != null ? modelInfo.Id.Value : throw new InvalidOperationException("Model ID is null"));

            if (passedFlag)
            {
                LogTestMessage(runId.ToString(), $"✅ [{model}] Test completato: {passedCount}/{steps} passati, score {score}/10, durata {durationMs/1000.0:0.##}s");
            }
            else
            {
                LogTestMessage(runId.ToString(), $"❌ [{model}] Test fallito: {passedCount}/{steps} passati, score {score}/10", "Error");
            }

            return new TestGroupRunResult
            {
                RunId = runId,
                Score = score,
                Steps = steps,
                PassedSteps = passedCount,
                PassedAll = passedFlag,
                DurationMs = durationMs
            };
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
                    var context = PrepareGroupRun(modelInfo.Name, selectedGroup);
                    if (context == null) continue;
                    var result = await ExecuteGroupRunAsync(context);
                    if (result != null)
                    {
                        runResults.Add(new
                        {
                            runId = result.RunId,
                            score = result.Score,
                            steps = result.Steps,
                            passed = result.PassedSteps,
                            duration = result.DurationMs
                        });
                    }
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var context = new TestExecutionContext(
            test,
            stepId,
            sw,
            runId,
            idx,
            model,
            modelInfo,
            prompt,
            testFolder);

        var command = CreateCommandForTest(test);
        if (command == null)
        {
            _database.UpdateTestStepResult(
                stepId,
                false,
                null,
                $"Unknown test type: {test.TestType}",
                null);
            return;
        }

        try
        {
            await command.ExecuteAsync(context);
        }
        catch (Exception ex)
        {
            _database.UpdateTestStepResult(stepId, false, null, ex.Message, null);
            LogTestMessage(runId.ToString(), $"[{model}] Step {idx} ERROR: {ex.Message}", "Error");
            }
        }

        // ==================== Test Type Handlers ====================

        private ITestCommand? CreateCommandForTest(TestDefinition test)
        {
            var type = (test.TestType ?? string.Empty).ToLowerInvariant();
            return type switch
            {
                "question" => new QuestionTestCommand(this),
                "functioncall" => new FunctionCallTestCommand(this),
                "writer" => new WriterTestCommand(this),
                "tts" => new TtsTestCommand(this),
                _ => null
            };
        }

        private interface ITestCommand
        {
            Task ExecuteAsync(TestExecutionContext context);
        }

        private sealed record TestExecutionContext(
            TestDefinition Test,
            int StepId,
            System.Diagnostics.Stopwatch Stopwatch,
            int RunId,
            int StepIndex,
            string Model,
            ModelInfo ModelInfo,
            string Prompt,
            string? TestFolder);

        private sealed class QuestionTestCommand : ITestCommand
        {
            private readonly LangChainTestService _service;
            public QuestionTestCommand(LangChainTestService service) => _service = service;

            public async Task ExecuteAsync(TestExecutionContext context)
            {
                var test = context.Test;
                var stepId = context.StepId;
                var sw = context.Stopwatch;
                var runId = context.RunId;
                var idx = context.StepIndex;
                var model = context.Model;
                var prompt = context.Prompt;

                var orchestrator = _service._kernelFactory.CreateOrchestrator(model, _service.ParseAllowedPlugins(test), null);
                var chatBridge = _service._kernelFactory.CreateChatBridge(model, test.Temperature, test.TopP);
                var timeout = test.TimeoutSecs > 0 ? Math.Max(1, test.TimeoutSecs) : 30;

                var instructions = _service.LoadExecutionPlan(test.ExecutionPlan);
                var fullPrompt = !string.IsNullOrWhiteSpace(instructions)
                    ? $"{instructions}\n\n{prompt}"
                    : prompt;
                var hasTools = orchestrator.GetToolSchemas().Any();
                fullPrompt = _service.AppendResponseFormatToPrompt(fullPrompt, test.JsonResponseFormat, hasTools);

                try
                {
                    var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                    var reactLoop = new ReActLoopOrchestrator(orchestrator, _service._customLogger, runId: runId.ToString(), modelBridge: chatBridge);
                    var reactResult = await reactLoop.ExecuteAsync(fullPrompt, cts.Token);
                    sw.Stop();

                    var responseText = reactResult.FinalResponse ?? string.Empty;
                    bool passed = _service.ValidateResponse(responseText, test, out var failReason);

                    var resultJson = JsonSerializer.Serialize(new
                    {
                        response = responseText,
                        expected = test.ExpectedPromptValue,
                        range = test.ValidScoreRange
                    });

                    _service._database.UpdateTestStepResult(
                        stepId,
                        passed,
                        resultJson,
                        passed ? null : failReason,
                        (long?)sw.ElapsedMilliseconds);

                    _service._customLogger?.Append(runId.ToString(),
                        $"[{model}] Step {idx} {(passed ? "PASSED" : "FAILED")}: {test.FunctionName ?? $"id_{test.Id}"} ({sw.ElapsedMilliseconds}ms)");
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    _service._database.UpdateTestStepResult(
                        stepId,
                        false,
                        null,
                        $"Timeout after {timeout}s",
                        (long?)sw.ElapsedMilliseconds);
                    _service._customLogger?.Append(runId.ToString(), $"[{model}] Step {idx} TIMEOUT");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _service._database.UpdateTestStepResult(
                        stepId,
                        false,
                        null,
                        $"Error: {ex.Message}",
                        (long?)sw.ElapsedMilliseconds);
                    _service._customLogger?.Append(runId.ToString(), $"[{model}] Step {idx} ERROR: {ex.Message}");
                }
            }
        }

        private sealed class FunctionCallTestCommand : ITestCommand
        {
            private readonly LangChainTestService _service;
            public FunctionCallTestCommand(LangChainTestService service) => _service = service;

            public async Task ExecuteAsync(TestExecutionContext context)
            {
                var test = context.Test;
                var stepId = context.StepId;
                var sw = context.Stopwatch;
                var runId = context.RunId;
                var idx = context.StepIndex;
                var model = context.Model;

                var orchestrator = _service._kernelFactory.CreateOrchestrator(model, _service.ParseAllowedPlugins(test), null);
                var chatBridge = _service._kernelFactory.CreateChatBridge(model, test.Temperature, test.TopP);
                var timeout = test.TimeoutSecs > 0 ? Math.Max(1, test.TimeoutSecs) : 30;

                var instructions = _service.LoadExecutionPlan(test.ExecutionPlan);
                var fullPrompt = !string.IsNullOrWhiteSpace(instructions)
                    ? $"{instructions}\n\n{context.Prompt}"
                    : context.Prompt;
                var hasTools = orchestrator.GetToolSchemas().Any();
                fullPrompt = _service.AppendResponseFormatToPrompt(fullPrompt, test.JsonResponseFormat, hasTools);

                try
                {
                    var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                    var reactLoop = new ReActLoopOrchestrator(orchestrator, _service._customLogger, runId: runId.ToString(), modelBridge: chatBridge);
                    var reactResult = await reactLoop.ExecuteAsync(fullPrompt, cts.Token);
                    sw.Stop();

                    var responseText = reactResult.FinalResponse ?? string.Empty;
                    bool passed = _service.ValidateFunctionCallResponse(responseText, test, out var failReason);

                    var resultJson = JsonSerializer.Serialize(new
                    {
                        response = responseText,
                        functionCalled = test.ExpectedBehavior,
                        toolsExecuted = reactResult.ExecutedTools.Select(t => new { t.ToolName, t.Input, t.Output }),
                        iterations = reactResult.IterationCount,
                        success = reactResult.Success
                    });

                    _service._database.UpdateTestStepResult(
                        stepId,
                        passed,
                        resultJson,
                        passed ? null : (failReason ?? reactResult.Error),
                        (long?)sw.ElapsedMilliseconds);

                    _service._customLogger?.Append(runId.ToString(),
                        $"[{model}] Step {idx} {(passed ? "PASSED" : "FAILED")}: {test.FunctionName ?? $"id_{test.Id}"} ({sw.ElapsedMilliseconds}ms) - {reactResult.ExecutedTools.Count} tools called");
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    _service._database.UpdateTestStepResult(
                        stepId,
                        false,
                        null,
                        $"Timeout after {timeout}s",
                        (long?)sw.ElapsedMilliseconds);
                }
                catch (ModelNoToolsSupportException ex)
                {
                    sw.Stop();
                    context.ModelInfo.NoTools = true;
                    _service._database.UpsertModel(context.ModelInfo);
                    _service._customLogger?.Append(runId.ToString(), $"[{model}] Model does not support tools - marked NoTools=true");
                    
                    _service._database.UpdateTestStepResult(
                        stepId,
                        false,
                        null,
                        $"Model does not support tool calling: {ex.Message}",
                        (long?)sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    if (ex.Message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
                    {
                        context.ModelInfo.NoTools = true;
                        _service._database.UpsertModel(context.ModelInfo);
                        _service._customLogger?.Append(runId.ToString(), $"[{model}] Model does not support tools - marked NoTools=true");
                    }

                    _service._database.UpdateTestStepResult(
                        stepId,
                        false,
                        null,
                        ex.Message,
                        (long?)sw.ElapsedMilliseconds);
                }
            }
        }

        private sealed class WriterTestCommand : ITestCommand
        {
            private readonly LangChainTestService _service;
            public WriterTestCommand(LangChainTestService service) => _service = service;

            public async Task ExecuteAsync(TestExecutionContext context)
            {
                var test = context.Test;
                var stepId = context.StepId;
                var sw = context.Stopwatch;
                var runId = context.RunId;
                var idx = context.StepIndex;
                var model = context.Model;
                var prompt = context.Prompt;

                var orchestrator = _service._kernelFactory.CreateOrchestrator(model, _service.ParseAllowedPlugins(test), null);
                var chatBridge = _service._kernelFactory.CreateChatBridge(model, test.Temperature, test.TopP);
                var writerMaxTokens = Math.Max(context.ModelInfo.ContextToUse, context.ModelInfo.MaxContext);
                if (writerMaxTokens > 0)
                {
                    if (!chatBridge.MaxResponseTokens.HasValue)
                    {
                        chatBridge.MaxResponseTokens = writerMaxTokens;
                    }
                    else
                    {
                        chatBridge.MaxResponseTokens = Math.Max(chatBridge.MaxResponseTokens.Value, writerMaxTokens);
                    }
                }
                var timeout = test.TimeoutSecs > 0 ? Math.Max(1, test.TimeoutSecs) : 120;

                var instructions = _service.LoadExecutionPlan(test.ExecutionPlan) ?? string.Empty;

                var fullPrompt = $"{instructions}\n\n{prompt}";
                var hasTools = orchestrator.GetToolSchemas().Any();
                fullPrompt = _service.AppendResponseFormatToPrompt(fullPrompt, test.JsonResponseFormat, hasTools);

                try
                {
                    const int MinStoryLength = 6000;
                    const int MaxAttempts = 3;

                    string? cleanStoryText = null;
                    long? storyId = null;
                    double evaluationScore = 0.0;
                    bool ok = false;

                    for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                    {
                        var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                        // recreate orchestrator for each attempt to ensure a fresh tool state
                        var attemptOrchestrator = _service._kernelFactory.CreateOrchestrator(model, _service.ParseAllowedPlugins(test), null);
                        var reactLoop = new ReActLoopOrchestrator(attemptOrchestrator, _service._customLogger, runId: runId.ToString(), modelBridge: chatBridge);

                        var promptAttempt = fullPrompt;
                        if (attempt > 1)
                        {
                            promptAttempt += $"\n\n[RETRY {attempt}/{MaxAttempts}] The previous story was too short (<{MinStoryLength} chars). Rewrite the story from scratch, make it significantly longer and more detailed. Produce at least {MinStoryLength * 2} characters. Do not reuse the previous text. Why are you producing so short stories?";
                            _service._customLogger?.Append(runId.ToString(), $"[{model}] Retry {attempt}/{MaxAttempts}: requesting a longer rewrite.");
                        }

                        var reactResult = await reactLoop.ExecuteAsync(promptAttempt, cts.Token);

                        var storyText = reactResult.FinalResponse ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(storyText))
                        {
                            _service._customLogger?.Append(runId.ToString(), $"[{model}] Attempt {attempt} returned empty story.");
                            if (attempt == MaxAttempts)
                            {
                                sw.Stop();
                                _service._database.UpdateTestStepResult(
                                    stepId,
                                    false,
                                    string.Empty,
                                    "No story text returned after multiple attempts");
                                return;
                            }
                            continue;
                        }

                        cleanStoryText = _service.ExtractJsonContent(storyText) ?? storyText;
                        if (cleanStoryText.Length < MinStoryLength)
                        {
                            _service._customLogger?.Append(runId.ToString(), $"[{model}] Attempt {attempt} produced too short story ({cleanStoryText.Length} chars).");
                            if (attempt == MaxAttempts)
                            {
                                sw.Stop();
                                _service._database.UpdateTestStepResult(
                                    stepId,
                                    false,
                                    JsonSerializer.Serialize(new { length = cleanStoryText.Length }),
                                    $"Story too short ({cleanStoryText.Length} chars) after {MaxAttempts} attempts");
                                return;
                            }
                            // otherwise retry
                            continue;
                        }

                        // success: persist story and evaluate
                        var generatedStatusId = _service._stories.ResolveStatusId("generated");
                        storyId = _service._stories.InsertSingleStory(
                            test.Prompt ?? string.Empty,
                            cleanStoryText,
                            context.ModelInfo.Id,
                            null,
                            0.0,
                            null,
                            0,
                            generatedStatusId,
                            null);

                        _service._database.AddTestAsset(
                            stepId,
                            "story",
                            $"/stories/{storyId}",
                            "Generated story",
                            durationSec: sw.Elapsed.TotalSeconds,
                            sizeBytes: cleanStoryText.Length,
                            storyId: storyId);

                        evaluationScore = await _service.EvaluateStoryWithAgentsAsync(storyId.Value, cleanStoryText, runId, model);
                        var passed = evaluationScore >= 4.0;

                        sw.Stop();

                        _service._database.UpdateTestStepResult(
                            stepId,
                            passed,
                            JsonSerializer.Serialize(new { storyId, length = cleanStoryText.Length, evaluationScore }),
                            passed ? null : $"Evaluation score too low: {evaluationScore:F1}/10",
                            (long?)sw.ElapsedMilliseconds);

                        _service._customLogger?.Append(runId.ToString(), $"[{model}] Step {idx} {(passed ? "PASSED" : "FAILED")}: story ID {storyId}, score {evaluationScore:F1}/10");

                        ok = true;
                        break;
                    }

                    if (!ok)
                    {
                        // already handled above; defensive fallback
                        sw.Stop();
                        _service._database.UpdateTestStepResult(
                            stepId,
                            false,
                            null,
                            "Story generation failed after multiple attempts");
                    }
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    _service._database.UpdateTestStepResult(
                        stepId,
                        false,
                        null,
                        $"Timeout after {timeout}s",
                        (long?)sw.ElapsedMilliseconds);
                }
                catch (ModelNoToolsSupportException ex)
                {
                    sw.Stop();
                    context.ModelInfo.NoTools = true;
                    _service._database.UpsertModel(context.ModelInfo);
                    _service._customLogger?.Append(runId.ToString(), $"[{model}] Model does not support tools - marked NoTools=true");
                    
                    _service._database.UpdateTestStepResult(
                        stepId,
                        false,
                        null,
                        $"Model does not support tool calling: {ex.Message}",
                        (long?)sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _service._database.UpdateTestStepResult(
                        stepId,
                        false,
                        null,
                        ex.Message,
                        (long?)sw.ElapsedMilliseconds);
                }
            }
        }

        private sealed class TtsTestCommand : ITestCommand
        {
            private readonly LangChainTestService _service;
            public TtsTestCommand(LangChainTestService service) => _service = service;

            public async Task ExecuteAsync(TestExecutionContext context)
            {
                await _service.ExecuteTtsTestAsync(
                    context.Test,
                    context.StepId,
                    context.Stopwatch,
                    context.RunId,
                    context.StepIndex,
                    context.Model,
                    context.ModelInfo,
                    context.Prompt,
                    context.TestFolder);
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
            _customLogger?.Append(runId.ToString(), $"[{model}] === TTS TEST ===");
            
            // Use the real TTS agent configuration but override only the model under test.
            var ttsAgent = _database.ListAgents()
                .FirstOrDefault(a => a.IsActive && a.Role.Equals("tts_json", StringComparison.OrdinalIgnoreCase));
            var ttsStory = _database.GetStoryById(23);
            var ttsStoryText = ttsStory?.StoryRaw ?? prompt;
            var allowedPlugins = ParseAgentSkills(ttsAgent) ?? ParseAllowedPlugins(test);
            var agentPrompt = !string.IsNullOrWhiteSpace(ttsAgent?.Prompt)
                ? ttsAgent.Prompt!
                : prompt;

            if (!string.IsNullOrWhiteSpace(ttsStoryText))
            {
                _customLogger?.Append(runId.ToString(), $"[{model}] Using story 23 for TTS test ({ttsStoryText.Length} chars)");
            }

            const int maxRetries = 3;
            var chatBridge = _kernelFactory.CreateChatBridge(model, test.Temperature, test.TopP);
            HybridLangChainOrchestrator? lastOrchestrator = null;
            try
            {
                for (int attempt = 1; attempt <= maxRetries; attempt++)
                {
                    var orchestrator = _kernelFactory.CreateOrchestrator(model, allowedPlugins, ttsAgent?.Id, testFolder, ttsStoryText);
                    lastOrchestrator = orchestrator;
                    var timeout = test.TimeoutSecs > 0 ? Math.Max(1, test.TimeoutSecs) : 60;

                    var executionPlan = LoadExecutionPlan(test.ExecutionPlan);
                    var fullPrompt = $"{agentPrompt}\n\nQuesta è la storia:\n{ttsStoryText}";
                    var systemMessage = string.IsNullOrWhiteSpace(ttsAgent?.Instructions)
                        ? executionPlan
                        : string.IsNullOrWhiteSpace(executionPlan)
                            ? ttsAgent.Instructions
                            : $"{ttsAgent.Instructions}\n\n{executionPlan}";

                    var hasTools = orchestrator.GetToolSchemas().Any();
                    fullPrompt = AppendResponseFormatToPrompt(fullPrompt, test.JsonResponseFormat, hasTools);

                    if (attempt > 1)
                    {
                        fullPrompt += $"\n\n[RETRY {attempt}/{maxRetries}] The schema file was not generated. Call ConfirmSchema() to save it.";
                    }

                    var stepLabel = test.FunctionName ?? $"id_{test.Id}";

                    try
                    {
                        var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeout));
                        
                        // Create ReAct loop to execute TTS schema generation
                        // Pass execution plan as system message instead of concatenating to user prompt
                        var reactLoop = new ReActLoopOrchestrator(orchestrator, _customLogger, runId: runId.ToString(), modelBridge: chatBridge, systemMessage: systemMessage);
                        var reactResult = await reactLoop.ExecuteAsync(fullPrompt, cts.Token);
                        sw.Stop();
                        
                        var response = reactResult.FinalResponse;
                        var responsePreview = string.IsNullOrEmpty(response) ? "empty" : response;
                        _customLogger?.Append(runId.ToString(), $"[{model}] Model Response (attempt {attempt}): {responsePreview}");

                        var generatedFile = FindLatestJson(testFolder);
                        if (!string.IsNullOrWhiteSpace(generatedFile))
                        {
                            _customLogger?.Append(runId.ToString(), $"[{model}] Found generated file: {Path.GetFileName(generatedFile)}");
                        }

                        if (!string.IsNullOrWhiteSpace(generatedFile))
                        {
                            if (!TryComputeTtsCoverage(ttsStoryText, generatedFile, out var coverage))
                            {
                                var warn = $"[{model}] Unable to verify TTS coverage for {Path.GetFileName(generatedFile)}. Retrying.";
                                _customLogger?.Append(runId.ToString(), warn, "Warning");
                                LogTestMessage(runId.ToString(), warn, "Warning");
                                continue;
                            }

                            if (coverage < TtsCoverageThreshold)
                            {
                                var warn = $"[{model}] Coverage {coverage:P1} below required {TtsCoverageThreshold:P0}. Retrying.";
                                _customLogger?.Append(runId.ToString(), warn, "Warning");
                                LogTestMessage(runId.ToString(), warn, "Warning");
                                continue;
                            }

                            var resultDetails = JsonSerializer.Serialize(new
                            {
                                response = responsePreview,
                                file = Path.GetFileName(generatedFile),
                                coverage
                            });

                            _database.UpdateTestStepResult(
                                stepId,
                                true,
                                resultDetails,
                                null,
                                (long?)sw.ElapsedMilliseconds);

                            var successMessage = $"[{model}] Step {idx} PASSED: {stepLabel} ({sw.ElapsedMilliseconds}ms) - saved {Path.GetFileName(generatedFile)} (coverage {coverage:P1})";
                            _customLogger?.Append(runId.ToString(), successMessage);
                            LogTestMessage(runId.ToString(), successMessage);

                            if (IsAllNeutralEmotion(generatedFile))
                            {
                                AddTtsNeutralPenalty(runId);
                                var penaltyMsg = $"[{model}] TTS schema contiene solo emozioni 'neutral' ({Path.GetFileName(generatedFile)}): penalità applicata";
                                _customLogger?.Append(runId.ToString(), penaltyMsg);
                                LogTestMessage(runId.ToString(), penaltyMsg, "Warning");
                            }

                            return; // Success
                        }
                        else if (attempt < maxRetries)
                        {
                            _customLogger?.Append(runId.ToString(), $"[{model}] No file generated, retrying... ({attempt}/{maxRetries})");
                            continue;
                        }
                        else
                        {
                            const string errorReason = "No JSON file generated after retries";
                            _database.UpdateTestStepResult(stepId, false, response, errorReason, (int)sw.ElapsedMilliseconds);
                            var failMessage = $"[{model}] Step {idx} FAILED: {stepLabel} - {errorReason}";
                            _customLogger?.Append(runId.ToString(), failMessage);
                            LogTestMessage(runId.ToString(), failMessage, "Error");
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        sw.Stop();
                        if (attempt < maxRetries)
                        {
                            _customLogger?.Append(runId.ToString(), $"[{model}] Timeout on attempt {attempt}, retrying...");
                            continue;
                        }
                        else
                        {
                            var errorReason = $"Timeout after {timeout}s on all {maxRetries} attempts";
                            _database.UpdateTestStepResult(stepId, false, null, errorReason, (int)sw.ElapsedMilliseconds);
                            var timeoutMessage = $"[{model}] Step {idx} FAILED: {stepLabel} - {errorReason}";
                            _customLogger?.Append(runId.ToString(), timeoutMessage);
                            LogTestMessage(runId.ToString(), timeoutMessage, "Error");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        if (attempt < maxRetries)
                        {
                            _customLogger?.Append(runId.ToString(), $"[{model}] Error on attempt {attempt}: {ex.Message}, retrying...");
                            continue;
                        }
                        else
                        {
                            var errorReason = $"{ex.Message} after {maxRetries} attempts";
                            _database.UpdateTestStepResult(stepId, false, null, errorReason, (int)sw.ElapsedMilliseconds);
                            var errorMessage = $"[{model}] Step {idx} FAILED: {stepLabel} - {ex.Message}";
                            _customLogger?.Append(runId.ToString(), errorMessage);
                            LogTestMessage(runId.ToString(), errorMessage, "Error");
                            return;
                        }
                    }
                }
            }
            finally
            {
                await FinalizeTtsArtifactsAsync(lastOrchestrator, testFolder, model, runId);
            }
        }

        // ==================== Helper Methods ====================

        private bool TryComputeTtsCoverage(string originalStory, string schemaPath, out double coverage)
        {
            coverage = 0;
            try
            {
                var normalizedStory = NormalizeForCoverage(originalStory);
                if (normalizedStory.Length == 0 || !File.Exists(schemaPath))
                    return false;

                var json = File.ReadAllText(schemaPath);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Timeline", out var timeline) || timeline.ValueKind != JsonValueKind.Array)
                    return false;

                int timelineLength = 0;
                foreach (var entry in timeline.EnumerateArray())
                {
                    if (entry.TryGetProperty("Text", out var textElem) && textElem.ValueKind == JsonValueKind.String)
                    {
                        timelineLength += NormalizeForCoverage(textElem.GetString() ?? string.Empty).Length;
                    }
                }

                if (timelineLength <= 0)
                    return false;

                coverage = (double)timelineLength / normalizedStory.Length;
                return true;
            }
            catch
            {
                coverage = 0;
                return false;
            }
        }

        private static string NormalizeForCoverage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (!char.IsWhiteSpace(ch))
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private string? FindLatestJson(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return null;

            return Directory.GetFiles(folderPath, "*.json")
                .Where(f => !f.EndsWith("_expected_result.json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetCreationTimeUtc)
                .FirstOrDefault();
        }

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

        private IEnumerable<string>? ParseAgentSkills(Agent? agent)
        {
            if (agent == null || string.IsNullOrWhiteSpace(agent.Skills))
                return null;

            try
            {
                var skills = JsonSerializer.Deserialize<List<string>>(agent.Skills);
                return skills?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim());
            }
            catch
            {
                return null;
            }
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

        private async Task FinalizeTtsArtifactsAsync(HybridLangChainOrchestrator? orchestrator, string? testFolder, string model, int runId)
        {
            try
            {
                var ttsTool = orchestrator?.GetTool<TtsSchemaTool>("ttsschema");
                if (ttsTool != null && ttsTool.HasSchemaEntries)
                {
                    if (ttsTool.TrySaveSnapshot(out var savedPath) && !string.IsNullOrWhiteSpace(savedPath))
                    {
                        _customLogger?.Append(runId.ToString(), $"[{model}] Schema TTS salvata ({Path.GetFileName(savedPath)})");
                    }
                    return;
                }

                await CleanupTestFolderAsync(testFolder, model, runId);
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId.ToString(), $"[{model}] Errore finalizzazione TTS: {ex.Message}");
            }
        }

        private Task CleanupTestFolderAsync(string? testFolder, string model, int runId)
        {
            if (string.IsNullOrWhiteSpace(testFolder) || !Directory.Exists(testFolder))
                return Task.CompletedTask;

            try
            {
                Directory.Delete(testFolder, true);
                _customLogger?.Append(runId.ToString(), $"[{model}] Cartella test rimossa (nessuna generazione TTS) ");
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId.ToString(), $"[{model}] Impossibile cancellare cartella test: {ex.Message}");
            }

            return Task.CompletedTask;
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

        private void AddTtsNeutralPenalty(int runId)
        {
            if (_ttsEmotionPenalties.ContainsKey(runId))
            {
                _ttsEmotionPenalties[runId]++;
            }
            else
            {
                _ttsEmotionPenalties[runId] = 1;
            }
        }

        private bool IsAllNeutralEmotion(string jsonFilePath)
        {
            try
            {
                if (!File.Exists(jsonFilePath))
                    return false;

                using var stream = File.OpenRead(jsonFilePath);
                using var doc = JsonDocument.Parse(stream);
                if (!doc.RootElement.TryGetProperty("Timeline", out var timelineElem) || timelineElem.ValueKind != JsonValueKind.Array)
                    return false;

                bool foundEmotion = false;
                foreach (var entry in timelineElem.EnumerateArray())
                {
                    if (!entry.TryGetProperty("Emotion", out var emotionElem) || emotionElem.ValueKind != JsonValueKind.String)
                        return false;

                    foundEmotion = true;
                    var emotionValue = emotionElem.GetString();
                    if (!string.Equals(emotionValue, "neutral", StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                return foundEmotion;
            }
            catch (Exception ex)
            {
                _customLogger?.Log("Warn", "LangChainTestService", $"Impossibile verificare emozioni nel file TTS: {ex.Message}");
                return false;
            }
        }

        private string AppendResponseFormatToPrompt(string prompt, string? responseFormat, bool hasTools = false)
        {
            if (string.IsNullOrWhiteSpace(responseFormat))
                return prompt;

            // If we have tools or format will be handled by model (Ollama format parameter), 
            // don't add format instruction to prompt
            if (hasTools)
                return prompt;

            // Skip forcing JSON format if the prompt already mentions tool functions (e.g., evaluate_full_story/read_story_part)
            var lower = prompt.ToLowerInvariant();
            if (lower.Contains("evaluate_full_story") || lower.Contains("read_story_part"))
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
                _customLogger?.Append(runId.ToString(), $"[{writerModel}] No evaluators found, using default score");
                return 10.0;
            }

            var totalScoreSum = 0;
            var evaluatorCount = 0;

            foreach (var evaluator in evaluators)
            {
                try
                {
                    _customLogger?.Append(runId.ToString(), $"[{writerModel}] Evaluating with {evaluator.Name}...");
                    var result = await _stories.EvaluateStoryWithAgentAsync(storyId, evaluator.Id);

                    if (result.success)
                    {
                        totalScoreSum += (int)result.score;
                        evaluatorCount++;
                        _customLogger?.Append(runId.ToString(), $"[{writerModel}] Evaluator score: {result.score}/100");
                    }
                }
                catch (Exception ex)
                {
                    _customLogger?.Append(runId.ToString(), $"[{writerModel}] Evaluator error: {ex.Message}", "Warning");
                }
            }

            if (evaluatorCount == 0)
                return 10.0;

            var maxPossible = evaluatorCount * 100;
            var finalScore = (double)totalScoreSum / maxPossible * 10.0;

            _customLogger?.Append(runId.ToString(), $"[{writerModel}] Final score: {finalScore:F2}/10");

            return finalScore;
        }

        private void LogTestMessage(string runId, string message, string level = "Information")
        {
            _customLogger?.Append(runId, message);
            
            try
            {
                _customLogger?.Log(level, "LangChainTestService", message);
            }
            catch { }
        }
    }
}
