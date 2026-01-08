using System;
using System.Collections.Concurrent;
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
        private readonly IMemoryEmbeddingGenerator _embeddingGenerator;
        private readonly ICommandDispatcher? _commandDispatcher;
        private readonly IServiceProvider _services;
        private readonly Dictionary<long, List<string>> _chunkCache = new();
        private readonly ConcurrentDictionary<(long executionId, int threadId), ChapterEmbeddingHistory> _chapterEmbeddingCache = new();
        private readonly bool _autoRecoverStaleExecutions;

        private sealed class ChapterEmbeddingHistory
        {
            public object Sync { get; } = new();
            public List<ChapterEmbeddingEntry> Entries { get; } = new();
        }

        private sealed record ChapterEmbeddingEntry(int StepNumber, string Text, float[] Embedding);

        private sealed record ChapterRepetitionCheckResult(
            bool ShouldRetry,
            string FeedbackMessage,
            int TotalBlocks,
            int HighSimilarityBlocks,
            double HighSimilarityRatio,
            float[]? ChapterEmbedding,
            string CleanedChapterText);

        private static readonly Regex ChapterHeadingRegex = new(
            @"^\s*(?:#{1,6}\s*)?(?:capitolo|chapter)\b.*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static readonly Regex StandaloneNumberLineRegex = new(
            @"^\s*\d+\s*[\.|\)]?\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static string CleanChapterTextForEmbeddings(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");

            // Remove chapter headings/titles and standalone numbering lines.
            normalized = ChapterHeadingRegex.Replace(normalized, string.Empty);
            normalized = StandaloneNumberLineRegex.Replace(normalized, string.Empty);

            // Whitespace normalization is allowed.
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized;
        }

        private static List<string> SplitIntoWordBlocks(string text, int minWords = 300, int maxWords = 600)
        {
            var blocks = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return blocks;

            var words = Regex.Split(text.Trim(), @"\s+").Where(w => !string.IsNullOrWhiteSpace(w)).ToArray();
            if (words.Length == 0) return blocks;

            int index = 0;
            while (index < words.Length)
            {
                var remaining = words.Length - index;
                int take;

                if (remaining <= maxWords)
                {
                    take = remaining;
                }
                else
                {
                    take = maxWords;
                    // Avoid producing a very small tail block when possible.
                    var tail = remaining - take;
                    if (tail > 0 && tail < minWords)
                    {
                        take = Math.Max(minWords, remaining - minWords);
                        take = Math.Min(take, maxWords);
                    }
                }

                var blockWords = words.Skip(index).Take(take);
                blocks.Add(string.Join(" ", blockWords));
                index += take;
            }

            return blocks;
        }

        private static float[] AverageEmbeddings(IReadOnlyList<float[]> embeddings)
        {
            if (embeddings == null || embeddings.Count == 0) return Array.Empty<float>();
            var dim = embeddings[0]?.Length ?? 0;
            if (dim == 0) return Array.Empty<float>();

            var sum = new double[dim];
            int count = 0;

            foreach (var emb in embeddings)
            {
                if (emb == null || emb.Length != dim) continue;
                for (int i = 0; i < dim; i++)
                {
                    sum[i] += emb[i];
                }
                count++;
            }

            if (count == 0) return Array.Empty<float>();

            var avg = new float[dim];
            for (int i = 0; i < dim; i++)
            {
                avg[i] = (float)(sum[i] / count);
            }

            return avg;
        }

        private ChapterEmbeddingHistory GetChapterEmbeddingHistory(long executionId, int threadId)
        {
            return _chapterEmbeddingCache.GetOrAdd((executionId, threadId), _ => new ChapterEmbeddingHistory());
        }

        private void AddOrReplaceChapterEmbedding(long executionId, int threadId, int stepNumber, string cleanedText, float[] embedding)
        {
            if (embedding == null || embedding.Length == 0) return;
            var history = GetChapterEmbeddingHistory(executionId, threadId);

            lock (history.Sync)
            {
                history.Entries.RemoveAll(e => e.StepNumber == stepNumber);
                history.Entries.Add(new ChapterEmbeddingEntry(stepNumber, cleanedText, embedding));
                history.Entries.Sort((a, b) => a.StepNumber.CompareTo(b.StepNumber));
            }
        }

        private async Task<ChapterRepetitionCheckResult?> CheckChapterRepetitionByEmbeddingsAsync(
            long executionId,
            int threadId,
            int stepNumber,
            string chapterText,
            CancellationToken ct = default)
        {
            // Thresholds from spec (empirical): high similarity if > 0.82.
            const double similarityThreshold = 0.82;
            const double ratioFailThreshold = 0.25;
            const double ratioMultipleHitThreshold = 0.20;

            var cleaned = CleanChapterTextForEmbeddings(chapterText ?? string.Empty);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return new ChapterRepetitionCheckResult(
                    ShouldRetry: false,
                    FeedbackMessage: string.Empty,
                    TotalBlocks: 0,
                    HighSimilarityBlocks: 0,
                    HighSimilarityRatio: 0,
                    ChapterEmbedding: null,
                    CleanedChapterText: cleaned);
            }

            var history = GetChapterEmbeddingHistory(executionId, threadId);
            List<float[]> previousChapterEmbeddings;
            lock (history.Sync)
            {
                previousChapterEmbeddings = history.Entries
                    .Where(e => e.StepNumber < stepNumber && e.Embedding != null && e.Embedding.Length > 0)
                    .OrderBy(e => e.StepNumber)
                    .Select(e => e.Embedding)
                    .ToList();
            }

            // No historical chapters -> nothing to compare. Still compute a usable embedding to cache.
            var blocks = SplitIntoWordBlocks(cleaned, minWords: 300, maxWords: 600);
            if (blocks.Count == 0)
            {
                blocks = new List<string> { cleaned };
            }

            var blockEmbeddings = new List<float[]>(blocks.Count);
            foreach (var block in blocks)
            {
                try
                {
                    var emb = await _embeddingGenerator.GenerateAsync(block, ct);
                    if (emb != null && emb.Length > 0)
                    {
                        blockEmbeddings.Add(emb);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log("Warning", "MultiStep", $"Embedding generation failed for step {stepNumber} block: {ex.Message}");
                    // If embeddings fail, abort sensor (non-blocking)
                    return null;
                }
            }

            var chapterEmbedding = AverageEmbeddings(blockEmbeddings);

            if (previousChapterEmbeddings.Count == 0)
            {
                return new ChapterRepetitionCheckResult(
                    ShouldRetry: false,
                    FeedbackMessage: string.Empty,
                    TotalBlocks: blocks.Count,
                    HighSimilarityBlocks: 0,
                    HighSimilarityRatio: 0,
                    ChapterEmbedding: chapterEmbedding.Length > 0 ? chapterEmbedding : null,
                    CleanedChapterText: cleaned);
            }

            var avgHistorical = AverageEmbeddings(previousChapterEmbeddings);
            if (avgHistorical.Length == 0)
            {
                return null;
            }

            int high = 0;
            for (int i = 0; i < blockEmbeddings.Count; i++)
            {
                var emb = blockEmbeddings[i];
                if (emb == null || emb.Length == 0) continue;
                var sim = CosineSimilarity(emb, avgHistorical);
                if (sim >= similarityThreshold)
                {
                    high++;
                }
            }

            var total = Math.Max(1, blockEmbeddings.Count);
            var ratio = (double)high / total;

            // Spec intent: fail when similarity crosses threshold on a meaningful portion of the chapter,
            // and avoid punishing a single isolated block.
            var shouldRetry = high >= 2 && (ratio > ratioFailThreshold || ratio >= ratioMultipleHitThreshold);

            var feedback = shouldRetry
                ? "Nel capitolo appena scritto sono presenti più passaggi che riprendono concetti già espressi in precedenza.\nRiduci spiegazioni e riformulazioni.\nLascia emergere i significati solo attraverso azioni e conseguenze."
                : string.Empty;

            return new ChapterRepetitionCheckResult(
                ShouldRetry: shouldRetry,
                FeedbackMessage: feedback,
                TotalBlocks: total,
                HighSimilarityBlocks: high,
                HighSimilarityRatio: ratio,
                ChapterEmbedding: chapterEmbedding.Length > 0 ? chapterEmbedding : null,
                CleanedChapterText: cleaned);
        }

        public MultiStepOrchestrationService(
            ILangChainKernelFactory kernelFactory,
            DatabaseService database,
            ResponseCheckerService checkerService,
            ITokenizer tokenizerService,
            ICustomLogger logger,
            IConfiguration configuration,
            IMemoryEmbeddingGenerator embeddingGenerator,
            IServiceProvider services,
            ICommandDispatcher? commandDispatcher = null)
        {
            _kernelFactory = kernelFactory;
            _database = database;
            _checkerService = checkerService;
            _tokenizerService = tokenizerService;
            _logger = logger;
            _configuration = configuration;
            _embeddingGenerator = embeddingGenerator;
            _services = services;
            _commandDispatcher = commandDispatcher;
            _autoRecoverStaleExecutions = _configuration.GetValue<bool>("MultiStep:AutoRecoverStaleExecutions", false);
        }

        private void EnqueueAutomaticStoryRevision(long storyDbId)
        {
            try
            {
                if (storyDbId <= 0) return;

                var storiesService = _services.GetService(typeof(StoriesService)) as StoriesService;
                if (storiesService == null)
                {
                    _logger.Log("Warning", "MultiStep", "Auto-advancement skipped: StoriesService non disponibile");
                    return;
                }

                // Instead of enqueueing just revision, start the full status chain
                // This will automatically advance through all states: revised → evaluated → tagged → tts_schema → etc.
                var chainId = storiesService.EnqueueStatusChain(storyDbId);
                if (!string.IsNullOrWhiteSpace(chainId))
                {
                    _logger.Log("Information", "MultiStep", $"✓ Auto-advancement chain started for story {storyDbId} (chainId: {chainId})");
                }
                else
                {
                    _logger.Log("Debug", "MultiStep", $"Auto-advancement chain not started for story {storyDbId} (maybe already active or dispatcher unavailable)");
                }
            }
            catch (Exception ex)
            {
                // Non-blocking best-effort: never fail story generation completion because of auto-enqueue.
                _logger.Log("Warning", "MultiStep", $"Auto-advancement chain failed for story {storyDbId}: {ex.Message}");
            }
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
            int? executorModelOverrideId = null)
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
            var mergedConfig = MergeConfigWithTemplateInstructions(configOverrides, templateInstructions, executorModelOverrideId);

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
            // Split by lines, keeping empty lines for structure
            var lines = stepPrompt.Split('\n');
            var steps = new List<string>();
            var stepStartPattern = new Regex(@"^\s*(\d+)[.\)]\s*(.*)$", RegexOptions.Compiled);
            
            var currentStepContent = new System.Text.StringBuilder();
            var originalNumbers = new List<int>();
            bool inStep = false;

            foreach (var line in lines)
            {
                var match = stepStartPattern.Match(line);
                if (match.Success)
                {
                    // Save previous step if any
                    if (inStep && currentStepContent.Length > 0)
                    {
                        steps.Add(currentStepContent.ToString().Trim());
                    }
                    
                    // Start new step
                    if (int.TryParse(match.Groups[1].Value, out var num))
                    {
                        originalNumbers.Add(num);
                    }
                    currentStepContent.Clear();
                    currentStepContent.Append(match.Groups[2].Value.Trim());
                    inStep = true;
                }
                else if (inStep)
                {
                    // Continue current step - append this line
                    if (currentStepContent.Length > 0)
                    {
                        currentStepContent.Append('\n');
                    }
                    currentStepContent.Append(line.TrimEnd());
                }
            }
            
            // Don't forget the last step
            if (inStep && currentStepContent.Length > 0)
            {
                steps.Add(currentStepContent.ToString().Trim());
            }

            // Check for gaps in original numbering
            if (originalNumbers.Count > 1)
            {
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
                Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - After while loop: execution={execution != null}, status={execution?.Status}, currentStep={execution?.CurrentStep}, maxStep={execution?.MaxStep}");
                _logger.Log("Debug", "MultiStep", $"After while loop: status={execution?.Status}, currentStep={execution?.CurrentStep}, maxStep={execution?.MaxStep}");
                
                if (execution != null && execution.Status == "in_progress" && execution.CurrentStep > execution.MaxStep)
                {
                    Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - Calling CompleteExecutionAsync");
                    _logger.Log("Information", "MultiStep", $"Calling CompleteExecutionAsync for execution {executionId}");
                    try
                    {
                        await CompleteExecutionAsync(executionId, threadId);
                        Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - CompleteExecutionAsync returned successfully");
                    }
                    catch (Exception completeEx)
                    {
                        Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - CompleteExecutionAsync FAILED: {completeEx.Message}");
                        _logger.Log("Error", "MultiStep", $"CompleteExecutionAsync failed: {completeEx.Message}", completeEx.ToString());
                        // Even if CompleteExecutionAsync fails, mark as completed to unblock
                        execution.Status = "completed";
                        execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                        _database.UpdateTaskExecution(execution);
                        _logger.Log("Warning", "MultiStep", $"Force-marked execution {executionId} as completed after CompleteExecutionAsync failure");
                    }
                }
                else if (execution != null && execution.CurrentStep > execution.MaxStep)
                {
                    // All steps done but status isn't in_progress - still mark as completed
                    Console.WriteLine($"[DEBUG] ExecuteAllStepsAsync - All steps done but status={execution.Status}, forcing completion");
                    _logger.Log("Warning", "MultiStep", $"All steps done but status={execution.Status}, forcing completion for execution {executionId}");
                    execution.Status = "completed";
                    execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                    _database.UpdateTaskExecution(execution);
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

            // Push a scope with step information so all logs during this step execution
            // will have StepNumber and MaxStep populated
            using var stepScope = LogScope.Push(
                $"step_{execution.CurrentStep}_of_{execution.MaxStep}",
                threadId,
                execution.CurrentStep,
                execution.MaxStep);

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
            // IMPORTANT: Take only the LAST attempt for each step (highest AttemptCount) to avoid duplications
            // when a step was retried multiple times.
            var completedPreviousSteps = previousSteps
                .Where(s => s.StepNumber < execution.CurrentStep && !string.IsNullOrWhiteSpace(s.StepOutput))
                .GroupBy(s => s.StepNumber)
                .Select(g => g.OrderByDescending(s => s.AttemptCount).First())
                .OrderBy(s => s.StepNumber)
                .ToList();
            
            // Check if the original step instruction contains {{PROMPT}} BEFORE replacing placeholders
            // This determines whether the InitialContext should be added to the system message later
            var originalHadPromptPlaceholder = stepInstruction.Contains("{{PROMPT}}");
            
            // Replace placeholders in stepInstruction
            stepInstruction = await ReplaceStepPlaceholdersAsync(stepInstruction, completedPreviousSteps, execution.InitialContext, threadId, ct);
            
            var context = string.Empty;
            List<ConversationMessage>? extraMessages = null;

            // Declare stepTemplate here so it's available to all validation branches
            Models.StepTemplate? stepTemplate = null;

            var hasChunkPlaceholder = stepInstruction.Contains("{{CHUNK_", StringComparison.OrdinalIgnoreCase);
            var chunks = GetChunksForExecution(execution);
            var (stepWithChunks, missingChunk) = ReplaceChunkPlaceholders(stepInstruction, chunks, execution.CurrentStep);
            stepInstruction = stepWithChunks;

            var chunkText = execution.CurrentStep <= chunks.Count
                ? chunks[execution.CurrentStep - 1]
                : string.Empty;
            
            // If the step prompt already contains the chunk via placeholders, avoid prepending it again
            // For "story" task type, NEVER prepend chunks because the workflow is:
            //   Step 1: Generate trama from InitialContext (user theme)
            //   Step 2: Generate characters from Step 1 output
            //   Step 3: Generate full story from previous steps
            // Chunks only make sense for "tts_schema" where each step processes a different chunk
            var isStoryTask = string.Equals(execution.TaskType, "story", StringComparison.OrdinalIgnoreCase);
            
            var chunkIntro = !hasChunkPlaceholder 
                && !string.IsNullOrWhiteSpace(chunkText)
                && !isStoryTask  // Never prepend chunks for story task type
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

            // If this is a retry (RetryCount > 0), build conversation history with previous attempts + feedback
            if (execution.RetryCount > 0)
            {
                var stepAttempts = previousSteps
                    .Where(s => s.StepNumber == execution.CurrentStep)
                    .OrderBy(s => s.AttemptCount)
                    .ToList();

                if (stepAttempts.Any(s => s.ParsedValidation != null && !s.ParsedValidation.IsValid))
                {
                    extraMessages = new List<ConversationMessage>();

                    var basePrompt = stepAttempts
                        .Select(s => s.StepInstruction)
                        .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                    if (string.IsNullOrWhiteSpace(basePrompt))
                    {
                        basePrompt = stepInstruction;
                    }
                    if (!string.IsNullOrWhiteSpace(basePrompt) && !string.IsNullOrWhiteSpace(context))
                    {
                        basePrompt = $"{context}\n\n---\n\n{basePrompt}";
                    }
                    if (!string.IsNullOrWhiteSpace(basePrompt) && !string.IsNullOrWhiteSpace(chunkIntro))
                    {
                        basePrompt = $"{chunkIntro}{basePrompt}";
                    }
                    if (!string.IsNullOrWhiteSpace(basePrompt))
                    {
                        extraMessages.Add(new ConversationMessage
                        {
                            Role = "user",
                            Content = basePrompt
                        });
                    }

                    foreach (var attempt in stepAttempts)
                    {
                        if (!string.IsNullOrWhiteSpace(attempt.StepOutput))
                        {
                            extraMessages.Add(new ConversationMessage
                            {
                                Role = "assistant",
                                Content = attempt.StepOutput
                            });
                        }

                        var validation = attempt.ParsedValidation;
                        if (validation != null && !validation.IsValid)
                        {
                            if (!string.IsNullOrWhiteSpace(validation.SystemMessageOverride))
                            {
                                extraMessages.Add(new ConversationMessage
                                {
                                    Role = "system",
                                    Content = validation.SystemMessageOverride
                                });
                            }
                            else
                            {
                                var retryNumber = attempt.AttemptCount;
                                var feedbackMessage = string.Equals(execution.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase)
                                    ? $@"**ATTENZIONE - RETRY {retryNumber}/3**\n\nLa tua risposta e' stata respinta per il seguente motivo:\n{validation.Reason}\n\nRipeti la trascrizione del chunk usando SOLO blocchi:\n[NARRATORE]\nTesto narrativo\n[PERSONAGGIO: Nome | EMOZIONE: emotion]\nTesto parlato\nNON aggiungere altro testo o JSON, copri tutto il chunk senza saltare nulla."
                                    : $@"**ATTENZIONE - RETRY {retryNumber}/3**\n\nLa tua risposta e' stata respinta per il seguente motivo:\n{validation.Reason}\n\nCorreggi la tua risposta tenendo conto del feedback ricevuto.";

                                extraMessages.Add(new ConversationMessage
                                {
                                    Role = "user",
                                    Content = feedbackMessage
                                });
                            }
                        }
                    }
                }
            }
            // Get executor agent
            var executorAgent = await GetExecutorAgentAsync(execution, threadId);

            // Measure token count
            int contextTokens = 0;
            try
            {
                contextTokens = _tokenizerService.CountTokens(context);
                if (contextTokens > 10000)
                {
                    _logger.Log($"Context size ({contextTokens} tokens) exceeds 10k, summarizing...", "MultiStep", "Warning");
                    string? preferredModelName = null;
                    if (isStoryTask)
                    {
                        preferredModelName = _database.GetModelInfoById(executorAgent.ModelId ?? 0)?.Name
                            ?? executorAgent.ModelName;
                    }
                    context = await SummarizeContextAsync(
                        context,
                        threadId,
                        ct,
                        preferredModelName,
                        executorAgent.Temperature,
                        executorAgent.TopP,
                        executorAgent.RepeatPenalty,
                        executorAgent.TopK,
                        executorAgent.RepeatLastN,
                        executorAgent.NumPredict);
                    contextTokens = _tokenizerService.CountTokens(context);
                    _logger.Log("Information", "MultiStep", $"Context summarized to {contextTokens} tokens");
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"Token counting failed: {ex.Message}, proceeding without summary", "MultiStep", "Warning");
            }
            
            // Push a nested scope with the agent name for logging
            using var agentScope = LogScope.Push(
                $"agent_{executorAgent.Name}",
                threadId,
                execution.CurrentStep,
                execution.MaxStep,
                executorAgent.Name);

            // Check if stepInstruction contains {{PROMPT}} tag
            string fullPrompt;
            var templateInstructions = GetTemplateInstructions(execution);
            var agentInstructions = executorAgent.Instructions ?? string.Empty;
            var agentPrompt = executorAgent.Prompt ?? string.Empty;
            // Requirement: for writer story generation, always add the agent's Instructions as the FIRST system message.
            // Any other system content (agent prompt, template instructions, story context) should come after.
            var useAgentInstructionsAsFirstSystemMessage = isStoryTask && !string.IsNullOrWhiteSpace(agentInstructions);

            string systemMessage;
            string secondarySystemMessage = string.Empty;

            if (useAgentInstructionsAsFirstSystemMessage)
            {
                systemMessage = agentInstructions.Trim();

                var secondaryBlocks = new List<string>();
                if (!string.IsNullOrWhiteSpace(agentPrompt))
                {
                    secondaryBlocks.Add($"=== AGENT PROMPT ===\n{agentPrompt.Trim()}");
                }
                if (!string.IsNullOrWhiteSpace(templateInstructions))
                {
                    secondaryBlocks.Add($"=== TEMPLATE INSTRUCTIONS ===\n{templateInstructions!.Trim()}");
                }
                secondarySystemMessage = secondaryBlocks.Count > 0 ? string.Join("\n\n", secondaryBlocks) : string.Empty;
            }
            else
            {
                var systemBlocks = new List<string>();
                if (!string.IsNullOrWhiteSpace(agentInstructions))
                {
                    systemBlocks.Add($"=== AGENT INSTRUCTIONS ===\n{agentInstructions.Trim()}");
                }
                if (!string.IsNullOrWhiteSpace(agentPrompt))
                {
                    systemBlocks.Add($"=== AGENT PROMPT ===\n{agentPrompt.Trim()}");
                }
                if (!string.IsNullOrWhiteSpace(templateInstructions))
                {
                    systemBlocks.Add($"=== TEMPLATE INSTRUCTIONS ===\n{templateInstructions!.Trim()}");
                }
                systemMessage = systemBlocks.Count > 0 ? string.Join("\n\n", systemBlocks) : string.Empty;
            }
            // Note: any validation-requested system overrides are injected via `extraMessages`
            var attachInitialContext = execution.CurrentStep == 1
                && !hasChunkPlaceholder
                && !string.IsNullOrWhiteSpace(execution.InitialContext)
                && !originalHadPromptPlaceholder;  // Don't attach to system message if {{PROMPT}} was in the template

            // The {{PROMPT}} placeholder was already replaced earlier by ReplaceStepPlaceholdersAsync,
            // so we just need to build the prompt. If originalHadPromptPlaceholder is true, the InitialContext
            // is already in the user prompt and should NOT be added to the system message.
            fullPrompt = string.IsNullOrEmpty(context)
                ? stepInstruction
                : $"{context}\n\n---\n\n{stepInstruction}";

            if (attachInitialContext)
            {
                // Only add to system message if the template did NOT have {{PROMPT}}
                // For writer story generation, keep agent instructions as the first system message and append context after.
                if (useAgentInstructionsAsFirstSystemMessage)
                {
                    secondarySystemMessage = string.IsNullOrEmpty(secondarySystemMessage)
                        ? $"=== CONTESTO DELLA STORIA ===\n{execution.InitialContext}"
                        : $"{secondarySystemMessage}\n\n=== CONTESTO DELLA STORIA ===\n{execution.InitialContext}";
                }
                else
                {
                    systemMessage = string.IsNullOrEmpty(systemMessage)
                        ? $"=== CONTESTO DELLA STORIA ===\n{execution.InitialContext}"
                        : $"{systemMessage}\n\n=== CONTESTO DELLA STORIA ===\n{execution.InitialContext}";
                }
            }

            // If we have secondary system content, inject it right after the first system message.
            if (useAgentInstructionsAsFirstSystemMessage && !string.IsNullOrWhiteSpace(secondarySystemMessage))
            {
                extraMessages ??= new List<ConversationMessage>();
                extraMessages.Insert(0, new ConversationMessage
                {
                    Role = "system",
                    Content = secondarySystemMessage
                });
            }

            if (!string.IsNullOrWhiteSpace(chunkIntro))
            {
                fullPrompt = $"{chunkIntro}{fullPrompt}";
            }

            // Create timeout for this step (20 minutes)
            using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stepCts.CancelAfter(TimeSpan.FromMinutes(20));

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
                var modelOverrideId = GetExecutionModelOverrideId(execution);
                var executorModelName = modelOverrideId.HasValue
                    ? _database.GetModelInfoById(modelOverrideId.Value)?.Name
                    : executorAgentModelInfo?.Name;
                if (string.IsNullOrWhiteSpace(executorModelName))
                {
                    throw new InvalidOperationException($"Executor agent \"{executorAgent.Name}\" has no model configured.");
                }
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
                var bridge = _kernelFactory.CreateChatBridge(
                    executorModelName,
                    executorAgent.Temperature,
                    executorAgent.TopP,
                    executorAgent.RepeatPenalty,
                    executorAgent.TopK,
                    executorAgent.RepeatLastN,
                    executorAgent.NumPredict);
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
                Console.WriteLine($"[DEBUG] ExecuteNextStepAsync - result.Success: {result.Success}, result.Error: {result.Error ?? "(null)"}");
                Console.WriteLine($"[DEBUG] ExecuteNextStepAsync - result.IterationCount: {result.IterationCount}, result.ExecutedTools count: {result.ExecutedTools?.Count ?? 0}");
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
                        _logger.Log("Error", "MultiStep", $"Executor agent produced empty output. Success={(result?.Success.ToString() ?? "false")}, Error={(result?.Error ?? "(null)" )}, Iterations={(result?.IterationCount.ToString() ?? "0")}, ExecutedTools={result?.ExecutedTools?.Count ?? 0}");
                        throw new InvalidOperationException($"Executor agent produced empty output. Model: {executorModelName}, Success: {(result?.Success.ToString() ?? "false")}, Error: {(result?.Error ?? "none")}, Iterations: {result?.IterationCount ?? 0}");
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

            // Detect summarizer step: either agent has role="summarizer" OR the prompt explicitly asks to summarize
            var isSummarizerStep = isStoryTask
                && (!string.IsNullOrWhiteSpace(executorAgent.Role)
                    && executorAgent.Role.Equals("summarizer", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(stepInstruction)
                    && stepInstruction.Contains("Riassumi", StringComparison.OrdinalIgnoreCase));

            if (isSummarizerStep)
            {
                baseValidation = new ValidationResult
                {
                    IsValid = true,
                    Reason = "Summary auto-accepted",
                    NeedsRetry = false,
                    SemanticScore = null
                };
            }
            else
            {
                // Get step template for validation checks (MinCharsStory, MinCharsTrama, etc.)
                if (executorAgent.MultiStepTemplateId.HasValue)
                {
                    stepTemplate = _database.GetStepTemplateById(executorAgent.MultiStepTemplateId.Value);
                }

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
                    // Generic deterministic/basic checks including MinCharsStory validation
                    baseValidation = await _checkerService.ValidateStepOutputAsync(
                        stepInstruction,
                        output,
                        validationCriteria,
                        threadId,
                        executorAgent.Name,
                        executorAgent.ModelName,
                        execution.TaskType,
                        execution.CurrentStep,
                        stepTemplate
                    );
                }
            }

            // Skip tool-use reminders for tts_schema (ora usa output testuale strutturato).
            if (!isSummarizerStep && !string.Equals(execution.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase))
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

                if (!isSummarizerStep && !string.IsNullOrWhiteSpace(executorAgent.Role) && writerRoles.Contains(executorAgent.Role))
                {
                    // stepTemplate already retrieved above for baseValidation

                    var writerValidation = await _checkerService.ValidateWriterResponseAsync(
                        stepInstruction,
                        output,
                        validationCriteria,
                        threadId,
                        executorAgent.Name,
                        executorAgent.ModelName,
                        execution.TaskType,
                        execution.CurrentStep,
                        stepTemplate,
                        execution
                    );

                    validationResult = writerValidation;
                }
                else
                {
                    validationResult = baseValidation;
                }
            }

            // Post-generation repetition sensor for STORY chapters only (capitoli): run AFTER generation and core validations.
            // Never affects sampling/prompt; only decides retry with narrative feedback.
            ChapterRepetitionCheckResult? repetitionCheck = null;
            var isChapterStep = isStoryTask && execution.CurrentStep >= 4; // Steps 1-3 are plot/characters/structure
            var isWriterRole = !string.IsNullOrWhiteSpace(executorAgent.Role)
                && (executorAgent.Role.Equals("writer", StringComparison.OrdinalIgnoreCase)
                    || executorAgent.Role.Equals("story_writer", StringComparison.OrdinalIgnoreCase)
                    || executorAgent.Role.Equals("text_writer", StringComparison.OrdinalIgnoreCase));

            if (validationResult.IsValid && !isSummarizerStep && isChapterStep && isWriterRole)
            {
                try
                {
                    repetitionCheck = await CheckChapterRepetitionByEmbeddingsAsync(
                        executionId,
                        threadId,
                        execution.CurrentStep,
                        output,
                        ct);

                    if (repetitionCheck != null && repetitionCheck.ShouldRetry)
                    {
                        _logger.Log("Warning", "MultiStep",
                            $"Embedding repetition sensor flagged step {execution.CurrentStep}: highBlocks={repetitionCheck.HighSimilarityBlocks}/{repetitionCheck.TotalBlocks} ratio={repetitionCheck.HighSimilarityRatio:F2}");

                        if (execution.RetryCount < 2)
                        {
                            validationResult = new ValidationResult
                            {
                                IsValid = false,
                                Reason = repetitionCheck.FeedbackMessage,
                                NeedsRetry = true,
                                SemanticScore = null
                            };
                        }
                        else
                        {
                            // Max retries exceeded: accept anyway but log warning (per spec)
                            _logger.Log("Warning", "MultiStep",
                                $"Step {execution.CurrentStep} still flagged as repetitive after 3 attempts, proceeding anyway");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Non-blocking: never fail the step due to sensor issues.
                    _logger.Log("Warning", "MultiStep", $"Embedding repetition sensor failed for step {execution.CurrentStep}: {ex.Message}");
                }
            }

            if (validationResult.IsValid)
            {
                // Step valid - check if this step requires evaluator validation
                var evaluationSteps = GetEvaluationStepsFromTemplate(execution);
                
                if (!isSummarizerStep && evaluationSteps.Contains(execution.CurrentStep))
                {
                    _logger.Log("Information", "MultiStep", $"Step {execution.CurrentStep} requires evaluator validation");
                    
                    var (passed, avgScore, feedback) = await EvaluateChapterWithEvaluatorsAsync(
                        output, execution.CurrentStep, threadId, ct);
                    
                    if (!passed)
                    {
                        // Evaluation failed - check if we should retry or proceed
                        _logger.Log("Warning", "MultiStep", 
                            $"Step {execution.CurrentStep} failed evaluator validation: avg={avgScore:F2} < 6.0");
                        
                        if (execution.RetryCount < 2)
                        {
                            // Can still retry - create validation result with evaluator feedback
                            var evalValidationResult = new ValidationResult
                            {
                                IsValid = false,
                                Reason = $"Il capitolo non ha superato la valutazione dei critici (punteggio medio: {avgScore:F1}/10, minimo richiesto: 6.0).\n\n{feedback}",
                                NeedsRetry = true,
                                SemanticScore = avgScore
                            };
                            
                            // Save failed attempt with evaluator feedback
                            var evalFailedStep = new TaskExecutionStep
                            {
                                ExecutionId = executionId,
                                StepNumber = execution.CurrentStep,
                                StepInstruction = stepInstruction,
                                StepOutput = output,
                                AttemptCount = execution.RetryCount + 1,
                                StartedAt = DateTime.UtcNow.ToString("o"),
                                CompletedAt = DateTime.UtcNow.ToString("o")
                            };
                            evalFailedStep.ParsedValidation = evalValidationResult;
                            _database.CreateTaskExecutionStep(evalFailedStep);
                            
                            execution.RetryCount++;
                            execution.UpdatedAt = DateTime.UtcNow.ToString("o");
                            _database.UpdateTaskExecution(execution);
                            
                            retryCallback?.Invoke(execution.RetryCount);
                            
                            // Retry same step with evaluator feedback
                            return await ExecuteNextStepAsync(executionId, threadId, ct, retryCallback);
                        }
                        else
                        {
                            // Max retries exceeded - accept anyway but log warning
                            _logger.Log("Warning", "MultiStep", 
                                $"Step {execution.CurrentStep} failed evaluation after 3 attempts, proceeding anyway with score {avgScore:F2}");
                            // Don't modify validationResult - keep it as valid to proceed
                        }
                    }
                    else
                    {
                        _logger.Log("Information", "MultiStep", 
                            $"Step {execution.CurrentStep} passed evaluator validation: avg={avgScore:F2}");
                    }
                }
                
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

                // If this was a story chapter step and we have an embedding, cache it for future chapter checks.
                if (isChapterStep && isWriterRole && repetitionCheck?.ChapterEmbedding != null && repetitionCheck.ChapterEmbedding.Length > 0)
                {
                    AddOrReplaceChapterEmbedding(executionId, threadId, execution.CurrentStep, repetitionCheck.CleanedChapterText, repetitionCheck.ChapterEmbedding);
                }

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
                    var msg = $"Executor agent {execution.ExecutorAgentId} not found or inactive.";
                    _logger.Log(msg, "MultiStep", "Error");
                    throw new InvalidOperationException(msg);
                }
            }
            else
            {
                var msg = "ExecutorAgentId is missing for task execution.";
                _logger.Log(msg, "MultiStep", "Error");
                throw new InvalidOperationException(msg);
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
                sourceText = story?.StoryRaw;
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

        private string? MergeConfigWithTemplateInstructions(string? configOverrides, string? templateInstructions, int? executorModelOverrideId)
        {
            if (string.IsNullOrWhiteSpace(templateInstructions) && !executorModelOverrideId.HasValue)
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
                if (executorModelOverrideId.HasValue)
                {
                    dict["executorModelIdOverride"] = executorModelOverrideId.Value;
                }
                return JsonSerializer.Serialize(dict);
            }
            catch
            {
                // Fall back to a minimal JSON that preserves the override and original raw config
                return JsonSerializer.Serialize(new
                {
                    templateInstructions,
                    executorModelIdOverride = executorModelOverrideId,
                    rawConfig = configOverrides
                });
            }
        }

        private int? GetExecutionModelOverrideId(TaskExecution execution)
        {
            return GetConfigValue<int?>(execution.Config, "executorModelIdOverride");
        }

        private (int? serieId, int? serieEpisode) GetSeriesInfoFromConfig(TaskExecution execution)
        {
            var serieId = GetConfigValue<int?>(execution.Config, "serie_id");
            var serieEpisode = GetConfigValue<int?>(execution.Config, "serie_episode");
            return (serieId, serieEpisode);
        }

        /// <summary>
        /// Gets a typed value from the execution config JSON.
        /// </summary>
        private static T? GetConfigValue<T>(string? configJson, string propertyName)
        {
            if (string.IsNullOrWhiteSpace(configJson))
                return default;

            try
            {
                using var doc = JsonDocument.Parse(configJson);
                if (doc.RootElement.TryGetProperty(propertyName, out var prop))
                {
                    if (typeof(T) == typeof(int?) || typeof(T) == typeof(int))
                    {
                        if (prop.TryGetInt32(out var intVal))
                            return (T)(object)intVal;
                    }
                    else if (typeof(T) == typeof(string))
                    {
                        return (T?)(object?)prop.GetString();
                    }
                    else if (typeof(T) == typeof(bool?) || typeof(T) == typeof(bool))
                    {
                        if (prop.ValueKind == JsonValueKind.True)
                            return (T)(object)true;
                        if (prop.ValueKind == JsonValueKind.False)
                            return (T)(object)false;
                    }
                }
            }
            catch
            {
            }

            return default;
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

            // Handle SECTION placeholders: {{STEP_N_SECTION[TAG]}}
            // Extract content between [TAG] and next [TAG] or end of text
            var sectionPattern = @"\{\{STEP_(\d+)_SECTION\[([^\]]+)\]\}\}";
            var sectionMatches = Regex.Matches(instruction, sectionPattern);
            
            _logger.Log("Debug", "MultiStep", $"Found {sectionMatches.Count} SECTION placeholder(s). Available steps: [{string.Join(", ", previousSteps.Select(s => s.StepNumber))}]");
            
            foreach (Match match in sectionMatches)
            {
                int stepNum = int.Parse(match.Groups[1].Value);
                string tag = match.Groups[2].Value;
                
                _logger.Log("Debug", "MultiStep", $"Processing SECTION placeholder: step={stepNum}, tag={tag}");
                
                var targetStep = previousSteps.FirstOrDefault(s => s.StepNumber == stepNum);
                if (targetStep == null)
                {
                    _logger.Log("Warning", "MultiStep", $"Step {stepNum} not found in previousSteps for SECTION[{tag}]");
                    instruction = instruction.Replace(match.Value, $"[Section {tag} from step {stepNum} not available]");
                    continue;
                }
                
                if (string.IsNullOrWhiteSpace(targetStep.StepOutput))
                {
                    _logger.Log("Warning", "MultiStep", $"Step {stepNum} has empty output for SECTION[{tag}]");
                    instruction = instruction.Replace(match.Value, $"[Section {tag} from step {stepNum} is empty]");
                    continue;
                }
                
                _logger.Log("Debug", "MultiStep", $"Step {stepNum} output length: {targetStep.StepOutput.Length} chars, extracting section [{tag}]");
                
                string sectionContent = ExtractSection(targetStep.StepOutput, tag);
                _logger.Log("Debug", "MultiStep", $"ExtractSection result for [{tag}]: {sectionContent.Length} chars, starts with: {(sectionContent.Length > 50 ? sectionContent.Substring(0, 50) + "..." : sectionContent)}");
                
                instruction = instruction.Replace(match.Value, sectionContent);
            }

            // Parse placeholders: {{STEP_N}}, {{STEP_N_SUMMARY}}, {{STEP_N_EXTRACT:filter}}, {{STEPS_N-M_SUMMARY}}
            // Colon is optional - only needed for EXTRACT:filter
            var placeholderPattern = @"\{\{STEP(?:S)?_(\d+)(?:-(\d+))?(?:_(SUMMARY|EXTRACT)(?::(.+?))?)?\}\}";
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

                // If there are multiple attempts for the same step, take only the last attempt (highest AttemptCount)
                // to avoid including both failed and successful versions
                targetSteps = targetSteps
                    .GroupBy(s => s.StepNumber)
                    .Select(g => g.OrderByDescending(s => s.AttemptCount).First())
                    .OrderBy(s => s.StepNumber)
                    .ToList();

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
            // Colon is optional - only needed for EXTRACT:filter
            var placeholderPattern = @"\{\{STEP(?:S)?_(\d+)(?:-(\d+))?(?:_(SUMMARY|EXTRACT)(?::(.+?))?)?\}\}";
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
            _logger.Log("Information", "MultiStep", $"GenerateSummaryAsync called for {steps.Count} step(s): [{string.Join(", ", steps.Select(s => s.StepNumber))}]");
            
            var text = string.Join("\n\n", steps.Select(s => s.StepOutput));
            
            _logger.Log("Debug", "MultiStep", $"Summary input text length: {text?.Length ?? 0} chars");
            
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.Log("Warning", "MultiStep", "Summary input is empty or whitespace");
                return string.Empty;
            }

            var summarizerAgent = _database.ListAgents()
                .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                    a.Role.Equals("summarizer", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (summarizerAgent == null)
            {
                _logger.Log("Warning", "MultiStep", "No summarizer agent found, returning full text");
                return text;
            }

            var summaryModelName = _database.GetModelInfoById(summarizerAgent.ModelId ?? 0)?.Name;
            if (string.IsNullOrWhiteSpace(summaryModelName))
            {
                _logger.Log("Warning", "MultiStep", $"Summarizer agent {summarizerAgent.Name} has no model configured, returning full text");
                return text;
            }

            _logger.Log("Information", "MultiStep", $"Summarizing {text.Length} chars using {summaryModelName}");

            try
            {
                var prompt = $@"Riassumi il seguente testo in modo conciso (max 500 parole):

{text}

RIASSUNTO:";

                var orchestrator = _kernelFactory.CreateOrchestrator(summaryModelName, new List<string>());
                var bridge = _kernelFactory.CreateChatBridge(
                    summaryModelName,
                    summarizerAgent.Temperature,
                    summarizerAgent.TopP,
                    summarizerAgent.RepeatPenalty,
                    summarizerAgent.TopK,
                    summarizerAgent.RepeatLastN,
                    summarizerAgent.NumPredict);
                var loop = new ReActLoopOrchestrator(orchestrator, _logger, modelBridge: bridge);
                var response = await loop.ExecuteAsync(prompt, ct);
                
                var summary = response.FinalResponse ?? string.Empty;
                _logger.Log("Information", "MultiStep", $"Summary generated: {summary.Length} chars, success={response.Success}");
                
                if (string.IsNullOrWhiteSpace(summary))
                {
                    _logger.Log("Warning", "MultiStep", "Summary result is empty, returning original text");
                    return text;
                }
                
                return summary;
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "MultiStep", $"Summary generation failed: {ex.Message}, returning full text", ex.ToString());
                return text;
            }
        }

        /// <summary>
        /// Extracts a section from text based on section tags.
        /// Supports multiple formats:
        /// - [CAPITOLO 2] or [CHAPTER 2] with brackets
        /// - CAPITOLO 2: or Capitolo 2: with colon
        /// - **CAPITOLO 2** or **Capitolo 2** with markdown bold
        /// - ## CAPITOLO 2 or ## Capitolo 2 with markdown headers
        /// Example: ExtractSection(text, "CAPITOLO 2") returns content between CAPITOLO 2 and CAPITOLO 3 (or end).
        /// </summary>
        private string ExtractSection(string output, string sectionTag)
        {
            if (string.IsNullOrWhiteSpace(output)) return string.Empty;

            // Build flexible patterns to match various section formats
            // sectionTag could be "CAPITOLO 2", "Chapter 2", etc.
            var escapedTag = Regex.Escape(sectionTag);
            
            // Try multiple patterns in order of specificity
            var patterns = new[]
            {
                // [CAPITOLO 2] - bracketed format
                $@"\[{escapedTag}\]",
                // **CAPITOLO 2** - markdown bold
                $@"\*\*{escapedTag}\*\*",
                // ## CAPITOLO 2 or ### CAPITOLO 2 - markdown headers  
                $@"#+\s*{escapedTag}",
                // CAPITOLO 2: or Capitolo 2: - with colon
                $@"(?:^|\n)\s*{escapedTag}\s*[:\-–]",
                // CAPITOLO 2 (at start of line, no punctuation but followed by newline)
                $@"(?:^|\n)\s*{escapedTag}\s*(?=\n)"
            };

            System.Text.RegularExpressions.Match startMatch = null!;
            foreach (var pattern in patterns)
            {
                startMatch = Regex.Match(output, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (startMatch.Success)
                {
                    _logger.Log("Debug", "MultiStep", $"Found section [{sectionTag}] using pattern: {pattern}");
                    break;
                }
            }
            
            if (startMatch == null || !startMatch.Success)
            {
                _logger.Log("Warning", "MultiStep", $"Section tag [{sectionTag}] not found in step output. Tried patterns for brackets, markdown, and colon formats.");
                return $"[Section {sectionTag} not found]";
            }

            int startIndex = startMatch.Index + startMatch.Length;
            
            // Find the next section tag (various formats) or end of text
            // Look for patterns like [CAPITOLO N], **CAPITOLO N**, ## CAPITOLO N, CAPITOLO N:
            var nextSectionPatterns = new[]
            {
                @"\[[A-Z][A-Za-z]+\s*\d+\]",              // [CAPITOLO 3]
                @"\*\*[A-Z][A-Za-z]+\s*\d+\*\*",         // **CAPITOLO 3**
                @"#+\s*[A-Z][A-Za-z]+\s*\d+",            // ## CAPITOLO 3
                @"(?:^|\n)\s*[A-Z][A-Za-z]+\s*\d+\s*[:\-–]"  // CAPITOLO 3:
            };
            
            int endIndex = output.Length;
            foreach (var pattern in nextSectionPatterns)
            {
                var nextMatch = Regex.Match(output.Substring(startIndex), pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                if (nextMatch.Success && startIndex + nextMatch.Index < endIndex)
                {
                    endIndex = startIndex + nextMatch.Index;
                    break;
                }
            }
            
            string content = output.Substring(startIndex, endIndex - startIndex).Trim();
            
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.Log("Warning", "MultiStep", $"Section [{sectionTag}] is empty");
                return $"[Section {sectionTag} is empty]";
            }
            
            return content;
        }

        private string ExtractContent(string output, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return output;

            var lines = output.Split('\n');
            var extracting = false;
            var result = new StringBuilder();

            // Build a pattern to detect the START of a new section (not just the filter we're looking for)
            // This handles multiple formats: [CAPITOLO N], **CAPITOLO N**, CAPITOLO N:, ## CAPITOLO N
            var newSectionPattern = @"^(\s*\[|\s*\*\*|\s*#+\s*|\s*)(Capitolo|Chapter|CAPITOLO|CHAPTER)\s*\d+";

            foreach (var line in lines)
            {
                // Check if this line starts a new section (and it's not the one we're looking for)
                if (extracting && Regex.IsMatch(line, newSectionPattern, RegexOptions.IgnoreCase))
                {
                    // Check if this is a DIFFERENT section than the one we started with
                    if (!line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        // Stop - we've reached the next section
                        break;
                    }
                }
                
                // Start extracting when we find the filter
                if (line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    extracting = true;
                }

                if (extracting)
                {
                    result.AppendLine(line);
                }
            }

            return result.Length > 0 ? result.ToString().Trim() : output;
        }

        private async Task<string> SummarizeContextAsync(
            string context,
            int threadId,
            CancellationToken ct,
            string? preferredModelName = null,
            double? temperature = null,
            double? topP = null,
            double? repeatPenalty = null,
            int? topK = null,
            int? repeatLastN = null,
            int? numPredict = null)
        {
            _logger.Log("Information", "MultiStep", "Summarizing context (>10k tokens)");

            var summarizerAgent = _database.ListAgents()
                .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                    a.Role.Equals("summarizer", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (summarizerAgent == null)
            {
                _logger.Log("Warning", "MultiStep", "No summarizer agent found, skipping context summarization");
                return context;
            }

            var summaryModelName = _database.GetModelInfoById(summarizerAgent.ModelId ?? 0)?.Name;
            if (string.IsNullOrWhiteSpace(summaryModelName))
            {
                _logger.Log("Warning", "MultiStep", $"Summarizer agent {summarizerAgent.Name} has no model configured, skipping context summarization");
                return context;
            }

            try
            {
                var prompt = $@"Riassumi questo contesto in max 500 parole mantenendo le informazioni chiave:

{context}

RIASSUNTO:";

                var orchestrator = _kernelFactory.CreateOrchestrator(summaryModelName, new List<string>());
                var bridge = _kernelFactory.CreateChatBridge(
                    summaryModelName,
                    temperature ?? summarizerAgent.Temperature,
                    topP ?? summarizerAgent.TopP,
                    repeatPenalty ?? summarizerAgent.RepeatPenalty,
                    topK ?? summarizerAgent.TopK,
                    repeatLastN ?? summarizerAgent.RepeatLastN,
                    numPredict ?? summarizerAgent.NumPredict);
                var systemMessage = summarizerAgent.Instructions ?? string.Empty;
                var loop = new ReActLoopOrchestrator(
                    orchestrator,
                    _logger,
                    modelBridge: bridge,
                    systemMessage: systemMessage);
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
                var bridge = _kernelFactory.CreateChatBridge(
                    fallbackModelName,
                    currentAgent.Temperature,
                    currentAgent.TopP,
                    currentAgent.RepeatPenalty,
                    currentAgent.TopK,
                    currentAgent.RepeatLastN,
                    currentAgent.NumPredict);
                var loop = new ReActLoopOrchestrator(orchestrator, _logger, modelBridge: bridge);

                using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stepCts.CancelAfter(TimeSpan.FromMinutes(20));

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

            // SALVATAGGIO STORIA COMPLETA: Se siamo allo step full_story_step, salva la storia con titolo estratto
            var agent = await GetExecutorAgentAsync(execution, threadId);
            Models.StepTemplate? stepTemplate = null;
            if (agent?.MultiStepTemplateId.HasValue == true)
            {
                stepTemplate = _database.GetStepTemplateById(agent.MultiStepTemplateId.Value);
            }

            if (stepTemplate?.FullStoryStep.HasValue == true && execution.CurrentStep >= stepTemplate.FullStoryStep)
            {
                // Get all steps for the full_story_step number, then pick the one with the longest output
                // (in case there were multiple attempts, we want the successful one with actual content)
                var fullStoryStepCandidates = steps.Where(s => s.StepNumber == stepTemplate.FullStoryStep.Value).ToList();
                var fullStoryStep = fullStoryStepCandidates
                    .OrderByDescending(s => (s.StepOutput ?? string.Empty).Length)
                    .FirstOrDefault();
                    
                if (fullStoryStep != null && !string.IsNullOrWhiteSpace(fullStoryStep.StepOutput))
                {
                    try
                    {
                        var storyText = fullStoryStep.StepOutput;
                        string? title = null;

                        // Prova a estrarre il titolo dalla prima riga se contiene "Titolo:" o "Title:"
                        var lines = storyText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                        if (lines.Length > 0)
                        {
                            var firstLine = lines[0].Trim();
                            if (firstLine.Contains("Titolo:", StringComparison.OrdinalIgnoreCase) || 
                                firstLine.Contains("Title:", StringComparison.OrdinalIgnoreCase))
                            {
                                // Estrai il titolo dopo "Titolo:" o "Title:"
                                var colonIndex = firstLine.IndexOf(":");
                                if (colonIndex >= 0 && colonIndex < firstLine.Length - 1)
                                {
                                    title = firstLine.Substring(colonIndex + 1).Trim();
                                }
                            }
                        }

                        // Se non c'è titolo trovato, usa il valore da Config se presente
                        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(execution.Config))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(execution.Config);
                                var root = doc.RootElement;
                                if (root.TryGetProperty("title", out var titleProp))
                                {
                                    title = titleProp.GetString();
                                }
                            }
                            catch
                            {
                                // ignore parse errors
                            }
                        }

                        _logger.Log("Information", "MultiStep", 
                            $"Full story step {stepTemplate.FullStoryStep} completed: saving story with title='{title}', length={storyText.Length}");

                        var (serieId, serieEpisode) = GetSeriesInfoFromConfig(execution);

                        // Se execution.EntityId non esiste ancora, crea la storia
                        if (!execution.EntityId.HasValue)
                        {
                            var modelOverrideId = GetExecutionModelOverrideId(execution);
                            var modelId = modelOverrideId ?? agent?.ModelId;

                            var prompt = execution.InitialContext ?? "[No prompt]";
                            var storyId = _database.InsertSingleStory(
                                prompt: prompt, 
                                story: storyText, 
                                agentId: agent?.Id, 
                                modelId: modelId, 
                                title: title,
                                serieId: serieId,
                                serieEpisode: serieEpisode);
                            
                            execution.EntityId = storyId;
                            _database.UpdateTaskExecution(execution);
                            _logger.Log("Information", "MultiStep", 
                                $"Created new story {storyId} from full_story_step with title='{title}'");

                            if (string.Equals(execution.TaskType, "story", StringComparison.OrdinalIgnoreCase))
                            {
                                EnqueueAutomaticStoryRevision(storyId);
                            }
                        }
                        else
                        {
                            // Story esiste già, aggiorna il titolo se necessario
                            if (!string.IsNullOrWhiteSpace(title))
                            {
                                _database.UpdateStoryTitle(execution.EntityId.Value, title);
                                _logger.Log("Information", "MultiStep", 
                                    $"Updated story {execution.EntityId.Value} title to '{title}'");
                            }
                            _database.UpdateStorySeriesInfo(execution.EntityId.Value, serieId, serieEpisode);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Log("Error", "MultiStep", 
                            $"Error saving story from full_story_step {stepTemplate.FullStoryStep}: {ex.Message}");
                    }
                }
                else
                {
                    // Log why story was not saved
                    var candidatesCount = fullStoryStepCandidates?.Count ?? 0;
                    var outputLength = fullStoryStep?.StepOutput?.Length ?? 0;
                    _logger.Log("Warning", "MultiStep", 
                        $"Could not save story from full_story_step {stepTemplate.FullStoryStep}: " +
                        $"candidates={candidatesCount}, selectedOutputLength={outputLength}, " +
                        $"step found={fullStoryStep != null}, output empty={string.IsNullOrWhiteSpace(fullStoryStep?.StepOutput)}");
                }
            }

            // If full_story_step was configured and completed, story is already saved - skip merge and further updates
            if (stepTemplate?.FullStoryStep.HasValue == true && execution.CurrentStep >= stepTemplate.FullStoryStep)
            {
                _logger.Log("Information", "MultiStep", 
                    $"Full story step {stepTemplate.FullStoryStep} was already saved. Skipping merge and final update.");
                return "Story already saved from full_story_step";
            }

            // Apply merge strategy
            string merged;
            if (taskTypeInfo.OutputMergeStrategy == "accumulate_chapters")
            {
                // Need to pass execution/context to MergeChapters so we can consult template.trama_steps
                merged = await MergeChapters(steps, execution, threadId);
            }
            else
            {
                merged = taskTypeInfo.OutputMergeStrategy switch
                {
                    "accumulate_all" => string.Join("\n\n---\n\n", steps.Select(s => s.StepOutput)),
                    "last_only" => steps.LastOrDefault()?.StepOutput ?? string.Empty,
                    _ => string.Join("\n\n", steps.Select(s => s.StepOutput))
                };
            }

            // Create or update story entity
            if (execution.TaskType == "story")
            {
                var modelOverrideId = GetExecutionModelOverrideId(execution);
                var modelId = modelOverrideId ?? agent?.ModelId;

                if (execution.EntityId.HasValue)
                {
                    // Story already exists, update it
                    try
                    {
                        _logger.Log("Debug", "MultiStep", $"Updating existing story {execution.EntityId.Value} with final text (length {merged?.Length ?? 0})");
                        var ok = _database.UpdateStoryById(
                            execution.EntityId.Value,
                            story: merged);
                        _logger.Log(ok ? "Information" : "Warning", "MultiStep", $"UpdateStoryById returned {ok} for story {execution.EntityId.Value}");
                        var (serieId, serieEpisode) = GetSeriesInfoFromConfig(execution);
                        _database.UpdateStorySeriesInfo(execution.EntityId.Value, serieId, serieEpisode);
                    }
                    catch (Exception ex)
                    {
                        _logger.Log("Error", "MultiStep", $"Exception updating story {execution.EntityId.Value}: {ex.Message}");
                    }
                }
                else
                {
                    // Create story only now with the complete text
                    var prompt = execution.InitialContext ?? "[No prompt]";
                    // Try to read a title from execution.Config if provided
                    string? title = null;
                    if (!string.IsNullOrWhiteSpace(execution.Config))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(execution.Config);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("title", out var titleProp))
                            {
                                title = titleProp.GetString();
                            }
                        }
                        catch
                        {
                            // ignore parse errors
                        }
                    }

                    try
                    {
                        _logger.Log("Debug", "MultiStep", $"Inserting new story (prompt len={prompt?.Length ?? 0}, merged len={merged?.Length ?? 0}, title present={(!string.IsNullOrWhiteSpace(title))})");
                        var safePrompt = prompt ?? string.Empty;
                        var safeMerged = merged ?? string.Empty;
                        var (serieId, serieEpisode) = GetSeriesInfoFromConfig(execution);
                        var storyId = _database.InsertSingleStory(safePrompt, safeMerged, agentId: agent?.Id, modelId: modelId, title: title, serieId: serieId, serieEpisode: serieEpisode);
                        _logger.Log("Information", "MultiStep", $"InsertSingleStory returned id {storyId}");

                        EnqueueAutomaticStoryRevision(storyId);

                        // Update execution with the new entity ID
                        execution.EntityId = storyId;
                        try
                        {
                            _database.UpdateTaskExecution(execution);
                            _logger.Log("Information", "MultiStep", $"Updated execution {execution.Id} with EntityId={storyId}");
                        }
                        catch (Exception ex)
                        {
                            _logger.Log("Error", "MultiStep", $"Failed to update TaskExecution with EntityId {storyId}: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Log("Error", "MultiStep", $"Exception inserting story: {ex.Message}");
                    }
                }

                // Extract and save characters if characters_step is configured
                var charactersStep = GetConfigValue<int?>(execution.Config, "characters_step");
                if (charactersStep.HasValue && execution.EntityId.HasValue)
                {
                    try
                    {
                        var charStep = steps.FirstOrDefault(s => s.StepNumber == charactersStep.Value);
                        if (charStep != null && !string.IsNullOrWhiteSpace(charStep.StepOutput))
                        {
                            var characters = StoryCharacterParser.ParseCharacterList(charStep.StepOutput);
                            if (characters.Count > 0)
                            {
                                var charactersJson = StoryCharacterParser.ToJson(characters);
                                _database.UpdateStoryCharacters(execution.EntityId.Value, charactersJson);
                                _logger.Log("Information", "MultiStep", 
                                    $"Saved {characters.Count} characters from step {charactersStep.Value} to story {execution.EntityId.Value}");
                            }
                        }
                        else
                        {
                            _logger.Log("Warning", "MultiStep", 
                                $"Characters step {charactersStep.Value} not found or empty for execution {executionId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Log("Warning", "MultiStep", $"Failed to extract characters: {ex.Message}");
                    }
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
                        var json = JsonSerializer.Serialize(schema, new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });
                        File.WriteAllText(filePath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

            // Best-effort cleanup of chapter embedding cache for this execution
            try
            {
                var keysToRemove = _chapterEmbeddingCache.Keys.Where(k => k.executionId == executionId).ToList();
                foreach (var key in keysToRemove)
                {
                    _chapterEmbeddingCache.TryRemove(key, out _);
                }
            }
            catch
            {
                // ignore cleanup errors
            }

            _logger.Log("Information", "MultiStep", $"Execution {executionId} completed successfully");

            return merged ?? string.Empty;
        }

        private async Task<string> MergeChapters(List<TaskExecutionStep> steps, TaskExecution execution, int threadId, CancellationToken ct = default)
        {
            var sb = new StringBuilder();

            // Steps 1-3 are typically: plot, characters, structure (skip in final output)
            // Steps 4+ are chapters
            var allChapters = steps.Where(s => s.StepNumber >= 4).OrderBy(s => s.StepNumber).ToList();

            // Group by step number and pick the longest output if duplicates exist
            var uniqueChapters = allChapters
                .GroupBy(s => s.StepNumber)
                .Select(g =>
                {
                    if (g.Count() > 1)
                    {
                        _logger.Log("Warning", "MultiStep",
                            $"Found {g.Count()} duplicate entries for step {g.Key}, keeping the longest one");
                        return g.OrderByDescending(s => (s.StepOutput ?? string.Empty).Length).First();
                    }
                    return g.First();
                })
                .OrderBy(s => s.StepNumber)
                .ToList();

            // Apply embeddings-based similarity filtering for trama steps (if configured)
            List<TaskExecutionStep> filteredChapters = await FilterSimilarChaptersByEmbeddingsAsync(uniqueChapters, execution, threadId, ct);

            foreach (var chapter in filteredChapters)
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

        private async Task<List<TaskExecutionStep>> FilterSimilarChaptersByEmbeddingsAsync(
            List<TaskExecutionStep> chapters,
            TaskExecution execution,
            int threadId,
            CancellationToken ct = default)
        {
            // Determine which steps contain trama text from the template associated to this execution's executor agent
            var tramaSteps = new List<int>();
            if (execution.ExecutorAgentId.HasValue)
            {
                var agent = _database.GetAgentById(execution.ExecutorAgentId.Value);
                if (agent?.MultiStepTemplateId != null)
                {
                    var template = _database.GetStepTemplateById(agent.MultiStepTemplateId.Value);
                    if (template != null)
                    {
                        tramaSteps = template.ParsedTramaSteps;
                    }
                }
            }

            // If no trama steps configured, return original chapters
            if (tramaSteps == null || tramaSteps.Count == 0)
            {
                return chapters;
            }

            // Similarity threshold (configurable)
            var threshold = _configuration.GetValue<double?>("MultiStep:TramaSimilarityThreshold") ?? 0.82;

            var kept = new List<TaskExecutionStep>();
            var keptEmbeddings = new List<float[]>();

            for (int i = 0; i < chapters.Count; i++)
            {
                var chap = chapters[i];
                // If this chapter's step is not marked as trama, keep it
                if (!tramaSteps.Contains(chap.StepNumber))
                {
                    kept.Add(chap);
                    keptEmbeddings.Add(Array.Empty<float>());
                    continue;
                }

                var text = chap.StepOutput ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    kept.Add(chap);
                    keptEmbeddings.Add(Array.Empty<float>());
                    continue;
                }

                try
                {
                    var embedding = await _embeddingGenerator.GenerateAsync(text, ct);
                    if (embedding == null || embedding.Length == 0)
                    {
                        // Couldn't compute embedding — keep chapter
                        kept.Add(chap);
                        keptEmbeddings.Add(Array.Empty<float>());
                        continue;
                    }

                    var isDuplicate = false;
                    for (int j = 0; j < keptEmbeddings.Count; j++)
                    {
                        var prevEmb = keptEmbeddings[j];
                        if (prevEmb == null || prevEmb.Length == 0) continue;
                        var sim = CosineSimilarity(embedding, prevEmb);
                        if (sim >= threshold)
                        {
                            isDuplicate = true;
                            _logger.Log("Warning", "MultiStep",
                                $"Chapter step {chap.StepNumber} is semantically similar to previous chapter (step {kept[j].StepNumber}) with similarity={sim:F3} >= threshold {threshold:F2}. Skipping duplicate.");
                            break;
                        }
                    }

                    if (!isDuplicate)
                    {
                        kept.Add(chap);
                        keptEmbeddings.Add(embedding);
                    }
                    else
                    {
                        // Optionally, add an informational log entry for the execution so the writer can be informed
                        _logger.Log("Information", "MultiStep", $"Execution {execution.Id}: removed duplicate chapter at step {chap.StepNumber} due to high similarity to earlier chapter.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log("Warning", "MultiStep", $"Embedding similarity check failed for step {chap.StepNumber}: {ex.Message}");
                    // On failure, keep the chapter to avoid accidental loss
                    kept.Add(chap);
                    keptEmbeddings.Add(Array.Empty<float>());
                }
            }

            return kept;
        }

        private static double CosineSimilarity(float[] a, float[] b)
        {
            if (a == null || b == null) return 0.0;
            if (a.Length != b.Length) return 0.0;
            double dot = 0.0;
            double na = 0.0;
            double nb = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += (double)a[i] * (double)b[i];
                na += (double)a[i] * (double)a[i];
                nb += (double)b[i] * (double)b[i];
            }
            if (na == 0 || nb == 0) return 0.0;
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }

        /// <summary>
        /// Evaluates a chapter/step output using a single active chapter evaluator.
        /// Returns the average score and combined feedback. If average score is below threshold, 
        /// the step should be retried with the feedback.
        /// </summary>
        private async Task<(bool passed, double averageScore, string combinedFeedback)> EvaluateChapterWithEvaluatorsAsync(
            string chapterText,
            int stepNumber,
            int threadId,
            CancellationToken ct = default)
        {
            // Get all active evaluators (story_evaluator role)
            var evaluators = _database.ListAgents()
                .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                    a.Role.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (evaluators.Count == 0)
            {
                _logger.Log("Warning", "MultiStep", "No active evaluators found, skipping chapter evaluation");
                return (true, 10.0, string.Empty);
            }

            var evaluator = evaluators
                .OrderBy(a => a.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .First();

            _logger.Log("Information", "MultiStep", $"Evaluating step {stepNumber} with evaluator {evaluator.Name}");

            ChapterEvaluationResult? result;
            try
            {
                result = await EvaluateChapterWithSingleEvaluatorAsync(
                    chapterText, stepNumber, evaluator, threadId, ct);
            }
            catch (Exception ex)
            {
                _logger.Log("Warning", "MultiStep", $"Evaluator {evaluator.Name} failed: {ex.Message}");
                result = null;
            }

            if (result == null)
            {
                _logger.Log("Warning", "MultiStep", "Evaluator failed, passing step by default");
                return (true, 10.0, string.Empty);
            }

            var evalFeedback = new StringBuilder();
            evalFeedback.AppendLine($"### Valutatore: {evaluator.Name} (punteggio medio: {result.AverageScore:F1})");
            evalFeedback.AppendLine($"- Coerenza narrativa: {result.NarrativeCoherenceScore}/10 - {result.NarrativeCoherenceFeedback}");
            evalFeedback.AppendLine($"- Originalita: {result.OriginalityScore}/10 - {result.OriginalityFeedback}");
            evalFeedback.AppendLine($"- Impatto emotivo: {result.EmotionalImpactScore}/10 - {result.EmotionalImpactFeedback}");
            evalFeedback.AppendLine($"- Stile: {result.StyleScore}/10 - {result.StyleFeedback}");
            evalFeedback.AppendLine($"**Valutazione complessiva:** {result.OverallFeedback}");

            _logger.Log("Information", "MultiStep", 
                $"Evaluator {evaluator.Name}: avg={result.AverageScore:F1} (coherence={result.NarrativeCoherenceScore}, originality={result.OriginalityScore}, emotion={result.EmotionalImpactScore}, style={result.StyleScore})");

            var overallAverage = result.AverageScore;
            var combinedFeedback = evalFeedback.ToString();
            var passed = overallAverage >= 6.0;

            _logger.Log("Information", "MultiStep", 
                $"Chapter evaluation complete: avg={overallAverage:F2}, passed={passed}, evaluators=1");

            return (passed, overallAverage, combinedFeedback);
        }

        private async Task<ChapterEvaluationResult?> EvaluateChapterWithSingleEvaluatorAsync(
            string chapterText,
            int stepNumber,
            Agent evaluator,
            int threadId,
            CancellationToken ct = default)
        {
            var modelInfo = _database.GetModelInfoById(evaluator.ModelId ?? 0);
            var modelName = modelInfo?.Name;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                _logger.Log("Warning", "MultiStep", $"Evaluator {evaluator.Name} has no model configured, skipping evaluation");
                return null;
            }

            // Create orchestrator with chapter_evaluator skill only
            var orchestrator = _kernelFactory.CreateOrchestrator(
                modelName,
                new List<string> { "chapter_evaluator" },
                evaluator.Id,
                ttsWorkingFolder: null,
                ttsStoryText: null);

            // Set tool context
            var chapterTool = orchestrator.GetTool<ChapterEvaluatorTool>("chapter_evaluator");
            if (chapterTool != null)
            {
                chapterTool.AgentId = evaluator.Id;
                chapterTool.ModelId = evaluator.ModelId;
                chapterTool.ModelName = modelName;
            }

            var bridge = _kernelFactory.CreateChatBridge(
                modelName,
                evaluator.Temperature,
                evaluator.TopP,
                evaluator.RepeatPenalty,
                evaluator.TopK,
                evaluator.RepeatLastN,
                evaluator.NumPredict);

            // Build system message for evaluation
            var systemMessage = evaluator.Instructions ?? 
                "Sei un critico letterario esperto. Valuta il capitolo fornito usando la funzione evaluate_chapter. " +
                "Fornisci punteggi da 1 a 10 per ogni criterio e feedback specifici. " +
                "Devi SEMPRE chiamare la funzione evaluate_chapter per restituire i tuoi risultati.";

            // Build user prompt with chapter text
            var userPrompt = $@"Valuta il seguente capitolo (Step {stepNumber}) usando la funzione evaluate_chapter:

---
{chapterText}
---

Analizza il testo e fornisci una valutazione dettagliata per:
1. Coerenza narrativa (1-10): logica, flusso, consistenza
2. Originalità (1-10): creatività, unicità
3. Impatto emotivo (1-10): coinvolgimento, emozioni
4. Stile (1-10): qualità della prosa, uso del linguaggio

Chiama la funzione evaluate_chapter con i tuoi punteggi e feedback.";

            var loop = new ReActLoopOrchestrator(
                orchestrator,
                _logger,
                maxIterations: 10,
                runId: null,
                modelBridge: bridge,
                systemMessage: systemMessage,
                responseChecker: null, // No response checker for evaluations
                agentRole: evaluator.Role,
                extraMessages: null);

            try
            {
                using var evalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                evalCts.CancelAfter(TimeSpan.FromMinutes(3));

                var result = await loop.ExecuteAsync(userPrompt, evalCts.Token);

                // Parse result from ChapterEvaluatorTool
                if (chapterTool?.LastResult != null)
                {
                    return JsonSerializer.Deserialize<ChapterEvaluationResult>(
                        chapterTool.LastResult,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }

                // Try to parse from final response
                if (!string.IsNullOrWhiteSpace(result.FinalResponse))
                {
                    try
                    {
                        return JsonSerializer.Deserialize<ChapterEvaluationResult>(
                            result.FinalResponse,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    }
                    catch
                    {
                        _logger.Log("Warning", "MultiStep", $"Could not parse evaluation result from {evaluator.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "MultiStep", $"Evaluation by {evaluator.Name} failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Gets the list of step numbers that require evaluator validation from the template.
        /// </summary>
        private List<int> GetEvaluationStepsFromTemplate(TaskExecution execution)
        {
            // Get executor agent
            if (!execution.ExecutorAgentId.HasValue)
                return new List<int>();

            var agent = _database.GetAgentById(execution.ExecutorAgentId.Value);
            if (agent?.MultiStepTemplateId == null)
                return new List<int>();

            var template = _database.GetStepTemplateById(agent.MultiStepTemplateId.Value);
            return template?.ParsedEvaluationSteps ?? new List<int>();
        }
    }
}
