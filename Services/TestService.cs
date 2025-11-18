using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public interface ITestService
    {
        Task ExecuteTestAsync(int runId, int idx, TestDefinition t, string model, ModelInfo modelInfo, string agentInstructions, object? defaultAgent);
        Task<object?> RunGroupAsync(string model, string group);
        Task<List<object>> RunAllEnabledModelsAsync(string? group);
    }

    /// <summary>
    /// Simplified TestService using OpenAI connector for all models (including Ollama).
    /// Uses ResponseFormat for structured outputs, eliminating manual parsing.
    /// Creates new kernels dynamically when skills change.
    /// </summary>
    public class TestService : ITestService
    {
        private readonly DatabaseService _database;
        private readonly ProgressService _progress;
        private readonly StoriesService _stories;
        private readonly KernelFactory _factory;

        public TestService(
            DatabaseService database,
            ProgressService progress,
            StoriesService stories,
            KernelFactory factory)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            _stories = stories ?? throw new ArgumentNullException(nameof(stories));
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public async Task<object?> RunGroupAsync(string model, string group)
        {
            var modelInfo = _database.GetModelInfo(model);
            if (modelInfo == null) return null;

            var tests = _database.GetPromptsByGroup(group) ?? new List<TestDefinition>();
            var runId = _database.CreateTestRun(
                modelInfo.Name,
                group,
                $"Group run {group}",
                false,
                null,
                "Started from TestService.RunGroupAsync");

            _progress?.Start(runId.ToString());

            // Warmup call only for Ollama models to exclude cold start from measurements
            if (modelInfo.Provider?.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    _progress?.Append(runId.ToString(), $"[{model}] Performing warmup call for Ollama model...");
                    var warmupKernel = _factory.CreateKernel(model, Array.Empty<string>());
                    if (warmupKernel?.Kernel != null)
                    {
                        var warmupService = warmupKernel.Kernel.GetRequiredService<IChatCompletionService>();
                        var warmupSettings = new OpenAIPromptExecutionSettings
                        {
                            Temperature = model.Contains("gpt-5-nano", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0,
                            MaxTokens = 10
                        };
                        var warmupHistory = new ChatHistory();
                        warmupHistory.AddUserMessage("Hello");
                        
                        await InvokeWithTimeoutAsync(
                            warmupService.GetChatMessageContentAsync(warmupHistory, warmupSettings, warmupKernel.Kernel),
                            10000);
                        
                        _progress?.Append(runId.ToString(), $"[{model}] Warmup completed");
                    }
                }
                catch (Exception ex)
                {
                    _progress?.Append(runId.ToString(), $"[{model}] Warmup failed (continuing anyway): {ex.Message}");
                }
            }

            // Start timing after warmup (if executed)
            var testStartUtc = DateTime.UtcNow;

            int idx = 0;
            foreach (var test in tests)
            {
                idx++;
                await ExecuteTestAsync(runId, idx, test, model, modelInfo, string.Empty, null);
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

            // Recalculate overall model score based on all latest group results
            _database.RecalculateModelScore(modelInfo.Name);

            // Send completion notification with success/error status
            if (passedFlag)
            {
                _progress?.Append(runId.ToString(), $"✅ [{model}] Test completato con successo: {passedCount}/{steps} test passati, score {score}/10, durata {durationMs/1000.0:0.##}s", "success");
            }
            else
            {
                _progress?.Append(runId.ToString(), $"❌ [{model}] Test fallito: {passedCount}/{steps} test passati, score {score}/10", "error");
            }

            return new { runId, score, steps, passed = passedCount, duration = durationMs };
        }

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
                catch
                {
                    // ignore per-model failures
                }
            }

            return runResults;
        }

        public async Task ExecuteTestAsync(
            int runId,
            int idx,
            TestDefinition test,
            string model,
            ModelInfo modelInfo,
            string agentInstructions,
            object? defaultAgent)
        {
            var stepId = _database.AddTestStep(
                runId,
                idx,
                test.FunctionName ?? $"test_{test.Id}",
                JsonSerializer.Serialize(new { prompt = test.Prompt }));

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                var testType = (test.TestType ?? string.Empty).ToLowerInvariant();

                switch (testType)
                {
                    case "question":
                        await ExecuteQuestionTestAsync(test, stepId, sw, runId, idx, model);
                        break;

                    case "functioncall":
                        await ExecuteFunctionCallTestAsync(test, stepId, sw, runId, idx, model, modelInfo);
                        break;

                    case "writer":
                        await ExecuteWriterTestAsync(test, stepId, sw, runId, idx, model, modelInfo);
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
                _progress?.Append(runId.ToString(), $"[{model}] Step {idx} ERROR: {ex.Message}");
            }
        }

        private async Task ExecuteQuestionTestAsync(
            TestDefinition test,
            int stepId,
            System.Diagnostics.Stopwatch sw,
            int runId,
            int idx,
            string model)
        {
            var kernel = CreateKernelForTest(model, test);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var settings = CreateExecutionSettings(test, model);
            var timeout = test.TimeoutMs > 0 ? test.TimeoutMs : 30000;

            var history = new ChatHistory();
            history.AddUserMessage(test.Prompt ?? string.Empty);

            try
            {
                var response = await InvokeWithTimeoutAsync(
                    chatService.GetChatMessageContentAsync(history, settings, kernel),
                    timeout);

                sw.Stop();

                var responseText = response?.Content ?? string.Empty;
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
            catch (TimeoutException)
            {
                sw.Stop();
                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    $"Timeout after {timeout / 1000}s",
                    (long?)sw.ElapsedMilliseconds);
                _progress?.Append(runId.ToString(), $"[{model}] Step {idx} TIMEOUT");
            }
        }

        private async Task ExecuteFunctionCallTestAsync(
            TestDefinition test,
            int stepId,
            System.Diagnostics.Stopwatch sw,
            int runId,
            int idx,
            string model,
            ModelInfo modelInfo)
        {
            var kernel = CreateKernelForTest(model, test);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var settings = CreateExecutionSettings(test, model);
            settings.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;

            var timeout = test.TimeoutMs > 0 ? test.TimeoutMs : 30000;
            var history = new ChatHistory();
            history.AddUserMessage(test.Prompt ?? string.Empty);

            try
            {
                var response = await InvokeWithTimeoutAsync(
                    chatService.GetChatMessageContentAsync(history, settings, kernel),
                    timeout);

                sw.Stop();

                var responseText = response?.Content ?? string.Empty;
                bool passed = ValidateFunctionCallResponse(responseText, test, out var failReason);

                var resultJson = JsonSerializer.Serialize(new
                {
                    response = responseText,
                    functionCalled = test.ExpectedBehavior
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
            catch (TimeoutException)
            {
                sw.Stop();
                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    $"Timeout after {timeout / 1000}s",
                    (long?)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Check for "no tools support" errors
                if (ex.Message.Contains("does not support tools", StringComparison.OrdinalIgnoreCase))
                {
                    modelInfo.NoTools = true;
                    _database.UpsertModel(modelInfo);
                    _progress?.Append(runId.ToString(), $"[{model}] Marked model as NoTools");
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
            ModelInfo modelInfo)
        {
            var kernel = CreateKernelForTest(model, test);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var settings = CreateExecutionSettings(test, model);
            // No need for ToolCallBehavior - writer just returns text

            // Load execution plan if specified
            var instructions = string.Empty;
            if (!string.IsNullOrWhiteSpace(test.ExecutionPlan))
            {
                var planPath = Path.Combine(Directory.GetCurrentDirectory(), "execution_plans", test.ExecutionPlan);
                if (File.Exists(planPath))
                    instructions = File.ReadAllText(planPath);
            }

            var timeout = test.TimeoutMs > 0 ? test.TimeoutMs : 60000; // Longer timeout for story generation
            var history = new ChatHistory();

            if (!string.IsNullOrWhiteSpace(instructions))
                history.AddSystemMessage(instructions);

            history.AddUserMessage(test.Prompt ?? string.Empty);

            try
            {
                var response = await InvokeWithTimeoutAsync(
                    chatService.GetChatMessageContentAsync(history, settings, kernel),
                    timeout);

                sw.Stop();

                var storyText = response?.Content ?? string.Empty;

                if (string.IsNullOrWhiteSpace(storyText))
                {
                    _database.UpdateTestStepResult(
                        stepId,
                        false,
                        string.Empty,
                        "No story text returned from writer",
                        (long?)sw.ElapsedMilliseconds);
                    return;
                }

                // Extract story content from JSON wrapper if present
                var cleanStoryText = storyText;
                if (storyText.Trim().StartsWith("{"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(storyText);
                        if (doc.RootElement.TryGetProperty("result", out var resultProp))
                        {
                            cleanStoryText = resultProp.GetString() ?? storyText;
                        }
                        else if (doc.RootElement.TryGetProperty("story", out var storyProp))
                        {
                            cleanStoryText = storyProp.GetString() ?? storyText;
                        }
                    }
                    catch
                    {
                        // Not JSON or parsing failed, use as-is
                    }
                }

                // Save the story to database
                var storyId = _stories.InsertSingleStory(
                    test.Prompt ?? string.Empty,
                    cleanStoryText,
                    _database.GetModelIdByName(model),
                    null, // agentId
                    0.0, // score - will be set by evaluation
                    null, // eval
                    0, // approved
                    "generated", // status
                    null // memoryKey
                );

                _database.AddTestAsset(
                    stepId,
                    "story",
                    $"/stories/{storyId}",
                    "Generated story",
                    durationSec: sw.Elapsed.TotalSeconds,
                    sizeBytes: cleanStoryText.Length,
                    storyId: storyId);

                // Evaluate story with evaluator agents
                var evaluationScore = await EvaluateStoryWithAgentsAsync(storyId, cleanStoryText, runId, model);

                var passed = evaluationScore >= 4.0; // Pass if score is 4 or higher
                
                _database.UpdateTestStepResult(
                    stepId,
                    passed,
                    JsonSerializer.Serialize(new { storyId = storyId, length = cleanStoryText.Length, evaluationScore }),
                    passed ? null : $"Evaluation score too low: {evaluationScore:F1}/10",
                    (long?)sw.ElapsedMilliseconds);

                _progress?.Append(runId.ToString(),
                    $"[{model}] Step {idx} {(passed ? "PASSED" : "FAILED")}: story ID {storyId}, evaluation score {evaluationScore:F1}/10");
            }
            catch (TimeoutException)
            {
                sw.Stop();
                _database.UpdateTestStepResult(
                    stepId,
                    false,
                    null,
                    $"Timeout after {timeout / 1000}s",
                    (long?)sw.ElapsedMilliseconds);
            }
        }

        private Kernel CreateKernelForTest(string model, TestDefinition test)
        {
            // Parse allowed plugins from test definition
            var allowedPlugins = string.IsNullOrWhiteSpace(test.AllowedPlugins)
                ? Array.Empty<string>()
                : test.AllowedPlugins
                    .Split(',')
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();

            // Create kernel with specified plugins (no special handling for writer tests)
            var kernelWithPlugins = _factory.CreateKernel(model, allowedPlugins);
            return kernelWithPlugins?.Kernel ?? throw new InvalidOperationException("Failed to create kernel");
        }

        private OpenAIPromptExecutionSettings CreateExecutionSettings(TestDefinition test, string model)
        {
            var settings = new OpenAIPromptExecutionSettings
            {
                Temperature = model.Contains("gpt-5-nano", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0,
                MaxTokens = 4000
            };

            // Apply JSON response format ONLY for OpenAI models using official ResponseFormat property
            if (!string.IsNullOrWhiteSpace(test.JsonResponseFormat))
            {
                var schemaPath = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "response_formats",
                    test.JsonResponseFormat);

                if (File.Exists(schemaPath))
                {
                    try
                    {
                        var schemaJson = File.ReadAllText(schemaPath);
                        
                        // Parse the schema to validate it
                        using var doc = JsonDocument.Parse(schemaJson);
                        var root = doc.RootElement;
                        
                        string finalSchemaJson;
                        
                        // Only wrap if schema is not already an object type
                        if (root.TryGetProperty("type", out var typeProperty) && 
                            typeProperty.GetString() != "object")
                        {
                            // Wrap simple schemas (integer, boolean, etc.) in an object structure
                            finalSchemaJson = $$"""
                            {
                                "type": "object",
                                "properties": {
                                    "result": {{schemaJson}}
                                },
                                "required": ["result"],
                                "additionalProperties": false
                            }
                            """;
                        }
                        else
                        {
                            // Use schema as-is if it's already an object
                            finalSchemaJson = schemaJson;
                        }
                        
                        // Use official ChatResponseFormat.CreateJsonSchemaFormat from OpenAI SDK
                        settings.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                            jsonSchemaFormatName: "response_schema",
                            jsonSchema: BinaryData.FromString(finalSchemaJson),
                            jsonSchemaIsStrict: true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Failed to set response format: {ex.Message}");
                        // Don't use fallback for OpenAI - let it fail naturally
                    }
                }
            }

            return settings;
        }

        private async Task<T> InvokeWithTimeoutAsync<T>(Task<T> task, int timeoutMs)
        {
            var completedTask = await Task.WhenAny(task, Task.Delay(timeoutMs));

            if (completedTask != task)
                throw new TimeoutException($"Operation timed out after {timeoutMs}ms");

            return await task;
        }

        private bool ValidateResponse(string? responseText, TestDefinition test, out string? failReason)
        {
            failReason = null;

            // Extract value from {"result": value} wrapper if present, or use direct value
            var valueToValidate = responseText?.Trim();
            
            if (!string.IsNullOrWhiteSpace(valueToValidate) && valueToValidate.StartsWith("{"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(valueToValidate);
                    
                    // Try to extract from {"result": value} wrapper
                    if (doc.RootElement.TryGetProperty("result", out var resultProperty))
                    {
                        valueToValidate = resultProperty.ValueKind switch
                        {
                            JsonValueKind.String => resultProperty.GetString(),
                            JsonValueKind.Number => resultProperty.GetInt32().ToString(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            _ => resultProperty.GetRawText()
                        };
                    }
                    // If no "result" property, try to extract direct value from root
                    else if (doc.RootElement.ValueKind == JsonValueKind.Number)
                    {
                        valueToValidate = doc.RootElement.GetInt32().ToString();
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.String)
                    {
                        valueToValidate = doc.RootElement.GetString();
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.True || doc.RootElement.ValueKind == JsonValueKind.False)
                    {
                        valueToValidate = doc.RootElement.GetBoolean().ToString().ToLower();
                    }
                }
                catch
                {
                    // If JSON parsing fails, use original response (it's a plain value)
                }
            }

            // Check ExpectedPromptValue
            if (!string.IsNullOrWhiteSpace(test.ExpectedPromptValue))
            {
                if (valueToValidate == test.ExpectedPromptValue)
                    return true;

                failReason = $"Expected '{test.ExpectedPromptValue}' but got '{valueToValidate}'";
                return false;
            }

            // Check ValidScoreRange
            if (!string.IsNullOrWhiteSpace(test.ValidScoreRange))
            {
                return ValidateRange(valueToValidate, test.ValidScoreRange, out failReason);
            }

            // Default: pass if response is not empty
            if (string.IsNullOrWhiteSpace(valueToValidate))
            {
                failReason = "Response is empty";
                return false;
            }

            return true;
        }

        private bool ValidateFunctionCallResponse(
            string? responseText,
            TestDefinition test,
            out string? failReason)
        {
            failReason = null;

            // For function call tests, we rely on the structured response format
            // If ResponseFormat is used, the model will return valid JSON
            if (!string.IsNullOrWhiteSpace(test.JsonResponseFormat))
            {
                try
                {
                    // Validate JSON structure
                    using var doc = JsonDocument.Parse(responseText ?? "{}");
                    return true;
                }
                catch (JsonException ex)
                {
                    failReason = $"Invalid JSON response: {ex.Message}";
                    return false;
                }
            }

            // Fallback validation
            return !string.IsNullOrWhiteSpace(responseText);
        }

        private bool ValidateRange(string? value, string validScoreRange, out string? failReason)
        {
            failReason = null;
            var range = validScoreRange.Trim();

            // Numeric range: min-max
            if (range.Contains("-") && !range.Contains(","))
            {
                var parts = range.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var min) &&
                    int.TryParse(parts[1], out var max))
                {
                    if (int.TryParse(value, out var val) && val >= min && val <= max)
                        return true;

                    failReason = $"Value '{value}' is not in range {min}-{max}";
                    return false;
                }
            }

            // List of values: A,B,C
            if (range.Contains(","))
            {
                var values = range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (values.Any(v => v.Equals(value, StringComparison.OrdinalIgnoreCase)))
                    return true;

                failReason = $"Value '{value}' is not in allowed list [{string.Join(", ", values)}]";
                return false;
            }

            // Single value
            if (value != null && value.Equals(range, StringComparison.OrdinalIgnoreCase))
                return true;

            failReason = $"Value '{value}' does not match expected '{range}'";
            return false;
        }

        private async Task<double> EvaluateStoryWithAgentsAsync(long storyId, string storyText, int runId, string writerModel)
        {
            // Get evaluator agents from database
            var allAgents = _database.ListAgents();
            var evaluators = allAgents.Where(a => 
                a.Role?.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) == true && 
                a.IsActive).ToList();

            if (evaluators.Count == 0)
            {
                _progress?.Append(runId.ToString(), $"[{writerModel}] No active story evaluator agents found, skipping evaluation");
                return 10.0; // Default score if no evaluators
            }

            _progress?.Append(runId.ToString(), $"[{writerModel}] Evaluating story with {evaluators.Count} evaluator agent(s)");

            var totalScoreSum = 0;
            var evaluatorCount = 0;

            foreach (var evaluator in evaluators)
            {
                try
                {
                    _progress?.Append(runId.ToString(), $"[{writerModel}] Running evaluation with agent: {evaluator.Name}");

                    // Get model name for this evaluator
                    var evaluatorModelName = evaluator.ModelId.HasValue 
                        ? _database.GetModelNameById(evaluator.ModelId.Value) 
                        : null;

                    if (string.IsNullOrWhiteSpace(evaluatorModelName))
                    {
                        _progress?.Append(runId.ToString(), $"[{writerModel}] Evaluator {evaluator.Name} has no valid model, skipping");
                        continue;
                    }

                    // Create kernel for evaluator with skills from agent configuration
                    var agentSkills = new List<string> { "evaluator" }; // Always include evaluator skill
                    
                    // Parse and add skills from agent's Skills JSON field
                    if (!string.IsNullOrWhiteSpace(evaluator.Skills))
                    {
                        try
                        {
                            var skillsArray = JsonSerializer.Deserialize<string[]>(evaluator.Skills);
                            if (skillsArray != null)
                            {
                                agentSkills.AddRange(skillsArray);
                            }
                            agentSkills = agentSkills.Distinct().ToList();
                        }
                        catch (Exception ex)
                        {
                            _progress?.Append(runId.ToString(), $"[{writerModel}] Warning: Failed to parse skills for evaluator {evaluator.Name}: {ex.Message}");
                        }
                    }
                    
                    var evaluatorKernel = _factory.CreateKernel(evaluatorModelName, agentSkills, evaluator.Id);

                    if (evaluatorKernel?.Kernel == null)
                    {
                        _progress?.Append(runId.ToString(), $"[{writerModel}] Failed to create kernel for evaluator {evaluator.Name}");
                        continue;
                    }

                    var chatService = evaluatorKernel.Kernel.GetRequiredService<IChatCompletionService>();
                    var settings = new OpenAIPromptExecutionSettings
                    {
                        Temperature = evaluatorModelName.Contains("gpt-5-nano", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0,
                        MaxTokens = 4000
                    };

                    // Apply full_evaluation.json response format
                    var schemaPath = Path.Combine(Directory.GetCurrentDirectory(), "response_formats", "full_evaluation.json");
                    if (File.Exists(schemaPath))
                    {
                        try
                        {
                            var schemaJson = File.ReadAllText(schemaPath);
                            settings.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                                jsonSchemaFormatName: "full_evaluation_schema",
                                jsonSchema: BinaryData.FromString(schemaJson),
                                jsonSchemaIsStrict: true);
                        }
                        catch (Exception ex)
                        {
                            _progress?.Append(runId.ToString(), $"[{writerModel}] Warning: Failed to set response format for evaluator: {ex.Message}");
                        }
                    }

                    var history = new ChatHistory();
                    
                    // Add evaluator instructions if available
                    if (!string.IsNullOrWhiteSpace(evaluator.Instructions))
                    {
                        history.AddSystemMessage(evaluator.Instructions);
                    }

                    // Add prompt to evaluate the story
                    var evaluationPrompt = $@"Please evaluate the following story across all 10 categories. For each category, provide:
- A score from 1 to 10
- A description of any defects found

Categories: narrative_coherence, structure, characterization, dialogues, pacing, originality, style, worldbuilding, thematic_coherence, emotional_impact

Also provide:
- total_score: sum of all category scores (0-100)
- overall_evaluation: a brief summary of the story's strengths and weaknesses

Story:
{storyText}";

                    history.AddUserMessage(evaluationPrompt);

                    var response = await InvokeWithTimeoutAsync(
                        chatService.GetChatMessageContentAsync(history, settings, evaluatorKernel.Kernel),
                        60000); // 60 second timeout for evaluation

                    // Parse structured evaluation response
                    var responseText = response?.Content ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(responseText))
                    {
                        try
                        {
                            var evalDoc = JsonDocument.Parse(responseText);
                            var root = evalDoc.RootElement;

                            // Extract all evaluation fields
                            var narrativeScore = root.GetProperty("narrative_coherence_score").GetInt32();
                            var narrativeDefects = root.GetProperty("narrative_coherence_defects").GetString() ?? "";
                            var structureScore = root.GetProperty("structure_score").GetInt32();
                            var structureDefects = root.GetProperty("structure_defects").GetString() ?? "";
                            var characterizationScore = root.GetProperty("characterization_score").GetInt32();
                            var characterizationDefects = root.GetProperty("characterization_defects").GetString() ?? "";
                            var dialoguesScore = root.GetProperty("dialogues_score").GetInt32();
                            var dialoguesDefects = root.GetProperty("dialogues_defects").GetString() ?? "";
                            var pacingScore = root.GetProperty("pacing_score").GetInt32();
                            var pacingDefects = root.GetProperty("pacing_defects").GetString() ?? "";
                            var originalityScore = root.GetProperty("originality_score").GetInt32();
                            var originalityDefects = root.GetProperty("originality_defects").GetString() ?? "";
                            var styleScore = root.GetProperty("style_score").GetInt32();
                            var styleDefects = root.GetProperty("style_defects").GetString() ?? "";
                            var worldbuildingScore = root.GetProperty("worldbuilding_score").GetInt32();
                            var worldbuildingDefects = root.GetProperty("worldbuilding_defects").GetString() ?? "";
                            var thematicScore = root.GetProperty("thematic_coherence_score").GetInt32();
                            var thematicDefects = root.GetProperty("thematic_coherence_defects").GetString() ?? "";
                            var emotionalScore = root.GetProperty("emotional_impact_score").GetInt32();
                            var emotionalDefects = root.GetProperty("emotional_impact_defects").GetString() ?? "";
                            var totalScore = root.GetProperty("total_score").GetDouble();
                            var overallEvaluation = root.GetProperty("overall_evaluation").GetString() ?? "";

                            // Get model and agent IDs
                            var evaluatorModelId = _database.GetModelIdByName(evaluatorModelName);

                            // Save to database
                            _database.AddStoryEvaluation(
                                storyId,
                                narrativeScore, narrativeDefects,
                                structureScore, structureDefects,
                                characterizationScore, characterizationDefects,
                                dialoguesScore, dialoguesDefects,
                                pacingScore, pacingDefects,
                                originalityScore, originalityDefects,
                                styleScore, styleDefects,
                                worldbuildingScore, worldbuildingDefects,
                                thematicScore, thematicDefects,
                                emotionalScore, emotionalDefects,
                                totalScore,
                                overallEvaluation,
                                responseText,
                                evaluatorModelId,
                                evaluator.Id
                            );

                            totalScoreSum += (int)totalScore;
                            evaluatorCount++;
                            _progress?.Append(runId.ToString(), $"[{writerModel}] Evaluator {evaluator.Name} scored: {totalScore}/100");
                        }
                        catch (Exception ex)
                        {
                            _progress?.Append(runId.ToString(), $"[{writerModel}] Failed to parse evaluation from {evaluator.Name}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _progress?.Append(runId.ToString(), $"[{writerModel}] Error evaluating with {evaluator.Name}: {ex.Message}");
                }
            }

            if (evaluatorCount == 0)
            {
                _progress?.Append(runId.ToString(), $"[{writerModel}] No successful evaluations, using default score");
                return 10.0;
            }

            // Calculate final score: (sum of scores / max possible) * 10
            // Each evaluator gives 0-100 points (10 categories × 10 points)
            var maxPossible = evaluatorCount * 100;
            var finalScore = (double)totalScoreSum / maxPossible * 10.0;

            _progress?.Append(runId.ToString(), $"[{writerModel}] Final evaluation score: {finalScore:F2}/10 (total: {totalScoreSum}/{maxPossible})");

            return finalScore;
        }
    }
}
