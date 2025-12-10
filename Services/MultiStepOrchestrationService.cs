using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Skills;

namespace TinyGenerator.Services
{
    public class MultiStepOrchestrationService
    {
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly DatabaseService _database;
        private readonly ResponseCheckerService _checkerService;
        private readonly ITokenizer _tokenizerService;
        private readonly ICustomLogger _logger;
        private readonly IConfiguration _configuration;
        private readonly Dictionary<long, List<string>> _chunkCache = new();
        private readonly bool _autoRecoverStaleExecutions;

        public MultiStepOrchestrationService(
            ILangChainKernelFactory kernelFactory,
            DatabaseService database,
            ResponseCheckerService checkerService,
            ITokenizer tokenizerService,
            ICustomLogger logger,
            IConfiguration configuration)
        {
            _kernelFactory = kernelFactory;
            _database = database;
            _checkerService = checkerService;
            _tokenizerService = tokenizerService;
            _logger = logger;
            _configuration = configuration;
            _autoRecoverStaleExecutions = _configuration.GetValue<bool>("MultiStep:AutoRecoverStaleExecutions", false);
        }

        public async Task<long> StartTaskExecutionAsync(
            string taskType,
            long? entityId,
            string stepPrompt,
            int? executorAgentId = null,
            int? checkerAgentId = null,
            string? configOverrides = null,
            string? initialContext = null,
            int threadId = 0,
            string? templateInstructions = null,
            string? executorModelOverride = null)
        {
            _logger.Log("Information", "MultiStep", "Starting task execution");
            await Task.CompletedTask;

            // Check for active execution lock
            var activeExecution = _database.GetActiveExecutionForEntity(entityId, taskType);
            if (activeExecution != null)
            {
                // User requested behavior: remove any existing partial/active execution and start a fresh one.
                try
                {
                    _logger.Log("Information", "MultiStep", $"Removing existing active execution (id={activeExecution.Id}) for entity {entityId} task {taskType} to start a new one.");
                    _database.DeleteTaskExecution(activeExecution.Id);
                }
                catch (Exception ex)
                {
                    _logger.Log($"Failed to delete existing execution {activeExecution.Id}: {ex.Message}", "MultiStep", "Error");
                    // If deletion fails, surface original error to avoid silent state corruption
                    throw new InvalidOperationException($"An active execution (id={activeExecution.Id}) already exists for entity {entityId} of type {taskType}");
                }
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

            // Merge any config overrides with template-level instructions so they persist across reloads
            var mergedConfig = MergeConfigWithTemplateInstructions(configOverrides, templateInstructions, executorModelOverride);

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
                Config = mergedConfig,
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
            var stepPattern = new Regex(@"^\s*\d+[.\)]\s*(.+)$", RegexOptions.Compiled);

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
                    var match = Regex.Match(line.Trim(), @"^(\d+)[.\)]");
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
            CancellationToken ct = default,
            Action<int>? retryCallback = null)
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

            // If the step prompt uses CHUNK placeholders, align the max step to the available chunks
            if (execution.StepPrompt.Contains("{{CHUNK_", StringComparison.OrdinalIgnoreCase))
            {
                var chunks = GetChunksForExecution(execution);
                var stepsCount = ParseAndRenumberSteps(execution.StepPrompt, threadId).Count;
                var targetMax = Math.Min(stepsCount, chunks.Count == 0 ? stepsCount : chunks.Count);

                if (chunks.Count == 0)
                {
                    _logger.Log("Warning", "MultiStep", $"Nessun chunk disponibile per execution {executionId}. Verranno usati gli step originali.");
                }

                if (targetMax > 0 && targetMax != execution.MaxStep)
                {
                    execution.MaxStep = targetMax;
                    execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                    _database.UpdateTaskExecution(execution);
                    _logger.Log("Information", "MultiStep", $"Allineato max_step a {targetMax} in base ai chunk disponibili");
                }
            }

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
                    await ExecuteNextStepAsync(executionId, threadId, ct, retryCallback);
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
            CancellationToken ct = default,
            Action<int>? retryCallback = null)
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

            // Get previous steps (we'll only build context when retrying a failed step)
            var previousSteps = _database.GetTaskExecutionSteps(executionId);

            // Build context from step placeholders ({{STEP_1}}, {{STEP_2}}, etc.)
            // This must be done ALWAYS, not just on retry, to resolve the placeholders in the instruction.
            // Only filter to completed steps from prior step numbers.
            var completedPreviousSteps = previousSteps
                .Where(s => s.StepNumber < execution.CurrentStep && !string.IsNullOrWhiteSpace(s.StepOutput))
                .ToList();
            
