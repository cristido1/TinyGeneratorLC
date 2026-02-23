using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

public sealed class CinoOptimizeStoryCommand : ICommand
{
    private readonly string _title;
    private readonly string _prompt;
    private readonly DatabaseService _database;
    private readonly StoriesService _storiesService;
    private readonly IAgentCallService _modelExecution;
    private readonly ICommandDispatcher? _dispatcher;
    private readonly ICustomLogger? _logger;
    private readonly ICallCenter? _callCenter;
    private readonly CinoOptions _options;
    private readonly bool _useResponseChecker;
    private readonly RepetitionDetectionOptions _repetitionOptions;
    private readonly EmbeddingRepetitionOptions _embeddingRepetitionOptions;

    public CinoOptimizeStoryCommand(
        string title,
        string prompt,
        DatabaseService database,
        StoriesService storiesService,
        IAgentCallService modelExecution,
        ICommandDispatcher? dispatcher,
        CinoOptions options,
        RepetitionDetectionOptions? repetitionOptions = null,
        EmbeddingRepetitionOptions? embeddingRepetitionOptions = null,
        ICallCenter? callCenter = null,
        bool? useResponseChecker = null,
        ICustomLogger? logger = null)
    {
        _title = title ?? string.Empty;
        _prompt = prompt ?? string.Empty;
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _storiesService = storiesService ?? throw new ArgumentNullException(nameof(storiesService));
        _modelExecution = modelExecution ?? throw new ArgumentNullException(nameof(modelExecution));
        _dispatcher = dispatcher;
        _options = options ?? new CinoOptions();
        _repetitionOptions = repetitionOptions ?? new RepetitionDetectionOptions();
        _embeddingRepetitionOptions = embeddingRepetitionOptions ?? new EmbeddingRepetitionOptions();
        _callCenter = callCenter;
        _useResponseChecker = useResponseChecker ?? _options.UseResponseChecker;
        _logger = logger;
    }

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        if (string.IsNullOrWhiteSpace(_title))
        {
            return new CommandResult(false, "Titolo obbligatorio.");
        }

        if (string.IsNullOrWhiteSpace(_prompt))
        {
            return new CommandResult(false, "Prompt obbligatorio.");
        }

        var effectiveRunId = string.IsNullOrWhiteSpace(runId)
            ? $"cino_optimize_story_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : runId.Trim();

        _logger?.Start(effectiveRunId);
        var startedAt = DateTime.UtcNow;
        var targetScore = Math.Max(1, _options.TargetScore);
        var maxDuration = TimeSpan.FromSeconds(Math.Max(30, _options.MaxDurationSeconds));
        var minGrowthPercent = Math.Max(0, _options.MinLengthGrowthPercent);

