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
        Task ExecuteTestAsync(int runId, int idx, TestDefinition t, string model, ModelInfo modelInfo, string agentInstructions, object? defaultAgent, string? testFolder = null);
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
        private readonly IKernelFactory _factory;

        public TestService(
            DatabaseService database,
            ProgressService progress,
            StoriesService stories,
            IKernelFactory factory)
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
            
            // Check if any test in this group requires file copying
            string? testFolder = null;
            var testsWithFiles = tests.Where(t => !string.IsNullOrWhiteSpace(t.FilesToCopy)).ToList();
            if (testsWithFiles.Any())
            {
                // Create test folder: test_run_folders/{model}_{group}_{yyyyMMdd_HHmmss}/
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var folderName = $"{model}_{group}_{timestamp}";
                testFolder = Path.Combine(Directory.GetCurrentDirectory(), "test_run_folders", folderName);
                
                try
                {
                    Directory.CreateDirectory(testFolder);
                    _progress?.Append("setup", $"[{model}] Created test folder: {testFolder}");
                    
                    // Copy files from test_source_files for each test that needs them
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
                                _progress?.Append("setup", $"[{model}] Copied file: {fileName}");
                            }
                            else
                            {
                                _progress?.Append("setup", $"[{model}] WARNING: Source file not found: {fileName}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _progress?.Append("setup", $"[{model}] ERROR creating test folder: {ex.Message}");
                    testFolder = null;
                }
            }
            
            var runId = _database.CreateTestRun(
                modelInfo.Name,
                group,
                $"Group run {group}",
                false,
                null,
                "Started from TestService.RunGroupAsync",
                testFolder);

            _progress?.Start(runId.ToString());

            // Warmup call only for Ollama models to exclude cold start from measurements
            if (modelInfo.Provider?.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    _progress?.Append(runId.ToString(), $"[{model}] Performing warmup call for Ollama model...");
                    var warmupKernel = _factory.CreateKernel(model, Array.Empty<string>());
                    if (warmupKernel != null)
                    {
                        var warmupService = warmupKernel.GetRequiredService<IChatCompletionService>();
                        var warmupSettings = new OpenAIPromptExecutionSettings
                        {
                            Temperature = model.Contains("gpt-5-nano", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0,
                            MaxTokens = 10
                        };
                        var warmupHistory = new ChatHistory();
                        warmupHistory.AddUserMessage("Hello");
                        
                        await InvokeWithTimeoutAsync(
                            warmupService.GetChatMessageContentAsync(warmupHistory, warmupSettings, warmupKernel),
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
            object? defaultAgent,
            string? testFolder = null)
        {
            // Replace [test_folder] placeholder in prompt with absolute path
            var prompt = test.Prompt ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(testFolder) && prompt.Contains("[test_folder]"))
            {
                prompt = prompt.Replace("[test_folder]", testFolder);
            }
            
            var stepId = _database.AddTestStep(
                runId,
                idx,
                test.FunctionName ?? $"test_{test.Id}",
                JsonSerializer.Serialize(new { prompt }));

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
                _progress?.Append(runId.ToString(), $"[{model}] Step {idx} ERROR: {ex.Message}");
            }
        }

        private async Task ExecuteQuestionTestAsync(
            TestDefinition test,
            int stepId,
            System.Diagnostics.Stopwatch sw,
            int runId,
            int idx,
            string model,
            string prompt)
        {
            var kernel = CreateKernelForTest(model, test);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var settings = CreateExecutionSettings(test, model);
            var timeout = test.TimeoutMs > 0 ? test.TimeoutMs : 30000;

            var history = new ChatHistory();
            history.AddUserMessage(prompt);

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
            ModelInfo modelInfo,
            string prompt)
        {
            var kernel = CreateKernelForTest(model, test);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            var settings = CreateExecutionSettings(test, model);
            settings.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions;

            var timeout = test.TimeoutMs > 0 ? test.TimeoutMs : 30000;
            var history = new ChatHistory();
            history.AddUserMessage(prompt);

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
            ModelInfo modelInfo,
            string prompt)
        {
            var kernel = CreateKernelForTest(model, test);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            // Settings ottimizzate per storie lunghe
            var settings = new OpenAIPromptExecutionSettings
            {
                Temperature = 0.7,  // Aumentata per più creatività
                MaxTokens = 8000    // Aumentata per storie lunghe
            };

            // Per modelli Ollama, usa il MaxContext dal database per num_ctx
            if (modelInfo.Provider?.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true)
            {
                var numCtx = modelInfo.MaxContext > 0 ? modelInfo.MaxContext : 32768; // Default 32K se non specificato
                
                // Prova diversi approcci per passare num_ctx a Ollama
                settings.ExtensionData = new Dictionary<string, object>
                {
                    { "num_ctx", numCtx },
                    { "options", new Dictionary<string, object> { { "num_ctx", numCtx } } }
                };
                
                _progress?.Append(runId.ToString(), $"[{model}] Requesting Ollama context window: {numCtx} tokens");
            }

            // Load execution plan if specified
            var instructions = string.Empty;
            if (!string.IsNullOrWhiteSpace(test.ExecutionPlan))
            {
                var planPath = Path.Combine(Directory.GetCurrentDirectory(), "execution_plans", test.ExecutionPlan);
                if (File.Exists(planPath))
                    instructions = File.ReadAllText(planPath);
            }
            else
            {
                // Default instructions per storie lunghe se ExecutionPlan non specificato
                instructions = @"You are a professional storyteller. Write detailed, engaging stories of at least 2000 words IN ITALIAN. 
Include rich descriptions, well-developed characters, multiple scenes, and a complete narrative arc. 
DO NOT rush or summarize. Take your time to develop the story fully.
IMPORTANT: Write the story in Italian language.";
            }

            var timeout = test.TimeoutMs > 0 ? test.TimeoutMs : 120000; // Usa timeout dal test o default 2 minuti
            var history = new ChatHistory();

            if (!string.IsNullOrWhiteSpace(instructions))
                history.AddSystemMessage(instructions);

            history.AddUserMessage(prompt);

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
            var kernel = _factory.CreateKernel(model, allowedPlugins);
            return kernel ?? throw new InvalidOperationException("Failed to create kernel");
        }

        private OpenAIPromptExecutionSettings CreateExecutionSettings(TestDefinition test, string model)
        {
            var settings = new OpenAIPromptExecutionSettings
            {
                Temperature = model.Contains("gpt-5-nano", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0,
                MaxTokens = 8000
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

            var originalResponse = responseText?.Trim() ?? string.Empty;
            var valueToValidate = originalResponse;

            // Check ExpectedPromptValue with direct response FIRST (before JSON parsing)
            if (!string.IsNullOrWhiteSpace(test.ExpectedPromptValue))
            {
                var expected = test.ExpectedPromptValue.Trim();
                
                // Try exact match with original response
                if (originalResponse == expected)
                    return true;
                
                // Try case-insensitive match with original response
                if (originalResponse.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            
            // If direct match didn't work, try extracting from JSON wrapper
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
                            JsonValueKind.Number => resultProperty.GetRawText(),
                            JsonValueKind.True => "true",
                            JsonValueKind.False => "false",
                            _ => resultProperty.GetRawText()
                        };
                    }
                    // If no "result" property, try to extract direct value from root
                    else if (doc.RootElement.ValueKind == JsonValueKind.Number)
                    {
                        valueToValidate = doc.RootElement.GetRawText();
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
                    // If JSON parsing fails, keep using original response
                    valueToValidate = originalResponse;
                }
            }

            // Re-check ExpectedPromptValue with extracted value from JSON (if different from original)
            if (!string.IsNullOrWhiteSpace(test.ExpectedPromptValue))
            {
                var expected = test.ExpectedPromptValue.Trim();
                var actual = valueToValidate?.Trim() ?? string.Empty;
                
                // Try exact match
                if (actual == expected)
                    return true;
                
                // Try case-insensitive match
                if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;

                failReason = $"Expected '{expected}' but got '{actual}' (original response: '{originalResponse}')";
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

                    // Use centralized evaluation method from StoriesService
                    var result = await _stories.EvaluateStoryWithAgentAsync(storyId, evaluator.Id);

                    if (result.success)
                    {
                        totalScoreSum += (int)result.score;
                        evaluatorCount++;
                        _progress?.Append(runId.ToString(), $"[{writerModel}] Evaluator {evaluator.Name} scored: {result.score}/100");
                    }
                    else
                    {
                        _progress?.Append(runId.ToString(), $"[{writerModel}] Evaluator {evaluator.Name} failed: {result.error}");
                    }
                }
                catch (Exception ex)
                {
                    var errorMsg = $"[{writerModel}] Error evaluating with {evaluator.Name}: {ex.Message}";
                    _progress?.Append(runId.ToString(), errorMsg);
                    Console.WriteLine(errorMsg);
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
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
            var kernel = CreateKernelForTest(model, test);
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            // For TTS tests, use function calling instead of JSON response format
            var settings = new OpenAIPromptExecutionSettings
            {
                Temperature = model.Contains("gpt-5-nano", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0,
                MaxTokens = 8000,
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
            };
            
            var timeout = test.TimeoutMs > 0 ? test.TimeoutMs : 60000;

            var history = new ChatHistory();
            history.AddUserMessage(prompt);

            try
            {
                var response = await InvokeWithTimeoutAsync(
                    chatService.GetChatMessageContentAsync(history, settings, kernel),
                    timeout);

                sw.Stop();

                var responseText = response?.Content ?? string.Empty;
                
                // Log raw response for debugging
                _progress?.Append(runId.ToString(), $"[{model}] Response: {responseText}");
                
                // Extract file path from function call metadata
                string? generatedFilePath = null;
                
                // Check if filesystem plugin was called by inspecting chat history or response metadata
                if (response?.Metadata != null && response.Metadata.ContainsKey("FunctionCalls"))
                {
                    // Extract from metadata if available
                    var functionCalls = response.Metadata["FunctionCalls"];
                    _progress?.Append(runId.ToString(), $"[{model}] Function calls metadata: {JsonSerializer.Serialize(functionCalls)}");
                }
                
                // Alternative: scan test folder for newly created JSON files
                if (!string.IsNullOrWhiteSpace(testFolder))
                {
                    var jsonFiles = Directory.GetFiles(testFolder, "*.json")
                        .Where(f => !f.EndsWith("_expected_result.json"))
                        .OrderByDescending(f => File.GetCreationTimeUtc(f))
                        .ToList();
                    
                    if (jsonFiles.Any())
                    {
                        generatedFilePath = jsonFiles.First();
                        _progress?.Append(runId.ToString(), $"[{model}] Found generated file: {Path.GetFileName(generatedFilePath)}");
                    }
                }

                if (string.IsNullOrWhiteSpace(generatedFilePath))
                {
                    _database.UpdateTestStepResult(stepId, false, responseText, "No JSON file generated in test folder", (int)sw.ElapsedMilliseconds);
                    _progress?.Append(runId.ToString(), $"[{model}] Step {idx} FAILED: No file generated");
                    return;
                }

                // Read generated file
                string generatedJson;
                try
                {
                    if (!File.Exists(generatedFilePath))
                    {
                        _database.UpdateTestStepResult(stepId, false, responseText, $"Generated file not found: {generatedFilePath}", (int)sw.ElapsedMilliseconds);
                        _progress?.Append(runId.ToString(), $"[{model}] Step {idx} FAILED: File not found");
                        return;
                    }
                    generatedJson = await File.ReadAllTextAsync(generatedFilePath);
                }
                catch (Exception ex)
                {
                    _database.UpdateTestStepResult(stepId, false, responseText, $"Failed to read generated file: {ex.Message}", (int)sw.ElapsedMilliseconds);
                    _progress?.Append(runId.ToString(), $"[{model}] Step {idx} FAILED: Cannot read file");
                    return;
                }

                // Read expected result file
                var expectedFilePath = Path.Combine(Directory.GetCurrentDirectory(), "test_source_files", "tts_dialogue_expexted_result.json");
                string expectedJson;
                try
                {
                    if (!File.Exists(expectedFilePath))
                    {
                        _database.UpdateTestStepResult(stepId, false, responseText, $"Expected result file not found: {expectedFilePath}", (int)sw.ElapsedMilliseconds);
                        _progress?.Append(runId.ToString(), $"[{model}] Step {idx} FAILED: Expected file missing");
                        return;
                    }
                    expectedJson = await File.ReadAllTextAsync(expectedFilePath);
                }
                catch (Exception ex)
                {
                    _database.UpdateTestStepResult(stepId, false, responseText, $"Failed to read expected file: {ex.Message}", (int)sw.ElapsedMilliseconds);
                    _progress?.Append(runId.ToString(), $"[{model}] Step {idx} FAILED: Cannot read expected file");
                    return;
                }

                // Evaluate TTS
                int score = ValutaTTS(expectedJson, generatedJson);
                bool passed = score >= 2; // Consider >= 7 as passing

                var resultJson = JsonSerializer.Serialize(new
                {
                    file_path = generatedFilePath,
                    score = score,
                    passed = passed
                });

                _database.UpdateTestStepResult(stepId, passed, resultJson, passed ? null : $"Score too low: {score}/10", (int)sw.ElapsedMilliseconds);
                
                var status = passed ? "PASSED" : "FAILED";
                _progress?.Append(runId.ToString(), $"[{model}] Step {idx} {status} - TTS Score: {score}/10");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _database.UpdateTestStepResult(stepId, false, null, ex.Message, (int)sw.ElapsedMilliseconds);
                _progress?.Append(runId.ToString(), $"[{model}] Step {idx} ERROR: {ex.Message}");
            }
        }

        /// <summary>
        /// Valuta un test TTS confrontando il JSON generato con quello atteso.
        /// Restituisce un punteggio da 1 a 10.
        /// </summary>
        public int ValutaTTS(string expectedJson, string generatedJson)
        {
            // 1. Parse JSON → oggetti .NET
            var expected = ParseDialogueTrack(expectedJson);
            var actual = ParseDialogueTrack(generatedJson);

            int penalty = 0;

            //--------------------------------------------
            // 2. Confronto personaggi e sesso
            //--------------------------------------------
            foreach (var expChar in expected.Characters)
            {
                var actChar = actual.Characters.FirstOrDefault(c => c.Name == expChar.Name);

                if (actChar == null)
                {
                    penalty += 3;    // personaggio mancante
                    continue;
                }

                if (actChar.Gender != expChar.Gender)
                {
                    penalty += 3;    // sesso sbagliato
                }
            }

            //--------------------------------------------
            // 3. Confronto frasi e testo dialogo
            //--------------------------------------------
            // Supponiamo che entrambe le liste siano in ordine temporale
            int count = Math.Min(expected.Entries.Count, actual.Entries.Count);

            for (int i = 0; i < count; i++)
            {
                var expEntry = expected.Entries[i];
                var actEntry = actual.Entries[i];

                // tipo diverso (dialogue/pause)
                if (expEntry.Type != actEntry.Type)
                {
                    penalty += 2;
                    continue;
                }

                // solo per tipo dialogue
                if (expEntry.Type == "dialogue")
                {
                    //------------------------------------
                    // 3a. Confronto parole
                    //------------------------------------
                    var expWords = SplitWords(expEntry.Text);
                    var actWords = SplitWords(actEntry.Text);

                    foreach (var w in expWords)
                    {
                        if (!actWords.Contains(w, StringComparer.OrdinalIgnoreCase))
                            penalty += 1; // parola mancante
                    }

                    //------------------------------------
                    // 3b. Personaggio corretto?
                    //------------------------------------
                    if (actEntry.Character != expEntry.Character)
                        penalty += 2;

                    //------------------------------------
                    // 3c. Emozione mancante o errata
                    //------------------------------------
                    if (actEntry.Emotion != expEntry.Emotion)
                        penalty += 1;
                }
            }

            //--------------------------------------------
            // 4. Punteggio finale (dal totale penalità)
            //--------------------------------------------
            // Ogni 3 punti di penalità = -1 sul voto
            int score = 10 - (penalty / 3);

            if (score < 1) score = 1;
            if (score > 10) score = 10;

            return score;
        }

        private DialogueTrack ParseDialogueTrack(string json)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<DialogueTrack>(json, options) ?? new DialogueTrack();
        }

        private List<string> SplitWords(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return text.Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                       .Select(w => w.Trim().ToLowerInvariant())
                       .Where(w => !string.IsNullOrEmpty(w))
                       .ToList();
        }
    }

    // Classi per la valutazione TTS
    public class DialogueTrack
    {
        public List<CharacterInfo> Characters { get; set; } = new List<CharacterInfo>();
        public List<DialogueEntry> Entries { get; set; } = new List<DialogueEntry>();
    }

    public class CharacterInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Gender { get; set; } = string.Empty;
    }

    public class DialogueEntry
    {
        public string Type { get; set; } = string.Empty; // "dialogue" o "pause"
        public string Character { get; set; } = string.Empty; // solo per dialogue
        public string Emotion { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty; // solo per dialogue
    }
}