            // Replace placeholders in stepInstruction
            stepInstruction = await ReplaceStepPlaceholdersAsync(stepInstruction, completedPreviousSteps, execution.InitialContext, threadId, ct);
            
            var context = string.Empty;
            List<ConversationMessage>? extraMessages = null;

            var hasChunkPlaceholder = stepInstruction.Contains("{{CHUNK_", StringComparison.OrdinalIgnoreCase);
            var chunks = GetChunksForExecution(execution);
            var (stepWithChunks, missingChunk) = ReplaceChunkPlaceholders(stepInstruction, chunks, execution.CurrentStep);
            stepInstruction = stepWithChunks;

            var chunkText = execution.CurrentStep <= chunks.Count
                ? chunks[execution.CurrentStep - 1]
                : string.Empty;
            
            // If the step prompt already contains the chunk via placeholders, avoid prepending it again
            // For "story" task type, don't prepend the chunk if it's the InitialContext (first step)
            // because it's already included via {{PROMPT}} or in the system message
            var isStoryWithInitialContext = execution.TaskType == "story" 
                && execution.CurrentStep == 1 
                && chunkText == execution.InitialContext;
            
            var chunkIntro = !hasChunkPlaceholder 
                && !string.IsNullOrWhiteSpace(chunkText)
                && !isStoryWithInitialContext
                ? $"### CHUNK {execution.CurrentStep}\n{chunkText}\n\n"
                : string.Empty;

            if (hasChunkPlaceholder && missingChunk)
            {
                _logger.Log("Information", "MultiStep", $"Chunk mancante per lo step {execution.CurrentStep}; l'esecuzione verrà considerata completata.");
                execution.CurrentStep = execution.MaxStep + 1;
                execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                _database.UpdateTaskExecution(execution);
                return new TaskExecutionStep
                {
                    ExecutionId = executionId,
                    StepNumber = execution.CurrentStep,
                    StepInstruction = stepInstruction,
                    StepOutput = string.Empty,
                    AttemptCount = execution.RetryCount + 1,
                    StartedAt = DateTime.UtcNow.ToString("o"),
                    CompletedAt = DateTime.UtcNow.ToString("o")
                };
            }

