using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    public class MultiStepOrchestrationService
    {
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly DatabaseService _database;
        private readonly ResponseCheckerService _checkerService;
        private readonly ITokenizer _tokenizerService;
        private readonly ICustomLogger _logger;

        public MultiStepOrchestrationService(
            ILangChainKernelFactory kernelFactory,
            DatabaseService database,
            ResponseCheckerService checkerService,
            ITokenizer tokenizerService,
            ICustomLogger logger)
        {
            _kernelFactory = kernelFactory;
            _database = database;
            _checkerService = checkerService;
            _tokenizerService = tokenizerService;
            _logger = logger;
        }

        public async Task<long> StartTaskExecutionAsync(
            string taskType,
            long? entityId,
            string stepPrompt,
            int? executorAgentId = null,
            int? checkerAgentId = null,
            string? configOverrides = null,
            string? initialContext = null,
            int threadId = 0)
        {
            _logger.Log("Information", "MultiStep", "Starting task execution");

            // Check for active execution lock
            var activeExecution = _database.GetActiveExecutionForEntity(entityId, taskType);
            if (activeExecution != null)
            {
                throw new InvalidOperationException($"An active execution (id={activeExecution.Id}) already exists for entity {entityId} of type {taskType}");
            }

            // Get task type info
            var taskTypeInfo = _database.GetTaskTypeByCode(taskType);
            if (taskTypeInfo == null)
            {
                throw new ArgumentException($"Task type '{taskType}' not found in database");
            }

            // Parse and renumber steps
            var steps = ParseAndRenumberSteps(stepPrompt, threadId);
            if (steps.Count == 0)
            {
                throw new ArgumentException("Step prompt contains no valid steps");
            }

            // Create execution record
            var execution = new TaskExecution
            {
                TaskType = taskType,
                EntityId = entityId,
                StepPrompt = stepPrompt,
                InitialContext = initialContext,
                CurrentStep = 1,
                MaxStep = steps.Count,
                RetryCount = 0,
                Status = "pending",
                ExecutorAgentId = executorAgentId,
                CheckerAgentId = checkerAgentId,
                Config = configOverrides,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                UpdatedAt = DateTime.UtcNow.ToString("o")
            };

            var executionId = _database.CreateTaskExecution(execution);
            _logger.Log($"Created task execution id={executionId}, max_step={execution.MaxStep}", "MultiStep", "Information");

            return executionId;
        }

        private List<string> ParseAndRenumberSteps(string stepPrompt, int threadId)
        {
            var lines = stepPrompt.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var steps = new List<string>();
            var stepPattern = new Regex(@"^\s*\d+\.\s*(.+)$", RegexOptions.Compiled);

            foreach (var line in lines)
            {
                var match = stepPattern.Match(line.Trim());
                if (match.Success)
                {
                    steps.Add(match.Groups[1].Value.Trim());
                }
            }

            // Check for gaps in original numbering
            if (steps.Count > 0)
            {
                var originalNumbers = new List<int>();
                foreach (var line in lines)
                {
                    var match = Regex.Match(line.Trim(), @"^(\d+)\.");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var num))
                    {
                        originalNumbers.Add(num);
                    }
                }

                for (int i = 1; i < originalNumbers.Count; i++)
                {
                    if (originalNumbers[i] != originalNumbers[i - 1] + 1)
                    {
                        _logger.Log($"Warning: Step numbering has gaps (found {originalNumbers[i - 1]} then {originalNumbers[i]}). Steps will be renumbered sequentially.", 
                            "MultiStep", "Warning");
                        break;
                    }
                }
            }

            return steps;
        }

        public async Task ExecuteAllStepsAsync(
            long executionId,
            int threadId,
            Action<string, int, int, string>? progressCallback = null,
            CancellationToken ct = default)
        {
            Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync START - executionId={executionId}, threadId={threadId}");
            _logger.Log("Information", "MultiStep", $"[START] ExecuteAllStepsAsync for execution {executionId}, threadId={threadId}");
            if (_logger is CustomLogger customLogger)
            {
                await customLogger.FlushAsync();
            }
            
            Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - Getting execution from DB");
            var execution = _database.GetTaskExecutionById(executionId);
            if (execution == null)
            {
                Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - Execution NOT FOUND");
                _logger.Log("Error", "MultiStep", $"Task execution {executionId} not found in database");
                if (_logger is CustomLogger cl) await cl.FlushAsync();
                throw new ArgumentException($"Task execution {executionId} not found");
            }

            Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - Execution found: status={execution.Status}, current_step={execution.CurrentStep}, max_step={execution.MaxStep}");
            _logger.Log("Information", "MultiStep", $"Executing all steps for execution {executionId}, current_step={execution.CurrentStep}, max_step={execution.MaxStep}, status={execution.Status}");
            if (_logger is CustomLogger cl2) await cl2.FlushAsync();

            // Update status to in_progress
            Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - Updating status to in_progress");
            execution.Status = "in_progress";
            execution.UpdatedAt = DateTime.UtcNow.ToString("o");
            _database.UpdateTaskExecution(execution);
            Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - Status updated to in_progress");

            try
            {
                Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - Entering while loop, current_step={execution.CurrentStep}, max_step={execution.MaxStep}");
                while (execution.CurrentStep <= execution.MaxStep && execution.Status == "in_progress")
                {
                    if (ct.IsCancellationRequested)
                    {
                        Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - Cancellation requested");
                        _logger.Log("Warning", "MultiStep", "Execution cancelled by token");
                        execution.Status = "paused";
                        execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                        _database.UpdateTaskExecution(execution);
                        break;
                    }

                    Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - Processing step {execution.CurrentStep}/{execution.MaxStep}");
                    var stepInstruction = GetStepInstruction(execution.StepPrompt, execution.CurrentStep);
                    Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - Step instruction length: {stepInstruction?.Length ?? 0}");
                    progressCallback?.Invoke($"Execution:{executionId}", execution.CurrentStep, execution.MaxStep, stepInstruction ?? string.Empty);

                    Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - About to call ExecuteNextStepAsync");
                    await ExecuteNextStepAsync(executionId, threadId, ct);
                    Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - ExecuteNextStepAsync completed");

                    // Reload execution to get updated state
                    execution = _database.GetTaskExecutionById(executionId);
                    if (execution == null) break;
                }

                // If completed all steps successfully, finalize
                if (execution != null && execution.Status == "in_progress" && execution.CurrentStep > execution.MaxStep)
                {
                    await CompleteExecutionAsync(executionId, threadId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - EXCEPTION: Type={ex.GetType().Name}, Message={ex.Message}");
                Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - StackTrace: {ex.StackTrace}");
                _logger.Log("Error", "MultiStep", $"ExecuteAllStepsAsync error: {ex.Message}", ex.ToString());
                
                if (execution != null)
                {
                    execution.Status = "failed";
                    execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                    _database.UpdateTaskExecution(execution);
                }
                
                throw;
            }

            _logger.Log($"All steps completed for execution {executionId}, final status: {execution?.Status}", "MultiStep", "Information");
        }

        private string GetStepInstruction(string stepPrompt, int stepNumber)
        {
            var steps = ParseAndRenumberSteps(stepPrompt, 0);
            if (stepNumber < 1 || stepNumber > steps.Count)
            {
                return string.Empty;
            }
            return steps[stepNumber - 1];
        }

        public async Task<TaskExecutionStep> ExecuteNextStepAsync(
            long executionId,
            int threadId,
            CancellationToken ct = default)
        {
            var execution = _database.GetTaskExecutionById(executionId);
            if (execution == null)
            {
                throw new ArgumentException($"Task execution {executionId} not found");
            }

            if (execution.Status != "in_progress")
            {
                throw new InvalidOperationException($"Execution {executionId} is not in progress (status: {execution.Status})");
            }

            _logger.Log("Information", "MultiStep", $"Executing step {execution.CurrentStep}/{execution.MaxStep}");

            // Get step instruction
            var stepInstruction = GetStepInstruction(execution.StepPrompt, execution.CurrentStep);
            if (string.IsNullOrEmpty(stepInstruction))
            {
                throw new InvalidOperationException($"Could not extract step instruction for step {execution.CurrentStep}");
            }

            // Get previous steps for context
            var previousSteps = _database.GetTaskExecutionSteps(executionId);

            // Build context from placeholders
            var context = await BuildStepContextAsync(execution.CurrentStep, stepInstruction, previousSteps, threadId, ct);

            // If this is a retry (RetryCount > 0), prepend validation failure feedback
            if (execution.RetryCount > 0)
            {
                var lastAttempt = previousSteps
                    .Where(s => s.StepNumber == execution.CurrentStep)
                    .OrderByDescending(s => s.AttemptCount)
                    .FirstOrDefault();

                if (lastAttempt?.ParsedValidation != null && !lastAttempt.ParsedValidation.IsValid)
                {
                    var feedbackMessage = $@"**ATTENZIONE - RETRY {execution.RetryCount}/3**

Il tentativo precedente è stato respinto per il seguente motivo:
{lastAttempt.ParsedValidation.Reason}

Output precedente che è stato respinto:
---
{lastAttempt.StepOutput}
---

Correggi l'output tenendo conto del feedback ricevuto.

";
                    context = string.IsNullOrEmpty(context) ? feedbackMessage : $"{feedbackMessage}\n{context}";
                }
            }

            // Measure token count
            int contextTokens = 0;
            try
            {
                contextTokens = _tokenizerService.CountTokens(context);
                if (contextTokens > 10000)
                {
                    _logger.Log($"Context size ({contextTokens} tokens) exceeds 10k, summarizing...", "MultiStep", "Warning");
                    context = await SummarizeContextAsync(context, threadId, ct);
                    contextTokens = _tokenizerService.CountTokens(context);
                    _logger.Log("Information", "MultiStep", $"Context summarized to {contextTokens} tokens");
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Token counting failed: {ex.Message}, proceeding without summary", "MultiStep", "Warning");
            }

            // Get executor agent
            var executorAgent = await GetExecutorAgentAsync(execution, threadId);

            // Check if stepInstruction contains {{PROMPT}} tag
            string fullPrompt;
            string systemMessage = executorAgent.Instructions ?? string.Empty;

            if (stepInstruction.Contains("{{PROMPT}}") && !string.IsNullOrWhiteSpace(execution.InitialContext))
            {
                // Replace {{PROMPT}} tag with initial context
                var stepWithPrompt = stepInstruction.Replace("{{PROMPT}}", execution.InitialContext);
                fullPrompt = string.IsNullOrEmpty(context)
                    ? stepWithPrompt
                    : $"{context}\n\n---\n\n{stepWithPrompt}";
            }
            else
            {
                // Original behavior: include initial context in system message for step 1
                fullPrompt = string.IsNullOrEmpty(context)
                    ? stepInstruction
                    : $"{context}\n\n---\n\n{stepInstruction}";

                if (execution.CurrentStep == 1 && !string.IsNullOrWhiteSpace(execution.InitialContext))
                {
                    systemMessage = string.IsNullOrEmpty(systemMessage)
                        ? $"**CONTESTO DELLA STORIA**:\n{execution.InitialContext}"
                        : $"{systemMessage}\n\n**CONTESTO DELLA STORIA**:\n{execution.InitialContext}";
                }
            }

            // Create timeout for this step (5 minutes)
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stepCts.CancelAfter(TimeSpan.FromMinutes(5));

            _logger.Log("Information", "MultiStep", $"Invoking executor agent: {executorAgent.Name} (model: {executorAgent.ModelName})");
            if (_logger is CustomLogger cl3) await cl3.FlushAsync();

            // Invoke executor agent
            string output;
            try
            {
                _logger.Log("Information", "MultiStep", $"Getting model info for executor agent, ModelId={executorAgent.ModelId}");
                if (_logger is CustomLogger cl4) await cl4.FlushAsync();
                
                var executorAgentModelInfo = _database.GetModelInfoById(executorAgent.ModelId ?? 0);
                var executorModelName = executorAgentModelInfo?.Name ?? "phi3:mini";
                
                _logger.Log("Information", "MultiStep", $"Creating orchestrator for model: {executorModelName}, skills: {executorAgent.Skills}");
                if (_logger is CustomLogger cl5) await cl5.FlushAsync();
                
                var orchestrator = _kernelFactory.CreateOrchestrator(executorModelName, ParseSkills(executorAgent.Skills));
                var bridge = _kernelFactory.CreateChatBridge(executorModelName);
                var loop = new ReActLoopOrchestrator(
                    orchestrator, 
                    _logger, 
                    maxIterations: 100, 
                    progress: null, 
                    runId: null, 
                    modelBridge: bridge,
                    systemMessage: systemMessage); // Use combined system message with context

                _logger.Log("Information", "MultiStep", $"About to execute ReAct loop - systemMessage length: {systemMessage?.Length ?? 0}, prompt length: {fullPrompt?.Length ?? 0}");
                if (_logger is CustomLogger cl6) await cl6.FlushAsync();
                
                Console.WriteLine($"[DEBUG] ExecuteNextStepAsync - Calling loop.ExecuteAsync with prompt length: {fullPrompt?.Length ?? 0}");
                var result = await loop.ExecuteAsync(fullPrompt ?? string.Empty, stepCts.Token);
                Console.WriteLine($"[DEBUG] ExecuteNextStepAsync - loop.ExecuteAsync completed");
                Console.WriteLine($"[DEBUG] ExecuteNextStepAsync - result.FinalResponse is null: {result.FinalResponse == null}");
                Console.WriteLine($"[DEBUG] ExecuteNextStepAsync - result.FinalResponse length: {result.FinalResponse?.Length ?? 0}");
                if (!string.IsNullOrWhiteSpace(result.FinalResponse))
                {
                    Console.WriteLine($"[DEBUG] ExecuteNextStepAsync - result.FinalResponse preview: {result.FinalResponse.Substring(0, Math.Min(200, result.FinalResponse.Length))}");
                }
                output = result.FinalResponse ?? string.Empty;

                if (string.IsNullOrWhiteSpace(output))
                {
                    Console.WriteLine($"[DEBUG] ExecuteNextStepAsync - Output is empty! result object: {result != null}, FinalResponse: '{result?.FinalResponse ?? "(null)"}'");
                    throw new InvalidOperationException("Executor agent produced empty output");
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Log("Error", "MultiStep", "Step execution timeout (5 minutes)");
                throw new TimeoutException("Step execution exceeded 5 minute timeout");
            }

            // Validate output with checker
            var taskTypeInfo = _database.GetTaskTypeByCode(execution.TaskType);
            var validationCriteria = taskTypeInfo?.ParsedValidationCriteria;

            var validationResult = await _checkerService.ValidateStepOutputAsync(
                stepInstruction,
                output,
                validationCriteria,
                threadId,
                executorAgent.Name,
                executorAgent.ModelName
            );

            if (validationResult.IsValid)
            {
                // Step valid - save and advance
                var step = new TaskExecutionStep
                {
                    ExecutionId = executionId,
                    StepNumber = execution.CurrentStep,
                    StepInstruction = stepInstruction,
                    StepOutput = output,
                    AttemptCount = execution.RetryCount + 1,
                    StartedAt = DateTime.UtcNow.ToString("o"),
                    CompletedAt = DateTime.UtcNow.ToString("o")
                };
                step.ParsedValidation = validationResult;

                _database.CreateTaskExecutionStep(step);
                _logger.Log("Information", "MultiStep", $"Step {execution.CurrentStep} completed successfully");

                // Advance to next step
                execution.CurrentStep++;
                execution.RetryCount = 0;
                execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                _database.UpdateTaskExecution(execution);

                return step;
            }
            else
            {
                // Step invalid - save failed attempt for feedback, then retry
                var failedStep = new TaskExecutionStep
                {
                    ExecutionId = executionId,
                    StepNumber = execution.CurrentStep,
                    StepInstruction = stepInstruction,
                    StepOutput = output,
                    AttemptCount = execution.RetryCount + 1,
                    StartedAt = DateTime.UtcNow.ToString("o"),
                    CompletedAt = DateTime.UtcNow.ToString("o")
                };
                failedStep.ParsedValidation = validationResult;
                _database.CreateTaskExecutionStep(failedStep);
                
                _logger.Log($"Step validation failed (retry {execution.RetryCount + 1}/3): {validationResult.Reason}", 
                    "MultiStep", "Warning");

                execution.RetryCount++;
                execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                _database.UpdateTaskExecution(execution);

                if (execution.RetryCount <= 2)
                {
                    // Retry same step with feedback
                    return await ExecuteNextStepAsync(executionId, threadId, ct);
                }
                else
                {
                    // Max retries exceeded - try fallback model
                    _logger.Log("Warning", "MultiStep", "Max retries exceeded, attempting fallback model");
                    
                    var fallbackSuccess = await TryFallbackModelAsync(execution, stepInstruction, context, threadId, ct);
                    
                    if (!fallbackSuccess)
                    {
                        execution.Status = "failed";
                        execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                        _database.UpdateTaskExecution(execution);

                        _logger.Log("Error", "MultiStep", $"Step {execution.CurrentStep} failed after retries and fallback");
                        throw new InvalidOperationException($"Step {execution.CurrentStep} failed validation after {execution.RetryCount} retries and fallback attempt");
                    }

                    // Fallback succeeded - reload and return
                    var step = _database.GetTaskExecutionSteps(executionId).LastOrDefault();
                    return step ?? throw new InvalidOperationException("Fallback succeeded but no step found");
                }
            }
        }

        private async Task<Agent> GetExecutorAgentAsync(TaskExecution execution, int threadId)
        {
            Agent? agent = null;

            if (execution.ExecutorAgentId.HasValue)
            {
                agent = _database.GetAgentById(execution.ExecutorAgentId.Value);
                if (agent == null || !agent.IsActive)
                {
                    _logger.Log($"Executor agent {execution.ExecutorAgentId} not found or inactive, falling back to role",
                        "MultiStep", "Warning");
                    agent = null;
                }
            }

            if (agent == null)
            {
                // Fallback to first active agent with correct role
                var taskTypeInfo = _database.GetTaskTypeByCode(execution.TaskType);
                if (taskTypeInfo == null)
                {
                    throw new InvalidOperationException($"Task type {execution.TaskType} not found");
                }

                agent = _database.ListAgents()
                    .FirstOrDefault(a => a.Role == taskTypeInfo.DefaultExecutorRole && a.IsActive);

                if (agent == null)
                {
                    throw new InvalidOperationException($"No active agent with role '{taskTypeInfo.DefaultExecutorRole}' found");
                }
            }

            return agent;
        }

        private List<string> ParseSkills(string? skillsJson)
        {
            if (string.IsNullOrWhiteSpace(skillsJson)) return new List<string>();

            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<string>>(skillsJson) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private async Task<string> BuildStepContextAsync(
            int currentStep,
            string instruction,
            List<TaskExecutionStep> previousSteps,
            int threadId,
            CancellationToken ct)
        {
            // Parse placeholders: {{STEP_N}}, {{STEP_N_SUMMARY}}, {{STEP_N_EXTRACT:filter}}, {{STEPS_N-M_SUMMARY}}
            var placeholderPattern = @"\{\{STEP(?:S)?_(\d+)(?:-(\d+))?(?:_(SUMMARY|EXTRACT):(.+?))?\}\}";
            var matches = Regex.Matches(instruction, placeholderPattern);

            if (matches.Count == 0)
            {
                // No placeholders - return empty context
                return string.Empty;
            }

            var context = new StringBuilder();

            foreach (Match match in matches)
            {
                int stepFrom = int.Parse(match.Groups[1].Value);
                int stepTo = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : stepFrom;
                string mode = match.Groups[3].Value; // SUMMARY, EXTRACT, or empty
                string filter = match.Groups[4].Value;

                var targetSteps = previousSteps.Where(s => s.StepNumber >= stepFrom && s.StepNumber <= stepTo).ToList();

                if (!targetSteps.Any()) continue;

                string content;
                if (mode == "SUMMARY")
                {
                    content = await GenerateSummaryAsync(targetSteps, threadId, ct);
                }
                else if (mode == "EXTRACT")
                {
                    content = ExtractContent(targetSteps.First().StepOutput ?? string.Empty, filter);
                }
                else
                {
                    content = string.Join("\n\n", targetSteps.Select(s => s.StepOutput));
                }

                string label = stepFrom == stepTo ? $"Step {stepFrom}" : $"Steps {stepFrom}-{stepTo}";
                context.AppendLine($"=== {label} ===");
                context.AppendLine(content);
                context.AppendLine();
            }

            return context.ToString();
        }

        private async Task<string> GenerateSummaryAsync(List<TaskExecutionStep> steps, int threadId, CancellationToken ct)
        {
            var text = string.Join("\n\n", steps.Select(s => s.StepOutput));
            
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            // Find fast summary model (qwen2.5:3b or phi3:mini)
            var summaryModel = _database.ListModels()
                .FirstOrDefault(m => m.Name.Contains("qwen2.5:3b") || m.Name.Contains("phi3:mini"));

            if (summaryModel == null)
            {
                _logger.Log("Warning", "MultiStep", "No summary model found, returning full text");
                return text; // Fallback: full text
            }

            _logger.Log("Information", "MultiStep", $"Summarizing context using {summaryModel.Name}");

            try
            {
                var prompt = $@"Riassumi il seguente testo in modo conciso (max 500 parole):

{text}

RIASSUNTO:";

                var summaryModelName = summaryModel.Name;
                var orchestrator = _kernelFactory.CreateOrchestrator(summaryModelName, new List<string>());
                var loop = new ReActLoopOrchestrator(orchestrator, _logger);
                var response = await loop.ExecuteAsync(prompt, ct);
                return response.FinalResponse ?? text;
            }
            catch (Exception ex)
            {
                _logger.Log($"Summary generation failed: {ex.Message}, returning full text", "MultiStep", "Warning");
                return text;
            }
        }

        private string ExtractContent(string output, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return output;

            var lines = output.Split('\n');
            var extracting = false;
            var result = new StringBuilder();

            foreach (var line in lines)
            {
                if (line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    extracting = true;
                }
                else if (extracting && Regex.IsMatch(line, @"^(Capitolo|Chapter|\d+\.)", RegexOptions.IgnoreCase))
                {
                    // Stop when encountering another chapter/section
                    break;
                }

                if (extracting)
                {
                    result.AppendLine(line);
                }
            }

            return result.Length > 0 ? result.ToString() : output;
        }

        private async Task<string> SummarizeContextAsync(string context, int threadId, CancellationToken ct)
        {
            _logger.Log("Information", "MultiStep", "Summarizing context (>10k tokens)");

            var summaryModel = _database.ListModels()
                .FirstOrDefault(m => m.Name.Contains("qwen2.5:3b") || m.Name.Contains("phi3:mini"));

            if (summaryModel == null) return context; // Fallback

            try
            {
                var prompt = $@"Riassumi questo contesto in max 500 parole mantenendo le informazioni chiave:

{context}

RIASSUNTO:";

                var summaryModelName = summaryModel.Name;
                var orchestrator = _kernelFactory.CreateOrchestrator(summaryModelName, new List<string>());
                var bridge = _kernelFactory.CreateChatBridge(summaryModelName);
                var loop = new ReActLoopOrchestrator(orchestrator, _logger, modelBridge: bridge);
                var response = await loop.ExecuteAsync(prompt, ct);
                return response.FinalResponse ?? context;
            }
            catch
            {
                return context;
            }
        }

        private async Task<bool> TryFallbackModelAsync(
            TaskExecution execution,
            string stepInstruction,
            string context,
            int threadId,
            CancellationToken ct)
        {
            _logger.Log("Information", "MultiStep", "Attempting fallback model");

            var currentAgent = await GetExecutorAgentAsync(execution, threadId);
            var currentModel = _database.GetModelInfoById(currentAgent.ModelId ?? 0);

            if (currentModel == null) return false;

            // Find better model
            var fallbackModel = _database.ListModels()
                .Where(m => m.WriterScore > currentModel.WriterScore && m.Enabled)
                .OrderByDescending(m => m.WriterScore)
                .FirstOrDefault();

            if (fallbackModel == null)
            {
                _logger.Log("Warning", "MultiStep", "No fallback model available");
                return false;
            }

            _logger.Log($"Fallback to model: {fallbackModel.Name} (WriterScore: {fallbackModel.WriterScore})", 
                "MultiStep", "Information");

            try
            {
                var fullPrompt = string.IsNullOrEmpty(context) ? stepInstruction : $"{context}\n\n---\n\n{stepInstruction}";
                var fallbackModelName = fallbackModel.Name;
                var orchestrator = _kernelFactory.CreateOrchestrator(fallbackModelName, new List<string>());
                var bridge = _kernelFactory.CreateChatBridge(fallbackModelName);
                var loop = new ReActLoopOrchestrator(orchestrator, _logger, modelBridge: bridge);

                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepCts.CancelAfter(TimeSpan.FromMinutes(5));

                var result = await loop.ExecuteAsync(fullPrompt, stepCts.Token);
                var output = result.FinalResponse ?? string.Empty;

                if (string.IsNullOrWhiteSpace(output)) return false;

                // Validate fallback output
                var taskTypeInfo = _database.GetTaskTypeByCode(execution.TaskType);
                var validationResult = await _checkerService.ValidateStepOutputAsync(
                    stepInstruction,
                    output,
                    taskTypeInfo?.ParsedValidationCriteria,
                    threadId,
                    $"{currentAgent.Name} (fallback: {fallbackModel.Name})",
                    fallbackModel.Name
                );

                if (validationResult.IsValid)
                {
                    // Save successful fallback step
                    var step = new TaskExecutionStep
                    {
                        ExecutionId = execution.Id,
                        StepNumber = execution.CurrentStep,
                        StepInstruction = stepInstruction,
                        StepOutput = output,
                        AttemptCount = execution.RetryCount + 1,
                        StartedAt = DateTime.UtcNow.ToString("o"),
                        CompletedAt = DateTime.UtcNow.ToString("o")
                    };
                    step.ParsedValidation = validationResult;

                    _database.CreateTaskExecutionStep(step);

                    execution.CurrentStep++;
                    execution.RetryCount = 0;
                    execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                    _database.UpdateTaskExecution(execution);

                    _logger.Log("Information", "MultiStep", $"Fallback model succeeded for step {step.StepNumber}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "MultiStep", $"Fallback model failed: {ex.Message}");
                return false;
            }
        }

        public async Task<string> CompleteExecutionAsync(long executionId, int threadId)
        {
            var execution = _database.GetTaskExecutionById(executionId);
            if (execution == null)
            {
                throw new ArgumentException($"Task execution {executionId} not found");
            }

            _logger.Log("Information", "MultiStep", $"Completing execution {executionId}");

            var steps = _database.GetTaskExecutionSteps(executionId);
            var taskTypeInfo = _database.GetTaskTypeByCode(execution.TaskType);

            if (taskTypeInfo == null)
            {
                throw new InvalidOperationException($"Task type {execution.TaskType} not found");
            }

            // Apply merge strategy
            string merged = taskTypeInfo.OutputMergeStrategy switch
            {
                "accumulate_chapters" => MergeChapters(steps),
                "accumulate_all" => string.Join("\n\n---\n\n", steps.Select(s => s.StepOutput)),
                "last_only" => steps.LastOrDefault()?.StepOutput ?? string.Empty,
                _ => string.Join("\n\n", steps.Select(s => s.StepOutput))
            };

            // Update entity if linked
            if (execution.EntityId.HasValue && execution.TaskType == "story")
            {
                _database.UpdateStoryById(execution.EntityId.Value, story: merged);
            }

            // Mark execution as completed
            execution.Status = "completed";
            execution.UpdatedAt = DateTime.UtcNow.ToString("o");
            _database.UpdateTaskExecution(execution);

            _logger.Log("Information", "MultiStep", $"Execution {executionId} completed successfully");

            return merged;
        }

        private string MergeChapters(List<TaskExecutionStep> steps)
        {
            var sb = new StringBuilder();

            // Steps 1-3 are typically: plot, characters, structure (skip in final output)
            // Steps 4+ are chapters
            var chapters = steps.Where(s => s.StepNumber >= 4).OrderBy(s => s.StepNumber);

            foreach (var chapter in chapters)
            {
                sb.AppendLine($"## CAPITOLO {chapter.StepNumber - 3}");
                sb.AppendLine();
                sb.AppendLine(chapter.StepOutput);
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