        var writers = _database.ListAgents()
            .Where(a => a.IsActive && string.Equals(a.Role, "writer_cino", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Id)
            .Take(2)
            .ToList();
        if (writers.Count < 2)
        {
            return new CommandResult(false, "Servono 2 agenti attivi con ruolo writer_cino.");
        }

        var evaluators = _database.ListAgents()
            .Where(a => a.IsActive && string.Equals(a.Role, "story_evaluator", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Id)
            .Take(2)
            .ToList();
        if (evaluators.Count < 2)
        {
            return new CommandResult(false, "Servono 2 agenti attivi con ruolo story_evaluator.");
        }

        var createdStoryIds = new HashSet<long>();
        var rootStoryId = _database.InsertSingleStory(
            prompt: _prompt,
            story: _prompt,
            title: _title);
        createdStoryIds.Add(rootStoryId);
        _logger?.Append(effectiveRunId, $"CINO root story creata: {rootStoryId}");

        var parent = await EvaluateStoryAsync(rootStoryId, evaluators, ct, effectiveRunId).ConfigureAwait(false);
        if (parent == null)
        {
            return new CommandResult(false, $"Valutazione baseline fallita (storyId={rootStoryId}).");
        }

        var best = parent;
        var generatedStories = 0;
        var acceptedStories = 0;
        _logger?.Append(effectiveRunId, $"Baseline score: {parent.AverageTotal:F2}");
        ReportCinoProgress(
            effectiveRunId,
            startedAt,
            maxDuration,
            iteration: 0,
            writerName: null,
            modelName: null,
            bestScore: best.AverageTotal,
            phase: "baseline",
            generatedStories: generatedStories,
            acceptedStories: acceptedStories,
            parentStoryId: parent.StoryId,
            candidateStoryId: null,
            note: "baseline_valutata");

        var iteration = 0;
        while (DateTime.UtcNow - startedAt < maxDuration && best.AverageTotal < targetScore)
        {
            ct.ThrowIfCancellationRequested();
            iteration++;
            _logger?.Append(effectiveRunId, $"Iterazione {iteration}: parent storyId={parent.StoryId}, score={parent.AverageTotal:F2}");

            CandidateEvaluation? bestAcceptedThisRound = null;
            foreach (var writer in writers)
            {
                ct.ThrowIfCancellationRequested();
                var writerModelDisplayName = ResolveWriterModelDisplayName(writer);
                ReportCinoProgress(
                    effectiveRunId,
                    startedAt,
                    maxDuration,
                    iteration,
                    writer.Name,
                    writerModelDisplayName,
                    best.AverageTotal,
                    "generazione",
                    generatedStories: generatedStories,
                    acceptedStories: acceptedStories,
                    parentStoryId: parent.StoryId,
                    candidateStoryId: null,
                    note: "writer_in_esecuzione");
                var candidateText = await GenerateCandidateAsync(writer, parent.StoryText, effectiveRunId, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(candidateText))
                {
                    _logger?.Append(effectiveRunId, $"Writer {writer.Name} non ha prodotto output valido.", "warning");
                    ReportCinoProgress(
                        effectiveRunId,
                        startedAt,
                        maxDuration,
                        iteration,
                        writer.Name,
                        writerModelDisplayName,
                        best.AverageTotal,
                        "writer_fallito",
                        generatedStories: generatedStories,
                        acceptedStories: acceptedStories,
                        parentStoryId: parent.StoryId,
                        candidateStoryId: null,
                        note: "output_non_valido");
                    continue;
                }

                var childStoryId = _database.InsertSingleStory(
                    prompt: _prompt,
                    story: candidateText,
                    modelId: writer.ModelId,
                    agentId: writer.Id,
                    title: _title,
                    parentStoryId: parent.StoryId);
                createdStoryIds.Add(childStoryId);
                generatedStories++;

                var candidate = await EvaluateStoryAsync(childStoryId, evaluators, ct, effectiveRunId).ConfigureAwait(false);
                if (candidate == null)
                {
                    _logger?.Append(effectiveRunId, $"Scarto storyId={childStoryId}: valutazione fallita.", "warning");
                    ReportCinoProgress(
                        effectiveRunId,
                        startedAt,
                        maxDuration,
                        iteration,
                        writer.Name,
                        writerModelDisplayName,
                        best.AverageTotal,
                        "valutazione_fallita",
                        generatedStories: generatedStories,
                        acceptedStories: acceptedStories,
                        parentStoryId: parent.StoryId,
                        candidateStoryId: childStoryId,
                        note: "eval_fallita");
                    continue;
                }

                var accepted = IsAccepted(parent, candidate, minGrowthPercent, out var reason);
                _logger?.Append(
                    effectiveRunId,
                    accepted
                        ? $"Accettata storyId={childStoryId}: score={candidate.AverageTotal:F2}"
                        : $"Scartata storyId={childStoryId}: {reason}",
                    accepted ? "success" : "warning");

                if (!accepted) continue;
                acceptedStories++;

                if (bestAcceptedThisRound == null || candidate.AverageTotal > bestAcceptedThisRound.AverageTotal)
                {
                    bestAcceptedThisRound = candidate;
                }

                ReportCinoProgress(
                    effectiveRunId,
                    startedAt,
                    maxDuration,
                    iteration,
                    writer.Name,
                    writerModelDisplayName,
                    Math.Max(best.AverageTotal, candidate.AverageTotal),
                    accepted ? "candidato_accettato" : "candidato_scartato",
                    generatedStories: generatedStories,
                    acceptedStories: acceptedStories,
                    parentStoryId: parent.StoryId,
                    candidateStoryId: childStoryId,
                    note: reason);
            }

            if (bestAcceptedThisRound != null)
            {
                parent = bestAcceptedThisRound;
                if (parent.AverageTotal > best.AverageTotal)
                {
                    best = parent;
                }
            }

            ReportCinoProgress(
                effectiveRunId,
                startedAt,
                maxDuration,
                iteration,
                writerName: null,
                modelName: null,
                bestScore: best.AverageTotal,
                phase: "fine_iterazione",
                generatedStories: generatedStories,
                acceptedStories: acceptedStories,
                parentStoryId: parent.StoryId,
                candidateStoryId: null,
                note: "round_completato");
        }

        await CleanupNonWinnerStoriesAsync(best.StoryId, createdStoryIds, effectiveRunId, preserveStoryId: rootStoryId).ConfigureAwait(false);
        _logger?.Append(effectiveRunId, $"CINO completato. Winner storyId={best.StoryId}, score={best.AverageTotal:F2}", "success");
        ReportCinoProgress(
            effectiveRunId,
            startedAt,
            maxDuration,
            iteration,
            writerName: null,
            modelName: null,
            bestScore: best.AverageTotal,
            phase: "completato",
            generatedStories: generatedStories,
            acceptedStories: acceptedStories,
            parentStoryId: best.StoryId,
            candidateStoryId: best.StoryId,
            note: $"winner={best.StoryId}");

        return new CommandResult(true, $"winner_story_id={best.StoryId}; score={best.AverageTotal:F2}");
    }

    private async Task<string?> GenerateCandidateAsync(Agent writer, string parentText, string runId, CancellationToken ct)
    {
        var callCenter = ResolveCallCenter();
        if (callCenter == null)
        {
            _logger?.Append(runId, $"Writer {writer.Name} fallito: CallCenter non disponibile", "warning");
            return null;
        }

        var systemPrompt = BuildSystemPrompt(writer);
        var prompt = BuildWriterPrompt(parentText);
        var history = new ChatHistory();
        history.AddSystem(systemPrompt);
        history.AddUser(prompt);

        var options = new CallOptions
        {
            Operation = "cino_optimize_story_writer",
            Timeout = TimeSpan.FromSeconds(180),
            MaxRetries = 2,
            UseResponseChecker = _useResponseChecker,
            AllowFallback = true,
            AskFailExplanation = true,
            SystemPromptOverride = systemPrompt
        };
        options.DeterministicChecks.Add(new CheckEmpty
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["ErrorMessage"] = "Output vuoto"
            })
        });
        options.DeterministicChecks.Add(new CheckMinLength
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["MinLength"] = 300,
                ["ErrorMessage"] = "Output troppo corto"
            })
        });
        options.DeterministicChecks.Add(new CheckMinimumGrowthPercent
        {
            Options = Options.Create<object>(new Dictionary<string, object>
            {
                ["SourceText"] = parentText ?? string.Empty,
                ["MinGrowthPercent"] = Math.Max(0d, _options.MinLengthGrowthPercent)
            })
        });

        var result = await callCenter.CallAgentAsync(
            storyId: 0,
            threadId: $"cino_optimize_story_writer:{writer.Id}:{runId}".GetHashCode(StringComparison.Ordinal),
            agent: writer,
            history: history,
            options: options,
            cancellationToken: ct).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
        {
            var error = result.FailureReason ?? "errore sconosciuto";
            _logger?.Append(runId, $"Writer {writer.Name} fallito: {error}", "warning");
            return null;
        }

        return result.ResponseText.Trim();
    }

    private async Task<CandidateEvaluation?> EvaluateStoryAsync(long storyId, IReadOnlyList<Agent> evaluators, CancellationToken ct, string? runId = null)
    {
        var story = _database.GetStoryById(storyId);
        if (story == null)
        {
            return null;
        }

        var ok1 = await _storiesService.EvaluateStoryWithAgentAsync(storyId, evaluators[0].Id, forceStandardFlow: true).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var ok2 = await _storiesService.EvaluateStoryWithAgentAsync(storyId, evaluators[1].Id, forceStandardFlow: true).ConfigureAwait(false);
        if (!ok1.success || !ok2.success)
        {
            return null;
        }

        var all = _database.GetStoryEvaluations(storyId);
        var eval1 = all
            .Where(e => e.AgentId == evaluators[0].Id)
            .OrderByDescending(e => e.Id)
            .FirstOrDefault();
        var eval2 = all
            .Where(e => e.AgentId == evaluators[1].Id)
            .OrderByDescending(e => e.Id)
            .FirstOrDefault();
        if (eval1 == null || eval2 == null)
        {
            return null;
        }

        var storyText = string.IsNullOrWhiteSpace(story.StoryRevised) ? (story.StoryRaw ?? string.Empty) : story.StoryRevised!;
        var charCount = storyText.Length;
        var penaltyFactor = Math.Min(1.0, charCount / (double)Math.Max(1, _options.LengthPenaltyNoPenaltyChars));
        var repetition = CommandModelExecutionService.AnalyzeTextRepetition(storyText, _repetitionOptions);
        CommandModelExecutionService.RepetitionResult? embeddingRep = null;
        if (_options.EnableEmbeddingSemanticRepetitionCheck && _modelExecution is CommandModelExecutionService execService)
        {
            embeddingRep = await execService.DetectEmbeddingRepetitionsAsync(storyText, _embeddingRepetitionOptions, ct).ConfigureAwait(false);
        }
        var repetitionScore = Math.Max(repetition.RepetitionScore, embeddingRep?.MaxScore ?? 0);
        var repetitionSource = (embeddingRep != null && embeddingRep.MaxScore >= repetition.RepetitionScore)
            ? $"embedding:{embeddingRep.Source}"
            : $"jaccard:{repetition.Source}";
        _logger?.Append(
            runId ?? string.Empty,
            $"[CINO][storyId={storyId}] repetitionScore={repetitionScore:0.000}; source={repetitionSource}; local={repetition.LocalJaccard:0.000}; chunk={repetition.ChunkJaccard:0.000}; embedding={(embeddingRep?.MaxScore ?? 0):0.000}");
        var hardFail = repetitionScore > _repetitionOptions.HardFailThreshold || (embeddingRep?.HardFail ?? false);
        var applyMediumPenalty = repetitionScore > _repetitionOptions.PenaltyMedium;
        var applyLowPenalty = repetitionScore > _repetitionOptions.PenaltyLow;

        var eval1Narr = eval1.NarrativeCoherenceScore * penaltyFactor;
        var eval1Orig = eval1.OriginalityScore * penaltyFactor;
        var eval1Emot = eval1.EmotionalImpactScore * penaltyFactor;
        var eval1Action = eval1.ActionScore * penaltyFactor;

        var eval2Narr = eval2.NarrativeCoherenceScore * penaltyFactor;
        var eval2Orig = eval2.OriginalityScore * penaltyFactor;
        var eval2Emot = eval2.EmotionalImpactScore * penaltyFactor;
        var eval2Action = eval2.ActionScore * penaltyFactor;

        var narrativeSum = eval1Narr + eval2Narr;
        var originalitySum = eval1Orig + eval2Orig;
        var emotionalSum = eval1Emot + eval2Emot;
        var actionSum = eval1Action + eval2Action;
        if (applyMediumPenalty)
        {
            narrativeSum = Math.Max(0, narrativeSum - 2.0);
        }
        if (applyLowPenalty)
        {
            originalitySum = Math.Max(0, originalitySum - 1.0);
        }

        var penalizedAverage = (narrativeSum + originalitySum + emotionalSum + actionSum) / 2.0;
        if (hardFail)
        {
            penalizedAverage = Math.Min(penalizedAverage, 1.0);
        }
        var penalizedNormalized = DatabaseService.NormalizeEvaluationScoreTo100(penalizedAverage);

        // Persist the penalized score directly on the story record.
        _database.UpdateStoryScore(storyId, penalizedNormalized);

        return new CandidateEvaluation(
            StoryId: storyId,
            StoryText: storyText,
            AverageTotal: penalizedAverage,
            NarrativeSum: narrativeSum,
            OriginalitySum: originalitySum,
            EmotionalSum: emotionalSum,
            ActionSum: actionSum,
            CharacterCount: charCount,
            LengthPenaltyFactor: penaltyFactor,
            RepetitionScore: repetitionScore,
            RepetitionSource: repetitionSource,
            HardFail: hardFail);
    }

    private static bool IsAccepted(CandidateEvaluation previous, CandidateEvaluation candidate, double minGrowthPercent, out string reason)
    {
        if (candidate.HardFail)
        {
            reason = $"hard-fail ripetizioni (score={candidate.RepetitionScore:0.000}, source={candidate.RepetitionSource})";
            return false;
        }

        var minCharsRequired = (int)Math.Ceiling(previous.CharacterCount * (1.0 + (Math.Max(0, minGrowthPercent) / 100.0)));
        if (candidate.CharacterCount < minCharsRequired)
        {
            reason = $"lunghezza insufficiente ({candidate.CharacterCount} < {minCharsRequired}, regola +{minGrowthPercent:0.##}%)";
            return false;
        }

        if (candidate.AverageTotal < previous.AverageTotal + 1.0)
        {
            reason = $"score medio insufficiente ({candidate.AverageTotal:F2} < {previous.AverageTotal + 1.0:F2})";
            return false;
        }

        if (candidate.NarrativeSum < previous.NarrativeSum - 1)
        {
            reason = "coerenza narrativa peggiorata oltre soglia";
            return false;
        }
        if (candidate.OriginalitySum < previous.OriginalitySum - 1)
        {
            reason = "originalità peggiorata oltre soglia";
            return false;
        }
        if (candidate.EmotionalSum < previous.EmotionalSum - 1)
        {
            reason = "impatto emotivo peggiorato oltre soglia";
            return false;
        }
        if (candidate.ActionSum < previous.ActionSum - 1)
        {
            reason = "azione peggiorata oltre soglia";
            return false;
        }

        reason = "ok";
        return true;
    }

    private async Task CleanupNonWinnerStoriesAsync(long winnerStoryId, IReadOnlyCollection<long> allStoryIds, string runId, long? preserveStoryId = null)
    {
        var deleteIds = allStoryIds
            .Where(id => id != winnerStoryId && (!preserveStoryId.HasValue || id != preserveStoryId.Value))
            .ToList();
        foreach (var storyId in deleteIds)
        {
            await Task.Yield();
            var story = _database.GetStoryById(storyId);
            if (!string.IsNullOrWhiteSpace(story?.Folder))
            {
                var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder!);
                try
                {
                    if (Directory.Exists(folderPath))
                    {
                        Directory.Delete(folderPath, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Append(runId, $"Cleanup cartella fallito per storyId={storyId}: {ex.Message}", "warning");
                }
            }

            try
            {
                _database.DeleteStoryPhysicallyById(storyId);
            }
            catch (Exception ex)
            {
                _logger?.Append(runId, $"Cleanup DB fallito per storyId={storyId}: {ex.Message}", "warning");
            }
        }
    }

    private static string BuildSystemPrompt(Agent writer)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(writer.Instructions))
        {
            sb.AppendLine(writer.Instructions.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(writer.Prompt))
        {
            sb.AppendLine(writer.Prompt.Trim());
        }

        sb.AppendLine("Riscrivi l'intera storia migliorandola.");
        sb.AppendLine("Non aggiungere commenti, note o markdown.");
        sb.AppendLine("Restituisci solo il testo completo della storia.");
        return sb.ToString();
    }

    private static string BuildWriterPrompt(string text)
    {
        return $"TESTO DA MIGLIORARE:\n\n{text}";
    }

    private string ResolveWriterModelDisplayName(Agent writer)
    {
        if (!string.IsNullOrWhiteSpace(writer.ModelName))
        {
            return writer.ModelName.Trim();
        }

        if (writer.ModelId.HasValue && writer.ModelId.Value > 0)
        {
            var modelName = _database.GetModelInfoById(writer.ModelId.Value)?.Name;
            if (!string.IsNullOrWhiteSpace(modelName))
            {
                return modelName.Trim();
            }

            return writer.ModelId.Value.ToString();
        }

        return "-";
    }

    private ICallCenter? ResolveCallCenter()
    {
        if (_callCenter != null)
        {
            return _callCenter;
        }

        var rootCallCenter = ServiceLocator.Services?.GetService<ICallCenter>();
        if (rootCallCenter != null)
        {
            return rootCallCenter;
        }
        
        return null;
    }

    private void ReportCinoProgress(
        string runId,
        DateTime startedAt,
        TimeSpan maxDuration,
        int iteration,
        string? writerName,
        string? modelName,
        double bestScore,
        string phase,
        int generatedStories,
        int acceptedStories,
        long? parentStoryId,
        long? candidateStoryId,
        string? note)
    {
        if (_dispatcher == null || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        var elapsed = DateTime.UtcNow - startedAt;
        var elapsedSec = Math.Max(0, (int)elapsed.TotalSeconds);
        var maxSec = Math.Max(1, (int)maxDuration.TotalSeconds);
        if (elapsedSec > maxSec) elapsedSec = maxSec;

        var safeWriter = string.IsNullOrWhiteSpace(writerName) ? "-" : writerName.Trim();
        var safeModel = string.IsNullOrWhiteSpace(modelName) ? "-" : modelName.Trim();
        var elapsedText = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        var desc = $"CINO|iter={Math.Max(0, iteration)}|writer={safeWriter}|model={safeModel}|best={bestScore:F2}|elapsed={elapsedText}|phase={phase}|generatedStories={Math.Max(0, generatedStories)}|acceptedStories={Math.Max(0, acceptedStories)}|parentStoryId={(parentStoryId.HasValue ? parentStoryId.Value : 0)}|candidateStoryId={(candidateStoryId.HasValue ? candidateStoryId.Value : 0)}|note={(string.IsNullOrWhiteSpace(note) ? "-" : note)}";

        _dispatcher.UpdateStep(runId, elapsedSec, maxSec, desc);
    }

    private sealed record CandidateEvaluation(
        long StoryId,
        string StoryText,
        double AverageTotal,
        double NarrativeSum,
        double OriginalitySum,
        double EmotionalSum,
        double ActionSum,
        int CharacterCount,
        double LengthPenaltyFactor,
        double RepetitionScore,
        string RepetitionSource,
        bool HardFail);
}