            // If this is a retry (RetryCount > 0), prepend validation failure feedback
            if (execution.RetryCount > 0)
            {
                var lastAttempt = previousSteps
                    .Where(s => s.StepNumber == execution.CurrentStep)
                    .OrderByDescending(s => s.AttemptCount)
                    .FirstOrDefault();

                if (lastAttempt?.ParsedValidation != null && !lastAttempt.ParsedValidation.IsValid)
                {
                    // If the previous validation included a SystemMessageOverride, inject it into the
                    // `systemMessage` instead of placing the feedback inside the user prompt/context.
                    if (!string.IsNullOrWhiteSpace(lastAttempt.ParsedValidation.SystemMessageOverride))
                    {
                        extraMessages = new List<ConversationMessage>
                        {
                            new ConversationMessage { Role = "system", Content = lastAttempt.ParsedValidation.SystemMessageOverride }
                        };
                    }
                    else
                    {
                        var feedbackMessage = string.Equals(execution.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase)
                            ? $@"**ATTENZIONE - RETRY {execution.RetryCount}/3**

Il tentativo precedente è stato respinto per il seguente motivo:
{lastAttempt.ParsedValidation.Reason}

Ripeti la trascrizione del chunk usando SOLO blocchi:
[NARRATORE]
Testo narrativo
[PERSONAGGIO: Nome | EMOZIONE: emotion]
Testo parlato
NON aggiungere altro testo o JSON, copri tutto il chunk senza saltare nulla.
"
                            : $@"**ATTENZIONE - RETRY {execution.RetryCount}/3**

Il tentativo precedente è stato respinto per il seguente motivo:
{lastAttempt.ParsedValidation.Reason}

Output precedente che è stato respinto:
---
{lastAttempt.StepOutput}
---

Correggi l'output tenendo conto del feedback ricevuto.

";
                        // Add feedback as a separate assistant message (do not insert into user prompt)
                        extraMessages = new List<ConversationMessage>
                        {
                            new ConversationMessage { Role = "assistant", Content = feedbackMessage }
                        };
                    }
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
            var templateInstructions = GetTemplateInstructions(execution);
            var systemMessage = !string.IsNullOrWhiteSpace(templateInstructions)
                ? templateInstructions!
                : executorAgent.Instructions ?? string.Empty;
            // Note: any validation-requested system overrides are injected via `extraMessages`
            var attachInitialContext = execution.CurrentStep == 1
                && !hasChunkPlaceholder
                && !string.IsNullOrWhiteSpace(execution.InitialContext);

            if (stepInstruction.Contains("{{PROMPT}}") && !string.IsNullOrWhiteSpace(execution.InitialContext) && !hasChunkPlaceholder)
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

                if (attachInitialContext)
                {
                    systemMessage = string.IsNullOrEmpty(systemMessage)
                        ? $"**CONTESTO DELLA STORIA**:\n{execution.InitialContext}"
                        : $"{systemMessage}\n\n**CONTESTO DELLA STORIA**:\n{execution.InitialContext}";
                }
            }

            if (!string.IsNullOrWhiteSpace(chunkIntro))
            {
                fullPrompt = $"{chunkIntro}{fullPrompt}";
            }

            // Create timeout for this step (5 minutes)
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stepCts.CancelAfter(TimeSpan.FromMinutes(5));

            _logger.Log("Information", "MultiStep", $"Invoking executor agent: {executorAgent.Name} (model: {executorAgent.ModelName})");
            if (_logger is CustomLogger cl3) await cl3.FlushAsync();

            // Invoke executor agent
            string output;
            List<Dictionary<string, object>> executorToolSchemas = new();
            try
            {
                _logger.Log("Information", "MultiStep", $"Getting model info for executor agent, ModelId={executorAgent.ModelId}");
                if (_logger is CustomLogger cl4) await cl4.FlushAsync();
                
                var executorAgentModelInfo = _database.GetModelInfoById(executorAgent.ModelId ?? 0);
                var modelOverride = GetExecutionModelOverride(execution);
                var executorModelName = !string.IsNullOrWhiteSpace(modelOverride)
                    ? modelOverride!
                    : executorAgentModelInfo?.Name ?? "phi3:mini";
                var (workingFolder, ttsStoryText) = GetExecutionTtsConfig(execution);
                
                _logger.Log("Information", "MultiStep", $"Creating orchestrator for model: {executorModelName}, skills: {executorAgent.Skills}");
                if (_logger is CustomLogger cl5) await cl5.FlushAsync();
                
                var orchestrator = _kernelFactory.CreateOrchestrator(
                    executorModelName,
                    ParseSkills(executorAgent.Skills),
                    executorAgent.Id,
                    workingFolder,
                    ttsStoryText);

                // Capture available tool schemas for later reminder use
                try
                {
                    executorToolSchemas = orchestrator.GetToolSchemas();
                }
                catch
                {
                    executorToolSchemas = new List<Dictionary<string, object>>();
                }

                if (execution.EntityId.HasValue)
                {
                    var ttsTool = orchestrator.GetTool<TtsSchemaTool>("ttsschema");
                    if (ttsTool != null)
                    {
                        ttsTool.CurrentStoryId = execution.EntityId.Value;
                    }
                }
                var bridge = _kernelFactory.CreateChatBridge(executorModelName, executorAgent.Temperature, executorAgent.TopP);
                var loop = new ReActLoopOrchestrator(
                    orchestrator,
                    _logger,
                    maxIterations: 100,
                    runId: null,
                    modelBridge: bridge,
                    systemMessage: systemMessage,
                    responseChecker: _checkerService,
                    agentRole: executorAgent.Role,
                    extraMessages: extraMessages); // Pass extra messages (assistant/system) for retry feedback

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

                // If FinalResponse is empty but we executed tool calls, reconstruct a tool_calls JSON
                // from the executed tools so the TTS validator can parse it.
                if (string.IsNullOrWhiteSpace(output))
                {
                    if (result.ExecutedTools != null && result.ExecutedTools.Count > 0)
                    {
                        output = ReconstructToolCallsJsonFromToolRecords(result.ExecutedTools);
                        _logger.Log("Information", "MultiStep", $"Reconstructed output from executed tools (count={result.ExecutedTools.Count}) for validation (threadId={threadId})");
                    }

                    if (string.IsNullOrWhiteSpace(output))
                    {
                        Console.WriteLine($"[DEBUG] ExecuteNextStepAsync - Output is empty! result object: {result != null}, FinalResponse: '{result?.FinalResponse ?? "(null)"}'");
                        throw new InvalidOperationException("Executor agent produced empty output");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Log("Error", "MultiStep", "Step execution timeout (5 minutes)");
                throw new TimeoutException("Step execution exceeded 5 minute timeout");
            }

            // Validate output with deterministic checks (no LLM checker by default)
            var taskTypeInfo = _database.GetTaskTypeByCode(execution.TaskType);
            var validationCriteria = taskTypeInfo?.ParsedValidationCriteria;

            // If tts_schema and the model did not return tool_calls, try to convert structured text blocks to tool_calls JSON
            if (string.Equals(execution.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase))
            {
                var converted = ConvertStructuredTtsTextToToolCalls(output);
                if (!string.IsNullOrWhiteSpace(converted))
                {
                    output = converted;
                }
            }

            ValidationResult baseValidation;

            // Task-specific deterministic checks (e.g., TTS coverage) are executed here
            if (string.Equals(execution.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase))
            {
                // Use chunkText for coverage comparison (may be empty if not applicable)
                var ttsResult = _checkerService.ValidateTtsSchemaResponse(output, chunkText ?? string.Empty, 0.80);

                var reasons = new List<string>();
                if (ttsResult.Errors.Any()) reasons.AddRange(ttsResult.Errors);
                if (ttsResult.Warnings.Any()) reasons.AddRange(ttsResult.Warnings);

                baseValidation = new ValidationResult
                {
                    IsValid = ttsResult.IsValid,
                    Reason = ttsResult.FeedbackMessage ?? (reasons.Count > 0 ? string.Join("; ", reasons) : "TTS validation result"),
                    NeedsRetry = !ttsResult.IsValid,
                    SemanticScore = null
                };
            }
            else
            {
                // Generic deterministic/basic checks (no LLM invocation)
                baseValidation = await _checkerService.ValidateStepOutputAsync(
                    stepInstruction,
                    output,
                    validationCriteria,
                    threadId,
                    executorAgent.Name,
                    executorAgent.ModelName,
                    execution.TaskType
                );
            }

            // Skip tool-use reminders for tts_schema (ora usa output testuale strutturato).
            if (!string.Equals(execution.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var skills = ParseSkills(executorAgent.Skills);
                    if (skills.Count > 0 && !_checkerService.ContainsToolCalls(output))
                    {
                        // Build a short reminder to return to the agent on retry
                        var toolSchemas = executorToolSchemas;
                        var reminderObj = await _checkerService.BuildToolUseReminderAsync(
                            systemMessage,
                            fullPrompt ?? string.Empty,
                            output,
                            toolSchemas,
                            execution.RetryCount);

                        if (reminderObj != null && !string.IsNullOrWhiteSpace(reminderObj.Value.content))
                        {
                            baseValidation = new ValidationResult
                            {
                                IsValid = false,
                                Reason = reminderObj.Value.content,
                                NeedsRetry = true,
                                SemanticScore = null,
                                SystemMessageOverride = string.Equals(reminderObj.Value.role, "system", StringComparison.OrdinalIgnoreCase)
                                    ? reminderObj.Value.content
                                    : null
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log("Warning", "MultiStep", $"Tool-reminder generation failed: {ex.Message}");
                }
            }

            ValidationResult validationResult;

            if (!baseValidation.IsValid)
            {
                // Base validation failed - use that result
                validationResult = baseValidation;
            }
            else
            {
                // For writer-type agents perform additional writer-specific validation (semantic checks)
                var writerRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "writer",
                    "story_writer",
                    "text_writer"
                };

                if (!string.IsNullOrWhiteSpace(executorAgent.Role) && writerRoles.Contains(executorAgent.Role))
                {
                    var writerValidation = await _checkerService.ValidateWriterResponseAsync(
                        stepInstruction,
                        output,
                        validationCriteria,
                        threadId,
                        executorAgent.Name,
                        executorAgent.ModelName,
                        execution.TaskType
                    );

                    validationResult = writerValidation;
                }
                else
                {
                    validationResult = baseValidation;
                }
            }

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
                
                // Notifica retry al dispatcher
                retryCallback?.Invoke(execution.RetryCount);

                if (execution.RetryCount <= 2)
                {
                    // Retry same step with feedback
                    return await ExecuteNextStepAsync(executionId, threadId, ct, retryCallback);
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
            await Task.CompletedTask;
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

        private List<string> GetChunksForExecution(TaskExecution execution)
        {
            if (_chunkCache.TryGetValue(execution.Id, out var cached)) return cached;

            string? sourceText = execution.InitialContext;

            if (string.IsNullOrWhiteSpace(sourceText) && execution.EntityId.HasValue)
            {
                var story = _database.GetStoryById(execution.EntityId.Value);
                sourceText = story?.Story;
            }

            sourceText ??= string.Empty;
            var chunks = StoryChunkHelper.SplitIntoChunks(sourceText);
            _chunkCache[execution.Id] = chunks;
            return chunks;
        }

        private static object TryParseJsonOrString(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return new Dictionary<string, object?>();
            try
            {
                // Attempt to parse string as JSON; if it works, return the deserialized object
                var obj = JsonSerializer.Deserialize<object>(s);
                return obj ?? s;
            }
            catch
            {
                // Not JSON — return the original string
                return s;
            }
        }

        /// <summary>
        /// Reconstruct a tool_calls JSON object from executed tool records. Useful when the model
        /// returned no assistant content but tools executed successfully and we need a predictable
        /// payload for downstream validators.
        /// </summary>
        public static string ReconstructToolCallsJsonFromToolRecords(List<ReActLoopOrchestrator.ToolExecutionRecord> executedTools)
        {
            if (executedTools == null || executedTools.Count == 0) return string.Empty;

            var reconstructed = new Dictionary<string, object>
            {
                ["tool_calls"] = executedTools.Select((t, idx) => new Dictionary<string, object>
                {
                    ["id"] = $"recon_{idx}",
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object>
                    {
                        ["name"] = t.ToolName ?? string.Empty,
                        ["arguments"] = TryParseJsonOrString(t.Input ?? string.Empty)
                    }
                }).ToList()
            };

            return JsonSerializer.Serialize(reconstructed);
        }

        private (string replaced, bool missingChunk) ReplaceChunkPlaceholders(string instruction, List<string> chunks, int stepNumber)
        {
            if (!instruction.Contains("{{CHUNK_", StringComparison.OrdinalIgnoreCase))
                return (instruction, false);

            var missing = false;

            var replaced = Regex.Replace(instruction, @"\{\{CHUNK_(\d+)\}\}", match =>
            {
                if (!int.TryParse(match.Groups[1].Value, out var idx) || idx < 1)
                    return match.Value;

                if (idx <= chunks.Count)
                {
                    return chunks[idx - 1];
                }

                missing = true;
                return $"[CHUNK_{idx}_NON_DISPONIBILE]";
            });

            if (missing)
            {
                _logger.Log("Warning", "MultiStep", $"Chunk placeholder richiesto oltre i chunk disponibili (disponibili: {chunks.Count}) per step {stepNumber}");
            }

            return (replaced, missing);
        }

        private (string? workingFolder, string? storyText) GetExecutionTtsConfig(TaskExecution execution)
        {
            if (string.IsNullOrWhiteSpace(execution.Config))
                return (null, null);

            try
            {
                using var doc = JsonDocument.Parse(execution.Config);
                var root = doc.RootElement;
                string? folder = null;
                string? text = null;

                if (root.TryGetProperty("workingFolder", out var folderProp))
                {
                    folder = folderProp.GetString();
                }

                if (root.TryGetProperty("storyText", out var textProp))
                {
                    text = textProp.GetString();
                }

                return (folder, text);
            }
            catch
            {
                return (null, null);
            }
        }

        private string? GetTemplateInstructions(TaskExecution execution)
        {
            if (string.IsNullOrWhiteSpace(execution.Config))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(execution.Config);
                var root = doc.RootElement;
                if (root.TryGetProperty("templateInstructions", out var instrProp))
                {
                    return instrProp.GetString();
                }
            }
            catch
            {
                // ignore parse errors: just return null
            }

            return null;
        }

        private string? MergeConfigWithTemplateInstructions(string? configOverrides, string? templateInstructions)
        {
            return MergeConfigWithTemplateInstructions(configOverrides, templateInstructions, null);
        }

        private string? MergeConfigWithTemplateInstructions(string? configOverrides, string? templateInstructions, string? executorModelOverride)
        {
            if (string.IsNullOrWhiteSpace(templateInstructions) && string.IsNullOrWhiteSpace(executorModelOverride))
                return configOverrides;

            try
            {
                Dictionary<string, object?> dict;
                if (string.IsNullOrWhiteSpace(configOverrides))
                {
                    dict = new Dictionary<string, object?>();
                }
                else
                {
                    dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(configOverrides) ?? new Dictionary<string, object?>();
                }

                if (!string.IsNullOrWhiteSpace(templateInstructions))
                {
                    dict["templateInstructions"] = templateInstructions;
                }
                if (!string.IsNullOrWhiteSpace(executorModelOverride))
                {
                    dict["executorModelOverride"] = executorModelOverride;
                }
                return JsonSerializer.Serialize(dict);
            }
            catch
            {
                // Fall back to a minimal JSON that preserves the override and original raw config
                return JsonSerializer.Serialize(new
                {
                    templateInstructions,
                    executorModelOverride,
                    rawConfig = configOverrides
                });
            }
        }

        private string? GetExecutionModelOverride(TaskExecution execution)
        {
            if (string.IsNullOrWhiteSpace(execution.Config))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(execution.Config);
                var root = doc.RootElement;
                if (root.TryGetProperty("executorModelOverride", out var modelProp))
                {
                    return modelProp.GetString();
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>
        /// Convert a structured text response (blocks like [NARRATORE] ... or [PERSONAGGIO: X | EMOZIONE: Y]) into a tool_calls JSON.
        /// Returns null/empty if conversion is not applicable.
        /// </summary>
        private static string? ConvertStructuredTtsTextToToolCalls(string? output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            // If it already contains tool_calls or add_narration/add_phrase, skip
            if (output.IndexOf("add_narration", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("add_phrase", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("\"tool_calls\"", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return null;
            }

            var lines = output.Replace("\r\n", "\n").Split('\n');
            var blocks = new List<(string type, string character, string emotion, List<string> content)>();
            string? currentType = null;
            string currentCharacter = string.Empty;
            string currentEmotion = "neutral";
            var buffer = new List<string>();

            var narratorRegex = new Regex(@"^\[\s*NARRATORE\s*\]\s*$", RegexOptions.IgnoreCase);
            var characterRegex = new Regex(@"\[\s*PERSONAGGIO:\s*(?<name>[^\]|]+?)\s*(?:\|\s*EMOZIONE:\s*(?<emo>[^\]]+))?\s*\]\s*", RegexOptions.IgnoreCase);

            void Flush()
            {
                if (currentType == null) return;
                var text = string.Join("\n", buffer).Trim();
                if (string.IsNullOrWhiteSpace(text)) return;
                blocks.Add((currentType, currentCharacter, currentEmotion, new List<string> { text }));
                buffer.Clear();
            }

            foreach (var rawLine in lines)
            {
                var line = rawLine ?? string.Empty;
                if (narratorRegex.IsMatch(line))
                {
                    Flush();
                    currentType = "narration";
                    currentCharacter = "Narratore";
                    currentEmotion = "neutral";
                    continue;
                }

                var cm = characterRegex.Match(line);
                if (cm.Success)
                {
                    Flush();
                    currentType = "phrase";
                    currentCharacter = cm.Groups["name"].Value.Trim();
                    currentEmotion = string.IsNullOrWhiteSpace(cm.Groups["emo"].Value)
                        ? "neutral"
                        : cm.Groups["emo"].Value.Trim();
                    continue;
                }

                buffer.Add(line);
            }
            Flush();

            if (blocks.Count == 0) return null;

            var toolCalls = new List<Dictionary<string, object>>();
            int idx = 0;
            foreach (var b in blocks)
            {
                if (b.type == "narration")
                {
                    toolCalls.Add(new Dictionary<string, object>
                    {
                        ["id"] = $"conv_{idx++}",
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object>
                        {
                            ["name"] = "add_narration",
                            ["arguments"] = new Dictionary<string, object>
                            {
                                ["text"] = b.content[0]
                            }
                        }
                    });
                }
                else
                {
                    toolCalls.Add(new Dictionary<string, object>
                    {
                        ["id"] = $"conv_{idx++}",
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object>
                        {
                            ["name"] = "add_phrase",
                            ["arguments"] = new Dictionary<string, object>
                            {
                                ["character"] = string.IsNullOrWhiteSpace(b.character) ? "Narratore" : b.character,
                                ["emotion"] = string.IsNullOrWhiteSpace(b.emotion) ? "neutral" : b.emotion,
                                ["text"] = b.content[0]
                            }
                        }
                    });
                }
            }

            var jsonObj = new Dictionary<string, object>
            {
                ["tool_calls"] = toolCalls
            };
            return JsonSerializer.Serialize(jsonObj);
        }

        private async Task<string> ReplaceStepPlaceholdersAsync(
            string instruction,
            List<TaskExecutionStep> previousSteps,
            string? initialContext,
            int threadId,
            CancellationToken ct)
        {
            // First, replace {{PROMPT}} with initialContext if present
            if (instruction.Contains("{{PROMPT}}") && !string.IsNullOrWhiteSpace(initialContext))
            {
                instruction = instruction.Replace("{{PROMPT}}", initialContext);
            }

            // Parse placeholders: {{STEP_N}}, {{STEP_N_SUMMARY}}, {{STEP_N_EXTRACT:filter}}, {{STEPS_N-M_SUMMARY}}
            var placeholderPattern = @"\{\{STEP(?:S)?_(\d+)(?:-(\d+))?(?:_(SUMMARY|EXTRACT):(.+?))?\}\}";
            var matches = Regex.Matches(instruction, placeholderPattern);

            if (matches.Count == 0)
            {
                return instruction;
            }

            var result = instruction;

            foreach (Match match in matches)
            {
                int stepFrom = int.Parse(match.Groups[1].Value);
                int stepTo = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : stepFrom;
                string mode = match.Groups[3].Value; // SUMMARY, EXTRACT, or empty
                string filter = match.Groups[4].Value;

                var targetSteps = previousSteps.Where(s => s.StepNumber >= stepFrom && s.StepNumber <= stepTo).ToList();

                if (!targetSteps.Any())
                {
                    // Placeholder references non-existent step, replace with empty or warning
                    result = result.Replace(match.Value, $"[Step {stepFrom} not yet available]");
                    continue;
                }

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

                    // Sanitize outputs that are raw ttsschema tool_calls (add_narration/add_phrase).
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(content) &&
                            Regex.IsMatch(content, @"\badd_(?:narration|phrase)\s*\(", RegexOptions.IgnoreCase))
                        {
                            var callMatches = Regex.Matches(content, @"\badd_(?:narration|phrase)\s*\(", RegexOptions.IgnoreCase);
                            content = $"[TTS tool calls recorded: {callMatches.Count} entries]";
                        }
                    }
                    catch { }
                }

                // Replace the placeholder with the actual content
                result = result.Replace(match.Value, content);
            }

            return result;
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

                    // Sanitize outputs that are raw ttsschema tool_calls (add_narration/add_phrase).
                    // If a previous step produced only tool calls, reinserirli nel prompt causes the
                    // next executor to simply repeat them. Replace with a short summary instead.
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(content) &&
                            Regex.IsMatch(content, @"\badd_(?:narration|phrase)\s*\(", RegexOptions.IgnoreCase))
                        {
                            var callMatches = Regex.Matches(content, @"\badd_(?:narration|phrase)\s*\(", RegexOptions.IgnoreCase);
                            content = $"[TTS tool calls recorded: {callMatches.Count} entries]";
                        }
                    }
                    catch { }
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
                var bridge = _kernelFactory.CreateChatBridge(fallbackModelName, currentAgent.Temperature, currentAgent.TopP);
                var loop = new ReActLoopOrchestrator(orchestrator, _logger, modelBridge: bridge);

                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepCts.CancelAfter(TimeSpan.FromMinutes(5));

                var result = await loop.ExecuteAsync(fullPrompt, stepCts.Token);
                var output = result.FinalResponse ?? string.Empty;

                if (string.IsNullOrWhiteSpace(output)) return false;

                // Validate fallback output (use TTS deterministic check if relevant)
                var taskTypeInfo = _database.GetTaskTypeByCode(execution.TaskType);
                ValidationResult validationResult;

                if (string.Equals(execution.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase))
                {
                    var ttsResult = _checkerService.ValidateTtsSchemaResponse(output, string.Empty, 0.90);
                    var reasons = new List<string>();
                    if (ttsResult.Errors.Any()) reasons.AddRange(ttsResult.Errors);
                    if (ttsResult.Warnings.Any()) reasons.AddRange(ttsResult.Warnings);

                    validationResult = new ValidationResult
                    {
                        IsValid = ttsResult.IsValid,
                        Reason = ttsResult.FeedbackMessage ?? (reasons.Count > 0 ? string.Join("; ", reasons) : "TTS validation result"),
                        NeedsRetry = !ttsResult.IsValid,
                        SemanticScore = null
                    };
                }
                else
                {
                    validationResult = await _checkerService.ValidateStepOutputAsync(
                        stepInstruction,
                        output,
                        taskTypeInfo?.ParsedValidationCriteria,
                        threadId,
                        $"{currentAgent.Name} (fallback: {fallbackModel.Name})",
                        fallbackModel.Name
                    );
                }

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
            await Task.CompletedTask;

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

            // Create or update story entity
            if (execution.TaskType == "story")
            {
                var agent = await GetExecutorAgentAsync(execution, threadId);
                var modelOverride = GetExecutionModelOverride(execution);
                int? modelId = null;

                if (!string.IsNullOrWhiteSpace(modelOverride))
                {
                    var modelInfoByName = _database.GetModelInfo(modelOverride);
                    modelId = modelInfoByName?.Id;
                }

                if (modelId == null && agent?.ModelId != null)
                {
                    modelId = agent.ModelId;
                }

                if (execution.EntityId.HasValue)
                {
                    // Story already exists, update it
                    _database.UpdateStoryById(
                        execution.EntityId.Value,
                        story: merged,
                        agentId: agent?.Id,
                        modelId: modelId);
                    
                    _logger.Log("Information", "MultiStep", $"Updated story {execution.EntityId.Value} with final text");
                }
                else
                {
                    // Create story only now with the complete text
                    var prompt = execution.InitialContext ?? "[No prompt]";
                    var storyId = _database.InsertSingleStory(prompt, merged, agentId: agent?.Id, modelId: modelId);
                    
                    // Update execution with the new entity ID
                    execution.EntityId = storyId;
                    _database.UpdateTaskExecution(execution);
                    
                    _logger.Log("Information", "MultiStep", $"Created story {storyId} with final text on successful completion");
                }
            }

            // If this is a TTS schema generation, aggregate all tool calls from steps
            // and persist a final `tts_schema.json` in the configured working folder.
            if (string.Equals(execution.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var chunks = GetChunksForExecution(execution);
                    var schema = new TtsSchema();

                    foreach (var step in steps.OrderBy(s => s.StepNumber))
                    {
                        var stepOutput = step.StepOutput ?? string.Empty;
                        // Ensure stepOutput is converted to tool_calls if the executor produced structured text
                        var converted = ConvertStructuredTtsTextToToolCalls(stepOutput);
                        if (!string.IsNullOrWhiteSpace(converted))
                        {
                            stepOutput = converted;
                        }
                        var chunkText = step.StepNumber <= chunks.Count ? chunks[step.StepNumber - 1] : string.Empty;

                        var ttsResult = _checkerService.ValidateTtsSchemaResponse(stepOutput, chunkText, 0.80);

                        foreach (var parsed in ttsResult.ExtractedToolCalls)
                        {
                            try
                            {
                                if (string.Equals(parsed.FunctionName, "add_narration", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (parsed.Arguments.TryGetValue("text", out var txtObj))
                                    {
                                        var text = txtObj?.ToString() ?? string.Empty;
                                        var phrase = new TtsPhrase { Character = "Narratore", Text = text, Emotion = "neutral" };
                                        schema.Timeline.Add(phrase);
                                        if (!schema.Characters.Any(c => c.Name.Equals("Narratore", StringComparison.OrdinalIgnoreCase)))
                                        {
                                            schema.Characters.Add(new TtsCharacter { Name = "Narratore", Voice = "default", VoiceId = string.Empty, Gender = "", EmotionDefault = "" });
                                        }
                                    }
                                }
                                else if (string.Equals(parsed.FunctionName, "add_phrase", StringComparison.OrdinalIgnoreCase))
                                {
                                    parsed.Arguments.TryGetValue("character", out var charObj);
                                    parsed.Arguments.TryGetValue("text", out var txtObj);
                                    parsed.Arguments.TryGetValue("emotion", out var emoObj);

                                    var character = charObj?.ToString() ?? string.Empty;
                                    if (string.IsNullOrWhiteSpace(character))
                                    {
                                        character = $"Character{schema.Characters.Count + 1}";
                                    }
                                    var text = txtObj?.ToString() ?? string.Empty;
                                    var emotion = emoObj?.ToString() ?? "neutral";

                                    if (!schema.Characters.Any(c => c.Name.Equals(character, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        schema.Characters.Add(new TtsCharacter { Name = character, Voice = "default", VoiceId = string.Empty, Gender = "", EmotionDefault = string.Empty });
                                    }

                                    var phrase = new TtsPhrase { Character = character, Text = text, Emotion = emotion };
                                    schema.Timeline.Add(phrase);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Log("Warning", "MultiStep", $"Failed to convert parsed tool call to phrase: {ex.Message}");
                            }
                        }
                    }

                    // Persist final schema to working folder (from execution config)
                    var (workingFolder, storyText) = GetExecutionTtsConfig(execution);
                    if (string.IsNullOrWhiteSpace(workingFolder))
                    {
                        // default location under data/tts/<executionId>
                        workingFolder = Path.Combine("data", "tts", executionId.ToString());
                    }

                    try
                    {
                        Directory.CreateDirectory(workingFolder);
                        var filePath = Path.Combine(workingFolder, "tts_schema.json");
                        var json = JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(filePath, json);
                        _logger.Log("Information", "MultiStep", $"Saved final TTS schema to {filePath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Log("Warning", "MultiStep", $"Failed to save final TTS schema: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log("Warning", "MultiStep", $"Error while assembling final TTS schema: {ex.Message}");
                }
            }

            // Mark execution as completed
            execution.Status = "completed";
            execution.UpdatedAt = DateTime.UtcNow.ToString("o");
            _database.UpdateTaskExecution(execution);

            if (_chunkCache.ContainsKey(executionId))
            {
                _chunkCache.Remove(executionId);
            }

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
