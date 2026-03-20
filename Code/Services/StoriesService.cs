using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using TinyGenerator.Skills;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    public void UpdateStoryTitle(long storyId, string title)
    {
        _database.UpdateStoryTitle(storyId, title);
    }
    private const int TtsSchemaReadyStatusId = 3;
    private const int DefaultPhraseGapMs = 2000;
    private readonly DatabaseService _database;
    private readonly ILogger<StoriesService>? _logger;
    private readonly TtsService _ttsService;
    private readonly ILangChainKernelFactory? _kernelFactory;
    private readonly ICustomLogger? _customLogger;
    private readonly ICommandDispatcher? _commandDispatcher;
    private readonly MultiStepOrchestrationService? _multiStepOrchestrator;
    private readonly SentimentMappingService? _sentimentMappingService;
    private readonly ResponseCheckerService? _responseChecker;
    private readonly CommandTuningOptions _tuning;
    private readonly IOptionsMonitor<TtsSchemaGenerationOptions>? _ttsSchemaOptions;
    private readonly IOptionsMonitor<AudioGenerationOptions>? _audioGenerationOptions;
    private readonly IOptionsMonitor<AudioMixOptions>? _audioMixOptions;
    private readonly IOptionsMonitor<NarratorVoiceOptions>? _narratorVoiceOptions;
    private readonly StoryEvaluationOptions _storyEvaluationOptions;
    private readonly IOptionsMonitor<AutomaticOperationsOptions>? _idleAutoOptions;
    private readonly IOptionsMonitor<MonomodelModeOptions>? _monomodelOptions;
    private readonly IOptionsMonitor<StoryTaggingPipelineOptions>? _storyTaggingOptions;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IServiceHealthMonitor? _healthMonitor;
    private readonly SoundSearchService? _soundSearchService;
    private readonly StoryMainCommands _mainCommands;
    private readonly ConcurrentDictionary<long, StatusChainState> _statusChains = new();
    private readonly ConcurrentQueue<long> _autoCompleteDeferredFailures = new();
    private readonly ConcurrentDictionary<long, byte> _storyEvalEmailSent = new();
    private static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // IMPORTANT: write real UTF-8 characters (no \uXXXX escaping) so TTS reads accents correctly.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex PersonaggioRegex = new(@"\[PERSONAGGIO:\s*([^\]\|]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EmotionRegex = new(@"\[EMOZIONE:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmbientRegex = new(@"\[RUMORI", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FxRegex = new(@"\[FX:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MusicRegex = new(@"\[MUSICA", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const double AutoFormatMinAverageScore = 65.0;
    private const int AutoFormatMinEvaluations = 2;

    public StoriesService(
        DatabaseService database,
        TtsService ttsService,
        ILangChainKernelFactory? kernelFactory = null,
        ICustomLogger? customLogger = null,
        ILogger<StoriesService>? logger = null,
        ICommandDispatcher? commandDispatcher = null,
        MultiStepOrchestrationService? multiStepOrchestrator = null,
        SentimentMappingService? sentimentMappingService = null,
        ResponseCheckerService? responseChecker = null,
        IOptions<CommandTuningOptions>? tuningOptions = null,
        IOptionsMonitor<TtsSchemaGenerationOptions>? ttsSchemaOptions = null,
        IOptionsMonitor<AudioGenerationOptions>? audioGenerationOptions = null,
        IOptionsMonitor<AudioMixOptions>? audioMixOptions = null,
        IOptionsMonitor<NarratorVoiceOptions>? narratorVoiceOptions = null,
        IOptions<StoryEvaluationOptions>? storyEvaluationOptions = null,
        IOptionsMonitor<AutomaticOperationsOptions>? idleAutoOptions = null,
        IOptionsMonitor<MonomodelModeOptions>? monomodelOptions = null,
        IServiceScopeFactory? scopeFactory = null,
        IOptionsMonitor<StoryTaggingPipelineOptions>? storyTaggingOptions = null,
        IServiceHealthMonitor? healthMonitor = null,
        SoundSearchService? soundSearchService = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
        _kernelFactory = kernelFactory;
        _customLogger = customLogger;
        _logger = logger;
        _commandDispatcher = commandDispatcher;
        _multiStepOrchestrator = multiStepOrchestrator;
        _sentimentMappingService = sentimentMappingService;
        _responseChecker = responseChecker;
        _tuning = tuningOptions?.Value ?? new CommandTuningOptions();
        _ttsSchemaOptions = ttsSchemaOptions;
        _audioGenerationOptions = audioGenerationOptions;
        _audioMixOptions = audioMixOptions;
        _narratorVoiceOptions = narratorVoiceOptions;
        _storyEvaluationOptions = storyEvaluationOptions?.Value ?? new StoryEvaluationOptions();
        _scopeFactory = scopeFactory;
        _idleAutoOptions = idleAutoOptions;
        _monomodelOptions = monomodelOptions;
        _storyTaggingOptions = storyTaggingOptions;
        _healthMonitor = healthMonitor;
        _soundSearchService = soundSearchService;
        _mainCommands = new StoryMainCommands(this);
    }

    internal DatabaseService Database => _database;
    internal ILogger<StoriesService>? Logger => _logger;
    internal ILangChainKernelFactory? KernelFactory => _kernelFactory;
    internal ICustomLogger? CustomLogger => _customLogger;
    internal ICommandDispatcher? CommandDispatcher => _commandDispatcher;
    internal CommandTuningOptions Tuning => _tuning;
    internal IServiceScopeFactory? ScopeFactory => _scopeFactory;

    internal bool IsCurrentDispatcherRunStatusChain()
    {
        var runId = CurrentDispatcherRunId;
        if (string.IsNullOrWhiteSpace(runId) || _commandDispatcher == null)
        {
            return false;
        }

        try
        {
            var snapshot = _commandDispatcher.GetActiveCommands()
                .FirstOrDefault(s => string.Equals(s.RunId, runId, StringComparison.OrdinalIgnoreCase));
            if (snapshot?.Metadata == null)
            {
                return false;
            }

            if (snapshot.Metadata.TryGetValue("chainMode", out var chainMode) &&
                string.Equals(chainMode, "1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ThreadScope) &&
                snapshot.ThreadScope.StartsWith("story/status_chain", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch
        {
            // best-effort
        }

        return false;
    }

    public long SaveGeneration(string prompt, StoryGenerationResult r, string? memoryKey = null)
    {
        return _database.SaveGeneration(prompt, r, memoryKey);
    }

    public List<StoryRecord> GetAllStories()
    {
        var stories = _database.GetAllStories();
        // Populate test info for each story
        foreach (var story in stories)
        {
            var testInfo = _database.GetTestInfoForStory(story.Id);
            story.TestRunId = testInfo.runId;
            story.TestStepId = testInfo.stepId;
        }
        return stories;
    }

    public void Delete(long id)
    {
        _database.DeleteStoryById(id);
    }

    public bool DeleteStoryCompletely(long storyId, out string? message)
    {
        message = null;
        try
        {
            _database.DeleteStoryById(storyId);

            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public bool DeleteStoryCompletelySingle(long storyId, out string? message)
    {
        return DeleteStoryCompletelySingle(storyId, trigger: null, out message);
    }

    public bool DeleteStoryCompletelySingle(long storyId, string? trigger, out string? message)
    {
        message = null;
        try
        {
            if (string.Equals(trigger, "idle_auto_delete", StringComparison.OrdinalIgnoreCase))
            {
                var story = _database.GetStoryById(storyId);
                if (story == null)
                {
                    message = "Story non trovata";
                    return false;
                }

                if (!string.Equals(story.Status, "evaluated", StringComparison.OrdinalIgnoreCase))
                {
                    message = $"Story non evaluated (status={story.Status})";
                    return false;
                }

                var opts = _idleAutoOptions?.CurrentValue ?? new AutomaticOperationsOptions();
                var minEvaluations = Math.Max(1, opts.AutoDeleteLowRated.MinEvaluations);
                var minAvg = opts.AutoDeleteLowRated.MinAverageScore;
                var evals = _database.GetStoryEvaluations(storyId) ?? new List<StoryEvaluation>();
                if (evals.Count < minEvaluations)
                {
                    message = $"Valutazioni insufficienti ({evals.Count}/{minEvaluations})";
                    return false;
                }

                var avg = evals.Average(e => e.TotalScore);
                var avgNormalized = DatabaseService.NormalizeEvaluationScoreTo100(avg);
                if (avgNormalized >= minAvg)
                {
                    message = $"Media valutazioni {avgNormalized:F2} >= soglia {minAvg:F2}";
                    return false;
                }
            }

            _database.DeleteStoryRowById(storyId);

            if (string.Equals(trigger, "idle_auto_delete", StringComparison.OrdinalIgnoreCase))
            {
                _database.AddWriterFailureForStory(storyId);
            }

            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public bool ApplyStatusTransitionAndCleanup(StoryRecord story, string statusCode, string? runId = null)
    {
        return ApplyStatusTransitionWithCleanup(story, statusCode, runId);
    }

    public bool ForceResetToEvaluatedAndCleanup(StoryRecord story, out string? message, string? runId = null)
    {
        message = null;
        if (story == null)
        {
            message = "Storia non trovata.";
            return false;
        }

        try
        {
            var evaluatedStatus = _database.GetStoryStatusByCode("evaluated");
            if (evaluatedStatus == null)
            {
                message = "Status 'evaluated' non trovato.";
                return false;
            }

            _database.UpdateStoryById(story.Id, statusId: evaluatedStatus.Id, updateStatus: true);
            var folderPath = ResolveStoryFolderPath(story);
            CleanupNextItemsForStatus(story.Id, "evaluated", folderPath);

            message = "Storia riportata a evaluated con cleanup degli output successivi.";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            _logger?.LogWarning(ex, "Errore reset a evaluated per story {StoryId}", story.Id);
            return false;
        }
    }

    public long InsertSingleStory(string prompt, string story, int? modelId = null, int? agentId = null, double score = 0.0, string? eval = null, int approved = 0, int? statusId = null, string? memoryKey = null, string? title = null, long? storyId = null, int? serieId = null, int? serieEpisode = null)
    {
        storyId ??= LogScope.CurrentStoryId;
        var newStoryId = _database.InsertSingleStory(prompt, story, storyId, modelId, agentId, score, eval, approved, statusId, memoryKey, title, serieId, serieEpisode);
        EnsureStoryStatusInserted(newStoryId);
        _ = EnqueueReviseStoryCommand(newStoryId, trigger: "auto_insert", priority: 2, force: false);
        return newStoryId;
    }

    private void EnsureStoryStatusInserted(long storyId)
    {
        if (storyId <= 0) return;
        var statusId = ResolveStatusId("inserted");
        if (!statusId.HasValue) return;
        try
        {
            _database.UpdateStoryById(storyId, statusId: statusId.Value, updateStatus: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Impossibile impostare lo stato inserted per story {StoryId}", storyId);
        }
    }

    public bool IsTaggedVoiceAutoLaunchEnabled()
        => _storyTaggingOptions?.CurrentValue?.TaggedVoice?.AutolaunchNextCommand ?? true;

    public bool IsTaggedAmbientAutoLaunchEnabled()
        => _storyTaggingOptions?.CurrentValue?.TaggedAmbient?.AutolaunchNextCommand ?? true;

    public bool IsTaggedFxAutoLaunchEnabled()
        => _storyTaggingOptions?.CurrentValue?.TaggedFx?.AutolaunchNextCommand ?? true;

    public bool IsTaggedFinalAutoLaunchEnabled()
        => _storyTaggingOptions?.CurrentValue?.Tagged?.AutolaunchNextCommand ?? true;

    public bool IsTtsSchemaAutoLaunchEnabled()
        => _audioGenerationOptions?.CurrentValue?.Tts?.AutolaunchNextCommand
            ?? _ttsSchemaOptions?.CurrentValue?.AutolaunchNextCommand
            ?? true;

    public bool IsAmbienceGenerationEnabled()
        => _audioGenerationOptions?.CurrentValue?.Ambience?.Enabled ?? true;

    public bool IsAmbienceRequiredForNextStatus()
        => _audioGenerationOptions?.CurrentValue?.Ambience?.RequiredForNextStatus ?? true;

    public bool TryValidateTaggedVoice(StoryRecord? story, out string? reason)
    {
        reason = null;
        var options = _storyTaggingOptions?.CurrentValue?.TaggedVoice;
        if (options == null || story == null)
        {
            return true;
        }

        var text = story.StoryTagged ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "story_tagged vuoto";
            return false;
        }

        if (options.EnableDialogTagCheck)
        {
            var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in PersonaggioRegex.Matches(text))
            {
                var name = match.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    characters.Add(name);
                }
            }

            if (characters.Count < Math.Max(1, options.MinDistinctCharacters))
            {
                reason = $"{characters.Count} personaggi distinti, minimo {options.MinDistinctCharacters}";
                return false;
            }
        }

        if (options.EnableEmotionTagCheck)
        {
            var personCount = PersonaggioRegex.Matches(text).Count;
            var emotionCount = EmotionRegex.Matches(text).Count;
            if (personCount > 0 && emotionCount < personCount)
            {
                reason = $"{emotionCount} tag [EMOZIONE], servono almeno {personCount}";
                return false;
            }
        }

        return true;
    }

    public bool TryValidateTaggedAmbient(StoryRecord? story, out string? reason)
    {
        reason = null;
        var options = _storyTaggingOptions?.CurrentValue?.TaggedAmbient;
        if (options == null || !options.EnableAmbientTagCheck || story == null)
        {
            return true;
        }

        var text = story.StoryTagged ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "story_tagged vuoto";
            return false;
        }

        var ambientTags = AmbientRegex.Matches(text).Count;
        var paragraphs = Regex.Split(text.Trim(), @"\r?\n\s*\r?\n")
            .Count(p => !string.IsNullOrWhiteSpace(p));
        paragraphs = Math.Max(1, paragraphs);
        var density = (double)ambientTags / paragraphs;
        if (density < options.MinAmbientTagDensity)
        {
            reason = $"densità rumori {density:F2} < {options.MinAmbientTagDensity:F2}";
            return false;
        }

        return true;
    }

    public bool TryValidateTaggedFx(StoryRecord? story, out string? reason)
    {
        reason = null;
        var options = _storyTaggingOptions?.CurrentValue?.TaggedFx;
        if (options == null || !options.EnableFxTagCheck || story == null)
        {
            return true;
        }

        var text = story.StoryTagged ?? string.Empty;
        var fxTags = FxRegex.Matches(text).Count;
        if (fxTags < options.MinFxTagCount)
        {
            reason = $"tag FX {fxTags} < {options.MinFxTagCount}";
            return false;
        }

        return true;
    }

    public bool TryValidateTaggedMusic(StoryRecord? story, out string? reason)
    {
        reason = null;
        var options = _storyTaggingOptions?.CurrentValue?.Tagged;
        if (options == null || !options.EnableMusicTagCheck || story == null)
        {
            return true;
        }

        var text = story.StoryTagged ?? string.Empty;
        var musicTags = MusicRegex.Matches(text).Count;
        if (musicTags < options.MinMusicTagCount)
        {
            reason = $"tag MUSICA {musicTags} < {options.MinMusicTagCount}";
            return false;
        }

        return true;
    }

    public (bool Success, long? NewStoryId, string Message) TryCloneStoryFromRevised(long storyId)
    {
        var story = GetStoryById(storyId);
        if (story == null)
        {
            return (false, null, "Storia non trovata.");
        }

        var evaluations = _database.GetStoryEvaluations(storyId) ?? new List<StoryEvaluation>();
        if (evaluations.Count < 2)
        {
            return (false, null, "Servono almeno due valutazioni per creare una versione migliorata.");
        }

        var revised = story.StoryRevised;
        if (string.IsNullOrWhiteSpace(revised))
        {
            return (false, null, "La storia non ha una versione revisionata.");
        }

        var baseTitle = string.IsNullOrWhiteSpace(story.Title) ? $"Story {story.Id}" : story.Title;
        var cloneTitle = $"{baseTitle} (versione migliorata)";

        // Use the DB insert directly so we can finish clone initialization (summary/evaluations copy)
        // before enqueueing the automatic revision command for the new story.
        var evaluatedStatusId = ResolveStatusId("evaluated");

        var newStoryId = _database.InsertSingleStory(
            story.Prompt ?? string.Empty,
            revised,
            story.ModelId,
            story.AgentId,
            score: story.Score,
            eval: story.Eval,
            approved: story.Approved ? 1 : 0,
            statusId: evaluatedStatusId,
            memoryKey: null,
            title: cloneTitle,
            serieId: story.SerieId,
            serieEpisode: story.SerieEpisode,
            parentStoryId: story.Id);

        if (!evaluatedStatusId.HasValue)
        {
            EnsureStoryStatusInserted(newStoryId);
        }
        CopyStorySummaryToClone(story, newStoryId);
        CopyStoryEvaluationsToClone(evaluations, newStoryId);

        if (evaluatedStatusId.HasValue)
        {
            try
            {
                _database.UpdateStoryById(newStoryId, statusId: evaluatedStatusId.Value, updateStatus: true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Impossibile impostare lo stato evaluated sul clone {NewStoryId}", newStoryId);
            }
        }

        var reviseRunId = EnqueueReviseStoryCommand(newStoryId, trigger: "clone_from_revised", priority: 2, force: true);
        var message = string.IsNullOrWhiteSpace(reviseRunId)
            ? $"Nuova storia {newStoryId} creata dalla revisione (enqueue revisione non riuscito)."
            : $"Nuova storia {newStoryId} creata dalla revisione. Revisione accodata: {reviseRunId}.";
        return (true, newStoryId, message);
    }

    private void CopyStorySummaryToClone(StoryRecord sourceStory, long newStoryId)
    {
        if (newStoryId <= 0) return;
        if (sourceStory == null) return;
        if (string.IsNullOrWhiteSpace(sourceStory.Summary)) return;
        try
        {
            _database.UpdateStorySummary(newStoryId, sourceStory.Summary);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Impossibile copiare il summary da story {SourceStoryId} a story {NewStoryId}", sourceStory.Id, newStoryId);
        }
    }

    private void CopyStoryEvaluationsToClone(IEnumerable<StoryEvaluation>? sourceEvaluations, long newStoryId)
    {
        if (newStoryId <= 0 || sourceEvaluations == null) return;

        foreach (var evaluation in sourceEvaluations)
        {
            if (evaluation == null) continue;
            if (evaluation.Id <= 0) continue; // Skip synthetic entries (e.g. global coherence).

            try
            {
                int? modelId = null;
                if (evaluation.ModelId.HasValue &&
                    evaluation.ModelId.Value > 0 &&
                    evaluation.ModelId.Value <= int.MaxValue)
                {
                    modelId = (int)evaluation.ModelId.Value;
                }

                _database.AddStoryEvaluation(
                    newStoryId,
                    evaluation.NarrativeCoherenceScore,
                    evaluation.NarrativeCoherenceDefects ?? string.Empty,
                    evaluation.OriginalityScore,
                    evaluation.OriginalityDefects ?? string.Empty,
                    evaluation.EmotionalImpactScore,
                    evaluation.EmotionalImpactDefects ?? string.Empty,
                    evaluation.ActionScore,
                    evaluation.ActionDefects ?? string.Empty,
                    evaluation.TotalScore,
                    evaluation.RawJson ?? string.Empty,
                    modelId: modelId,
                    agentId: evaluation.AgentId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Impossibile copiare la valutazione {EvaluationId} su story {NewStoryId}", evaluation.Id, newStoryId);
            }
        }
    }

    public bool UpdateStoryById(long id, string? story = null, int? modelId = null, int? agentId = null, int? statusId = null, bool updateStatus = false, bool allowCreatorMetadataUpdate = false)
    {
        return _database.UpdateStoryById(id, story, modelId, agentId, statusId, updateStatus, allowCreatorMetadataUpdate);
    }

    public bool UpdateStoryCharacters(long storyId, string charactersJson)
    {
        return _database.UpdateStoryCharacters(storyId, charactersJson);
    }

    public List<StoryStatus> GetAllStoryStatuses()
    {
        return _database.ListAllStoryStatuses();
    }

    public bool TryChangeStatus(long storyId, string? functionName, string? runId = null)
    {
        if (storyId <= 0 || string.IsNullOrWhiteSpace(functionName))
            return true;

        var story = _database.GetStoryById(storyId);
        if (story == null)
        {
            _logger?.LogWarning("Story {StoryId} not found while trying status transition for function '{FunctionName}'", storyId, functionName);
            return false;
        }

        var normalizedFunction = NormalizeFunctionNameForStatusLookup(functionName);
        if (string.IsNullOrWhiteSpace(normalizedFunction))
            return true;

        var statuses = _database.ListAllStoryStatuses()
            .OrderBy(s => s.Step)
            .ThenBy(s => s.Id)
            .ToList();
        if (statuses.Count == 0)
            return true;

        var target = statuses.FirstOrDefault(s =>
            !string.IsNullOrWhiteSpace(s.FunctionName) &&
            string.Equals(NormalizeFunctionNameForStatusLookup(s.FunctionName), normalizedFunction, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            _logger?.LogDebug("No stories_status row found for function '{FunctionName}'", normalizedFunction);
            return true;
        }

        StoryStatus? current = null;
        if (story.StatusId.HasValue)
        {
            current = statuses.FirstOrDefault(s => s.Id == story.StatusId.Value);
        }

        var currentStep = current?.Step ?? int.MinValue;
        if (target.Step <= currentStep)
        {
            return true;
        }

        if (string.Equals(target.Code, "evaluated", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedFunction, "evaluate_story", StringComparison.OrdinalIgnoreCase))
        {
            var evaluations = _database.GetStoryEvaluations(storyId) ?? new List<StoryEvaluation>();
            if (evaluations.Count < 2)
            {
                _logger?.LogDebug("Skipping transition to evaluated for story {StoryId}: evaluations {Count}/2", storyId, evaluations.Count);
                return true;
            }
        }

        try
        {
            _database.UpdateStoryById(storyId, statusId: target.Id, updateStatus: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to update story {StoryId} to status {StatusCode} (function '{FunctionName}')", storyId, target.Code, normalizedFunction);
            return false;
        }

        if (!target.DeleteNextItems)
        {
            return true;
        }

        var folderPath = ResolveStoryFolderPath(story);
        try
        {
            CleanupNextItemsForStatus(storyId, target.Code ?? string.Empty, folderPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Cleanup for status {StatusCode} failed for story {StoryId}", target.Code, storyId);
        }

        return false;
    }

    private static string NormalizeFunctionNameForStatusLookup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value.Trim();
        var slash = text.LastIndexOf('/');
        if (slash >= 0 && slash < text.Length - 1)
        {
            text = text[(slash + 1)..];
        }

        if (long.TryParse(text, out _))
        {
            return string.Empty;
        }

        return text.Trim().ToLowerInvariant();
    }

    public StoryStatus? GetNextStatusForStory(StoryRecord story, IReadOnlyList<StoryStatus>? statuses = null)
    {
        statuses ??= _database.ListAllStoryStatuses();
        if (statuses == null || statuses.Count == 0 || story == null)
            return null;

        var ordered = statuses
            .OrderBy(s => s.Step)
            .ThenBy(s => s.Id)
            .ToList();

        StoryStatus? current = null;
        if (story.StatusId.HasValue)
        {
            current = ordered.FirstOrDefault(s => s.Id == story.StatusId.Value);
        }

        if (current == null)
        {
            return ordered.FirstOrDefault();
        }

        return ordered.FirstOrDefault(s => s.Step > current.Step);
    }

    public string? EnqueueAllNextStatusEnqueuer(
        long storyId,
        string trigger,
        int priority = 2,
        bool ignoreActiveChain = false,
        string? resetToStatusCodeOnFailure = null,
        bool deferAutoCompleteOnFailure = false)
    {
        var story = GetStoryById(storyId);
        if (story == null)
            return null;

        return EnqueueAllNextStatusEnqueuer(
            story,
            trigger,
            priority,
            ignoreActiveChain,
            resetToStatusCodeOnFailure,
            deferAutoCompleteOnFailure);
    }

    public string? EnqueueAllNextStatusEnqueuer(
        StoryRecord story,
        string trigger,
        int priority = 2,
        bool ignoreActiveChain = false,
        string? resetToStatusCodeOnFailure = null,
        bool deferAutoCompleteOnFailure = false)
    {
        if (story == null) return null;
        if (_commandDispatcher == null) return null;

        if (ignoreActiveChain)
        {
            StopStatusChain(story.Id);
        }

        return EnqueueStatusChain(story.Id, resetToStatusCodeOnFailure, deferAutoCompleteOnFailure);
    }

    private sealed class StatusChainState
    {
        public string ChainId { get; }
        public int? LastEnqueuedStatusId { get; set; }
        public string? ResetToStatusCodeOnFailure { get; }
        public bool DeferAutoCompleteOnFailure { get; }

        public StatusChainState(string chainId, string? resetToStatusCodeOnFailure = null, bool deferAutoCompleteOnFailure = false)
        {
            ChainId = chainId;
            ResetToStatusCodeOnFailure = string.IsNullOrWhiteSpace(resetToStatusCodeOnFailure)
                ? null
                : resetToStatusCodeOnFailure.Trim();
            DeferAutoCompleteOnFailure = deferAutoCompleteOnFailure;
        }
    }

    public bool IsStatusChainActive(long storyId, out string chainId)
    {
        chainId = string.Empty;
        if (storyId <= 0) return false;

        if (_statusChains.TryGetValue(storyId, out var state) && state != null && !string.IsNullOrWhiteSpace(state.ChainId))
        {
            chainId = state.ChainId;
            return true;
        }

        return false;
    }

    public void StopStatusChain(long storyId)
    {
        if (storyId <= 0) return;
        _statusChains.TryRemove(storyId, out _);
    }

    public string? EnqueueStatusChain(long storyId, string? resetToStatusCodeOnFailure = null, bool deferAutoCompleteOnFailure = false)
    {
        if (storyId <= 0) return null;
        if (_commandDispatcher == null) return null;

        if (IsStatusChainActive(storyId, out var existing))
        {
            return existing;
        }

        var story = GetStoryById(storyId);
        if (story == null) return null;

        var chainId = $"chain_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var state = new StatusChainState(chainId, resetToStatusCodeOnFailure, deferAutoCompleteOnFailure);
        if (!_statusChains.TryAdd(storyId, state))
        {
            return IsStatusChainActive(storyId, out var current) ? current : null;
        }

        // Enqueue the first step immediately.
        var started = TryAdvanceStatusChain(storyId, chainId);
        if (!started)
        {
            StopStatusChain(storyId);
            return null;
        }

        return chainId;
    }

    public bool TryAdvanceStatusChain(long storyId, string chainId)
    {
        if (storyId <= 0) return false;
        if (string.IsNullOrWhiteSpace(chainId)) return false;
        if (_commandDispatcher == null) return false;

        if (!_statusChains.TryGetValue(storyId, out var state) || state == null)
            return false;
        if (!string.Equals(state.ChainId, chainId, StringComparison.OrdinalIgnoreCase))
            return false;

        var story = GetStoryById(storyId);
        if (story == null)
        {
            StopStatusChain(storyId);
            return false;
        }

        var statuses = _database.ListAllStoryStatuses();
        var next = GetNextStatusForStory(story, statuses);
        if (next == null)
        {
            StopStatusChain(storyId);
            return false;
        }

        if (state.LastEnqueuedStatusId.HasValue && state.LastEnqueuedStatusId.Value == next.Id)
        {
            // Best-effort de-dup if we get multiple completion signals.
            return false;
        }

        var cmd = CreateCommandForStatus(next);
        if (cmd == null)
        {
            StopStatusChain(storyId);
            return false;
        }

        var runId = $"status_{next.Code ?? next.Id.ToString()}_{story.Id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var metadata = new Dictionary<string, string>
        {
            ["storyId"] = story.Id.ToString(),
            ["operation"] = next.FunctionName ?? next.Code ?? "status",
            ["chainId"] = state.ChainId,
            ["chainMode"] = "1",
            ["statusId"] = next.Id.ToString(),
            ["statusCode"] = next.Code ?? string.Empty
        };
        TryPopulateAgentMetadata(next, metadata);
        var batchStatusCommand = string.Equals(next.FunctionName, "generate_ambience_audio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(next.FunctionName, "generate_fx_audio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(next.FunctionName, "generate_music", StringComparison.OrdinalIgnoreCase);

        _commandDispatcher.Enqueue(
            next.FunctionName ?? next.Code ?? "status",
            async ctx =>
            {
                var latestStory = GetStoryById(story.Id);
                if (latestStory == null)
                    return new CommandResult(false, "Storia non trovata");

                using var runScope = BeginDispatcherRunScope(ctx.RunId, ctx.CancellationToken);
                var (success, message) = await ExecuteStoryCommandAsync(latestStory, cmd, next);
                
                // After successful command execution, automatically advance to next status in chain
                if (success)
                {
                    // Use a small delay to ensure status update is persisted before checking next step
                    await Task.Delay(100, ctx.CancellationToken);
                    TryAdvanceStatusChain(story.Id, chainId);
                }
                else
                {
                    HandleStatusChainFailure(story.Id, state, message);
                }
                
                return new CommandResult(success, message);
            },
            runId: runId,
            threadScope: $"story/status_chain/{story.Id}",
            metadata: metadata,
            priority: 2,
            batch: batchStatusCommand);

        state.LastEnqueuedStatusId = next.Id;
        return true;
    }

    public IReadOnlyList<long> DrainAutoCompleteDeferredFailures()
    {
        var ids = new HashSet<long>();
        while (_autoCompleteDeferredFailures.TryDequeue(out var storyId))
        {
            if (storyId > 0)
            {
                ids.Add(storyId);
            }
        }

        return ids.Count == 0 ? Array.Empty<long>() : ids.ToList();
    }

    private void HandleStatusChainFailure(long storyId, StatusChainState state, string? message)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(state.ResetToStatusCodeOnFailure))
            {
                TryResetStoryToStatusCode(storyId, state.ResetToStatusCodeOnFailure!, message);
            }

            if (state.DeferAutoCompleteOnFailure && storyId > 0)
            {
                _autoCompleteDeferredFailures.Enqueue(storyId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed handling status-chain failure for story {StoryId}", storyId);
        }
        finally
        {
            StopStatusChain(storyId);
        }
    }

    private void TryResetStoryToStatusCode(long storyId, string statusCode, string? reason = null)
    {
        if (storyId <= 0 || string.IsNullOrWhiteSpace(statusCode))
        {
            return;
        }

        try
        {
            var status = _database.GetStoryStatusByCode(statusCode);
            if (status == null)
            {
                _logger?.LogWarning("Status code {StatusCode} not found while resetting story {StoryId}", statusCode, storyId);
                return;
            }

            _database.UpdateStoryById(storyId, statusId: status.Id, updateStatus: true);
            _logger?.LogWarning(
                "Story {StoryId} reset to status {StatusCode} after status-chain failure. Reason: {Reason}",
                storyId,
                statusCode,
                string.IsNullOrWhiteSpace(reason) ? "(none)" : reason);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed resetting story {StoryId} to status {StatusCode}", storyId, statusCode);
        }
    }

    public StoryStatus? GetStoryStatusById(int id)
    {
        return _database.GetStoryStatusById(id);
    }

    public StoryStatus? GetStoryStatusByCode(string? code)
    {
        return _database.GetStoryStatusByCode(code);
    }

    public int? ResolveStatusId(string? statusCode)
    {
        if (string.IsNullOrWhiteSpace(statusCode)) return null;
        try { return _database.GetStoryStatusByCode(statusCode)?.Id; }
        catch { return null; }
    }

    public StoryRecord? GetStoryById(long id)
    {
        var story = _database.GetStoryById(id);
        if (story == null) return null;
        try
        {
            story.Evaluations = _database.GetStoryEvaluations(id);
            var testInfo = _database.GetTestInfoForStory(id);
            story.TestRunId = testInfo.runId;
            story.TestStepId = testInfo.stepId;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load evaluations for story {Id}", id);
        }
        return story;
    }

    public (bool success, string message) ResetStoryAudioPipelineToTtsGenerated(long storyId, string? runId = null)
    {
        if (storyId <= 0) return (false, "storyId non valido");
        var story = GetStoryById(storyId);
        if (story == null) return (false, $"Storia {storyId} non trovata");

        try
        {
            var allowNext = ApplyStatusTransitionWithCleanup(story, "tts_generated", runId);
            if (!allowNext)
            {
                _customLogger?.Append(runId ?? "live-logs", $"[{storyId}] Reset a tts_generated con cleanup dati audio successivi");
            }
            return (true, "Reset a tts_generated completato");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Reset audio pipeline a tts_generated fallito per storyId={StoryId}", storyId);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Scan the stories_folder for existing final_mix.wav files and set the story status
    /// to `audio_master_generated` when appropriate. Returns the number of stories updated.
    /// </summary>
    public async Task<int> ScanAndMarkAudioMastersAsync()
    {
        var baseFolder = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder");
        if (!Directory.Exists(baseFolder))
            return 0;

        var dirs = Directory.GetDirectories(baseFolder);
        var stories = _database.GetAllStories() ?? new List<StoryRecord>();
        var byFolder = stories.Where(s => !string.IsNullOrWhiteSpace(s.Folder))
                             .ToDictionary(s => s.Folder!, s => s, StringComparer.OrdinalIgnoreCase);

        var audioStatus = GetStoryStatusByCode("audio_master_generated");
        int updatedCount = 0;

        foreach (var dir in dirs)
        {
            try
            {
                var folderName = Path.GetFileName(dir);
                var finalMix = Path.Combine(dir, "final_mix.wav");
                if (!File.Exists(finalMix))
                    continue;

                if (!byFolder.TryGetValue(folderName, out var story))
                {
                    _customLogger?.Append("live-logs", $"[Scan] Folder '{folderName}' contains final_mix.wav but no DB story found.");
                    continue;
                }

                if (audioStatus == null)
                {
                    _customLogger?.Append("live-logs", "[Scan] Status code 'audio_master_generated' not found in DB.");
                    break;
                }

                if (story.StatusId.HasValue && story.StatusId.Value == audioStatus.Id)
                {
                    // already set
                    continue;
                }

                var ok = _database.UpdateStoryById(story.Id, statusId: audioStatus.Id, updateStatus: true);
                if (ok)
                {
                    updatedCount++;
                    _customLogger?.Append("live-logs", $"[Scan] Story {story.Id} ('{folderName}') status updated to {audioStatus.Code}.");
                }
                else
                {
                    _customLogger?.Append("live-logs", $"[Scan] Failed to update story {story.Id} ('{folderName}').");
                }
            }
            catch (Exception ex)
            {
                _customLogger?.Append("live-logs", $"[Scan] Exception scanning '{dir}': {ex.Message}");
            }
        }

        await Task.CompletedTask;
        return updatedCount;
    }

    private Task<(bool success, string? message)> ExecuteStoryCommandAsync(long storyId, IStoryCommand command)
    {
        var story = GetStoryById(storyId);
        if (story == null)
            return Task.FromResult<(bool, string?)>((false, "Storia non trovata"));

        return ExecuteStoryCommandAsync(story, command, null);
    }

    private async Task<(bool success, string? message)> ExecuteStoryCommandAsync(StoryRecord story, IStoryCommand command, StoryStatus? targetStatus = null)
    {
        var cancellationToken = CurrentDispatcherCancellationToken ?? CancellationToken.None;
        cancellationToken.ThrowIfCancellationRequested();

        if (command.RequireStoryText && string.IsNullOrWhiteSpace(story.StoryRaw))
            return (false, "La storia non contiene testo");

        string folderPath = string.Empty;
        if (command.EnsureFolder)
        {
            folderPath = EnsureStoryFolder(story);
        }
        else if (!string.IsNullOrWhiteSpace(story.Folder))
        {
            folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder);
            Directory.CreateDirectory(folderPath);
        }

        var context = new StoryCommandContext(story, folderPath, targetStatus, cancellationToken);
        var result = await command.ExecuteAsync(context);

        if (result.success && targetStatus?.Id > 0 && !command.HandlesStatusTransition)
        {
            try
            {
                _database.UpdateStoryById(story.Id, statusId: targetStatus.Id, updateStatus: true);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to update story {Id} to status {StatusId}", story.Id, targetStatus.Id);
            }
        }

        return result;
    }

    public List<StoryEvaluation> GetEvaluationsForStory(long storyId)
    {
        return _database.GetStoryEvaluations(storyId);
    }

    public async Task<(bool success, string? message)> ExecuteNextStatusOperationAsync(long storyId, string? currentRunId = null)
    {
        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Storia non trovata");

        var statuses = _database.ListAllStoryStatuses();
        var nextStatus = GetNextStatusForStory(story, statuses);
        if (nextStatus == null)
            return (false, "Nessuno stato successivo disponibile");

        var command = CreateCommandForStatus(nextStatus);
        if (command == null)
            return (false, $"Operazione non supportata per lo stato {nextStatus.Code ?? nextStatus.Id.ToString()}");

        // Update operation name in the command dispatcher to show the actual command being executed
        if (!string.IsNullOrWhiteSpace(currentRunId) && _commandDispatcher != null)
        {
            var operationName = GetOperationNameForStatus(nextStatus);
            if (!string.IsNullOrWhiteSpace(operationName))
            {
                _commandDispatcher.UpdateOperationName(currentRunId, operationName);
            }
        }

        return await ExecuteStoryCommandAsync(story, command, nextStatus);
    }

    public void SaveChapter(string memoryKey, int chapterNumber, string content)
    {
        _database.SaveChapter(memoryKey, chapterNumber, content);
    }

    public async Task<(bool success, double score, string? error)> EvaluateStoryWithAgentAsync(long storyId, int agentId, bool forceStandardFlow = false)
    {
        if (_kernelFactory == null)
            return (false, 0, "Kernel factory non disponibile");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, 0, "Storia non trovata");

        var storyText = !string.IsNullOrWhiteSpace(story.StoryRevised)
            ? story.StoryRevised
            : story.StoryRaw;

        if (string.IsNullOrWhiteSpace(storyText))
            return (false, 0, "Storia non trovata o priva di contenuto");

        var agent = _database.GetAgentById(agentId);
        if (agent == null || !agent.IsActive)
            return (false, 0, "Agente valutatore non trovato");

        if (!agent.ModelId.HasValue)
            return (false, 0, "Modello non configurato per l'agente valutatore");

        var modelInfo = _database.GetModelInfoById(agent.ModelId.Value);
        var modelName = modelInfo?.Name;
        if (string.IsNullOrWhiteSpace(modelName))
            return (false, 0, "Modello associato all'agente non disponibile");

        var allowedPlugins = ParseAgentSkills(agent)?.ToList() ?? new List<string>();

        var runId = $"storyeval_{story.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        var evaluationOpId = LogScope.GenerateOperationId();
        using var scope = LogScope.Push($"story_evaluation_{story.Id}", evaluationOpId, null, null, agent.Description, agent.Role, storyId: story.Id);

        try
        {

            async Task<(bool success, double score, string? error)> ExecuteEvaluationWithModelAsync(
                string evaluationModelName,
                int evaluationModelId,
                string? instructionsOverride,
                double? topPOverride,
                int? topKOverride,
                bool? thinkOverride)
            {
                try
                {
                    var orchestrator = _kernelFactory.CreateOrchestrator(evaluationModelName, allowedPlugins, agent.Id);
                    var evaluatorTool = orchestrator.GetTool<EvaluatorTool>("evaluate_full_story");
                    if (evaluatorTool != null)
                    {
                        evaluatorTool.CurrentStoryId = story.Id;
                    }

                    // Set CurrentStoryId for coherence evaluation tools
                    var chunkFactsTool = orchestrator.GetTool<ChunkFactsExtractorTool>("extract_chunk_facts");
                    if (chunkFactsTool != null)
                    {
                        chunkFactsTool.CurrentStoryId = story.Id;
                    }

                    var coherenceTool = orchestrator.GetTool<CoherenceCalculatorTool>("calculate_coherence");
                    if (coherenceTool != null)
                    {
                        coherenceTool.CurrentStoryId = story.Id;
                    }

                    var isStoryEvaluatorTextProtocol =
                        forceStandardFlow ||
                        (agent.Role?.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) ?? false);
                    var baseSystemMessage = (isStoryEvaluatorTextProtocol
                        ? StoriesServiceDefaults.EvaluatorTextProtocolInstructions
                        : (!string.IsNullOrWhiteSpace(agent.SystemPrompt) ? agent.SystemPrompt : ComposeSystemMessage(agent)))
                        ?? string.Empty;
                    var systemMessage = !string.IsNullOrWhiteSpace(instructionsOverride)
                        ? instructionsOverride!
                        : baseSystemMessage;

                    var effectiveAgent = new Agent
                    {
                        Id = agent.Id,
                        Description = agent.Description,
                        Role = agent.Role,
                        ModelId = evaluationModelId,
                        ModelName = evaluationModelName,
                        VoiceId = agent.VoiceId,
                        Skills = agent.Skills,
                        Config = agent.Config,
                        JsonResponseFormat = agent.JsonResponseFormat,
                        UserPrompt = agent.UserPrompt,
                        SystemPrompt = agent.SystemPrompt,
                        ExecutionPlan = agent.ExecutionPlan,
                        IsActive = agent.IsActive,
                        CreatedAt = agent.CreatedAt,
                        UpdatedAt = agent.UpdatedAt,
                        Notes = agent.Notes,
                        Temperature = agent.Temperature,
                        TopP = topPOverride ?? agent.TopP,
                        RepeatPenalty = agent.RepeatPenalty,
                        TopK = topKOverride ?? agent.TopK,
                        RepeatLastN = agent.RepeatLastN,
                        NumPredict = agent.NumPredict,
                        Thinking = thinkOverride ?? agent.Thinking,
                        MultiStepTemplateId = agent.MultiStepTemplateId,
                        SortOrder = agent.SortOrder,
                        AllowedProfiles = agent.AllowedProfiles
                    };

                    // Use different prompt based on agent role
                    bool isCoherenceEvaluation = agent.Role?.Equals("coherence_evaluator", StringComparison.OrdinalIgnoreCase) ?? false;
                    var prompt = BuildStoryEvaluationPrompt(story, isCoherenceEvaluation);

                    var beforeEvaluations = _database.GetStoryEvaluations(storyId)
                        .Select(e => e.Id)
                        .ToHashSet();

                    if (isStoryEvaluatorTextProtocol)
                    {
                        var protocolResult = await EvaluateStoryWithTextProtocolAsync(
                            story,
                            effectiveAgent,
                            systemMessage,
                            modelId: evaluationModelId,
                            agentId: effectiveAgent.Id).ConfigureAwait(false);

                        if (!protocolResult.success)
                        {
                            var error = protocolResult.error ?? "Valutazione fallita";
                            _customLogger?.Append(runId, $"[{storyId}] Valutazione fallita: {error}", "Error");
                            return (false, 0, error);
                        }
                    }
                    else
                    {
                        if (!allowedPlugins.Any())
                            return (false, 0, "L'agente valutatore non ha strumenti abilitati");

                        var reactAgent = effectiveAgent;

                        var reactExecution = await ExecuteReActWithStandardPathAsync(
                            runId: runId,
                            roleCode: agent.Role ?? "story_evaluator",
                            agent: reactAgent,
                            modelName: evaluationModelName,
                            prompt: prompt,
                            systemMessage: systemMessage,
                            responseChecker: _responseChecker,
                            maxIterations: 100,
                            orchestratorFactory: () =>
                            {
                                var o = _kernelFactory.CreateOrchestrator(evaluationModelName, allowedPlugins, agent.Id);
                                var evaluator = o.GetTool<EvaluatorTool>("evaluate_full_story");
                                if (evaluator != null)
                                {
                                    evaluator.CurrentStoryId = story.Id;
                                }

                                var chunkFacts = o.GetTool<ChunkFactsExtractorTool>("extract_chunk_facts");
                                if (chunkFacts != null)
                                {
                                    chunkFacts.CurrentStoryId = story.Id;
                                }

                                var coherence = o.GetTool<CoherenceCalculatorTool>("calculate_coherence");
                                if (coherence != null)
                                {
                                    coherence.CurrentStoryId = story.Id;
                                }

                                return o;
                            },
                            ct: CancellationToken.None).ConfigureAwait(false);

                        if (!reactExecution.success)
                        {
                            var error = reactExecution.error ?? "Valutazione fallita";
                            _customLogger?.Append(runId, $"[{storyId}] Valutazione fallita: {error}", "Error");
                            return (false, 0, error);
                        }
                    }

                    // Check results based on agent type
                    if (isCoherenceEvaluation)
                    {
                        // For coherence evaluation, check if global coherence was saved
                        var globalCoherence = _database.GetGlobalCoherence((int)storyId);
                        if (globalCoherence == null)
                        {
                            var msg = "L'agente non ha salvato la coerenza globale.";
                            _customLogger?.Append(runId, $"[{storyId}] {msg}", "Warn");
                            TryLogEvaluationResult(runId, storyId, agent?.Description, success: false, msg);
                            return (false, 0, msg);
                        }

                        var score = globalCoherence.GlobalCoherenceValue * 10; // Convert 0-1 to 0-10 scale
                        _customLogger?.Append(runId, $"[{storyId}] Valutazione di coerenza completata. Score: {score:F2}");
                        TryLogEvaluationResult(runId, storyId, agent?.Description, success: true, $"Valutazione di coerenza completata. Score: {score:F2}");
                        var allowNext = true;
                        var (count, average) = _database.GetStoryEvaluationStats(storyId);
                        TrySendStoryAfterEvaluationEmail(storyId, count, average);
                        if (count >= 2)
                        {
                            allowNext = TryTransitionStoryToEvaluatedIfRevised(storyId, runId);
                        }
                        if (allowNext)
                        {
                            TryEnqueueAutoFormatAfterEvaluation(storyId);
                        }
                        return (true, score, null);
                    }
                    else
                    {
                        // For standard evaluation, check stories_evaluations table
                        var afterEvaluations = _database.GetStoryEvaluations(storyId)
                            .Where(e => !beforeEvaluations.Contains(e.Id))
                            .OrderBy(e => e.Id)
                            .ToList();

                        if (afterEvaluations.Count == 0)
                        {
                            var msg = "L'agente non ha salvato alcuna valutazione.";
                            _customLogger?.Append(runId, $"[{storyId}] {msg}", "Warn");
                            TryLogEvaluationResult(runId, storyId, agent?.Description, success: false, msg);
                            return (false, 0, msg);
                        }

                        var avgScore = afterEvaluations.Average(e => e.TotalScore);
                        _customLogger?.Append(runId, $"[{storyId}] Valutazione completata. Score medio: {avgScore:F2}");
                        TryLogEvaluationResult(runId, storyId, agent?.Description, success: true, $"Valutazione completata. Score medio: {avgScore:F2}");

                        // Se score > 60% (su scala 0-40), avvia riassunto automatico con priorita' bassa
                        if (avgScore > 24 && _commandDispatcher != null)
                        {
                            try
                            {
                                var summarizerAgent = _database.ListAgents()
                                    .FirstOrDefault(a => a.Role == "summarizer" && a.IsActive);

                                if (summarizerAgent != null)
                                {
                                    var summarizeRunId = Guid.NewGuid().ToString();
                                    var cmd = new Commands.SummarizeStoryCommand(
                                        storyId,
                                        _database,
                                        _kernelFactory!,
                                        _customLogger!,
                                        scopeFactory: _scopeFactory);

                                    _commandDispatcher.Enqueue(
                                        "SummarizeStory",
                                        async ctx =>
                                        {
                                            bool success = await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId);
                                            var message = success
                                                ? "Summary generated"
                                                : (string.IsNullOrWhiteSpace(cmd.LastError) ? "Failed to generate summary" : cmd.LastError);
                                            return new CommandResult(success, message);
                                        },
                                        runId: summarizeRunId,
                                        metadata: new Dictionary<string, string>
                                        {
                                            ["storyId"] = storyId.ToString(),
                                            ["storyTitle"] = story.Title ?? "Untitled",
                                            ["triggeredBy"] = "auto_evaluation",
                                            ["evaluationScore"] = avgScore.ToString("F2")
                                        },
                                        priority: 3);

                                    _customLogger?.Append(runId, $"[{storyId}] Auto-summarization enqueued (score: {avgScore:F2})");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, "Failed to enqueue auto-summarization for story {StoryId}", storyId);
                            }
                        }

                        var allowNext = true;
                        var (count, average) = _database.GetStoryEvaluationStats(storyId);
                        TrySendStoryAfterEvaluationEmail(storyId, count, average);
                        if (count >= 2)
                        {
                            allowNext = TryTransitionStoryToEvaluatedIfRevised(storyId, runId);
                        }
                        if (allowNext)
                        {
                            TryEnqueueAutoFormatAfterEvaluation(storyId);
                        }

                        return (true, avgScore, null);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Errore durante EvaluateStoryWithAgent per storia {StoryId} agente {AgentId}", storyId, agentId);
                    TryLogEvaluationResult(runId, storyId, agent?.Description, success: false, ex.Message);
                    return (false, 0, ex.Message);
                }
            }

            var primaryResult = await ExecuteEvaluationWithModelAsync(
                modelName,
                agent.ModelId.Value,
                agent.SystemPrompt,
                agent.TopP,
                agent.TopK,
                agent.Thinking);

            if (primaryResult.success)
            {
                return primaryResult;
            }

            if (_scopeFactory == null)
            {
                return primaryResult;
            }

            using var fallbackScope = _scopeFactory.CreateScope();
            var fallbackService = fallbackScope.ServiceProvider.GetService<ModelFallbackService>();
            if (fallbackService == null)
            {
                _customLogger?.Append(runId, "ModelFallbackService not available; cannot fallback.", "warn");
                return primaryResult;
            }

            var roleCode = string.IsNullOrWhiteSpace(agent.Role) ? "story_evaluator" : agent.Role!;
            var triedModelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                modelName
            };

            var (fallbackResult, successfulModelRole) = await fallbackService.ExecuteWithFallbackAsync(
                roleCode,
                agent.ModelId,
                async modelRole =>
                {
                    var fallbackModelName = modelRole.Model?.Name;
                    if (string.IsNullOrWhiteSpace(fallbackModelName))
                    {
                        throw new InvalidOperationException("Fallback ModelRole has no Model.Name");
                    }

                    return await ExecuteEvaluationWithModelAsync(
                        fallbackModelName,
                        modelRole.ModelId,
                        string.IsNullOrWhiteSpace(modelRole.Instructions) ? agent.SystemPrompt : modelRole.Instructions,
                        modelRole.TopP ?? agent.TopP,
                        modelRole.TopK ?? agent.TopK,
                        modelRole.Thinking ?? agent.Thinking);
                },
                validateResult: r => r.success,
                shouldTryModelRole: modelRole =>
                {
                    var name = modelRole.Model?.Name;
                    return !string.IsNullOrWhiteSpace(name) && triedModelNames.Add(name);
                },
                agentId: agent.Id);

            if (fallbackResult.success && successfulModelRole?.Model != null)
            {
                _customLogger?.Append(runId, $"[{storyId}] Fallback model succeeded: {successfulModelRole.Model.Name} (role={roleCode})");
                return fallbackResult;
            }

            return primaryResult;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante EvaluateStoryWithAgent per storia {StoryId} agente {AgentId}", storyId, agentId);
            TryLogEvaluationResult(runId, storyId, agent?.Description, success: false, ex.Message);
            return (false, 0, ex.Message);
        }
        finally
        {
            _customLogger?.MarkCompleted(runId);
        }

    }

    private async Task<(bool success, double score, string? error)> EvaluateStoryWithTextProtocolAsync(
        StoryRecord story,
        Agent evaluatorAgent,
        string systemMessage,
        int? modelId,
        int agentId)
    {
        if (story == null) return (false, 0, "Storia non trovata");
        var storyText = !string.IsNullOrWhiteSpace(story.StoryRevised)
            ? story.StoryRevised
            : story.StoryRaw;

        if (string.IsNullOrWhiteSpace(storyText)) return (false, 0, "Storia non trovata o priva di contenuto");

        if (_scopeFactory == null) return (false, 0, "IServiceScopeFactory non disponibile");
        using var scope = _scopeFactory.CreateScope();
        var callCenter = scope.ServiceProvider.GetService<ICallCenter>()
            ?? ServiceLocator.Services?.GetService<ICallCenter>();
        if (callCenter == null) return (false, 0, "ICallCenter non disponibile");

        var contextLimitTokens = ResolveEvaluatorContextLimitTokens(modelId);
        // 4 sezioni + spiegazioni brevi richiedono un margine più ampio di 96 token.
        // Un budget troppo basso porta a output troncati e sezioni finali mancanti.
        const int expectedOutputTokens = 256;
        const int maxContextReductionRetries = 3;
        var contextReductionRetry = 0;
        var hadContextLimitFailure = false;
        var storyTextForEvaluation = BuildEvaluationStoryTextForBudget(
            storyText,
            systemMessage,
            contextLimitTokens,
            expectedOutputTokens,
            extraSafetyTokens: 0);

        if (!string.Equals(storyTextForEvaluation, storyText, StringComparison.Ordinal))
        {
            _customLogger?.Append(
                $"evaluate_story_{story.Id}_agent_{agentId}",
                $"[{story.Id}] story_evaluator pre-trim attivato: chars {storyText.Length} -> {storyTextForEvaluation.Length}; context_limit={contextLimitTokens}; output_budget={expectedOutputTokens}");
        }

        var messages = new List<ConversationMessage>
        {
            new ConversationMessage { Role = "system", Content = systemMessage }
        };
        const int maxAttemptsFinalEvaluation = 3;

        // Single-shot evaluation: send the full story once and expect immediate formatted evaluation.
        // Allow a few attempts to recover if the model uses the wrong format.
        messages.Add(new ConversationMessage
        {
            Role = "user",
            Content = BuildStoryEvaluatorUserMessage(storyTextForEvaluation)
        });

        var parsed = new ParsedEvaluation(0, string.Empty, 0, string.Empty, 0, string.Empty, 0, string.Empty);
        string? parseError = null;
        string evalText = string.Empty;

        var evalOk = false;
        for (var attempt = 1; attempt <= maxAttemptsFinalEvaluation; attempt++)
        {
            var stepId = RequestIdGenerator.Generate();
            _logger?.LogInformation("[StepID: {StepId}][Story: {StoryId}] Valutazione tentativo {Attempt}/{Max}", stepId, story.Id, attempt, maxAttemptsFinalEvaluation);

            var history = new ChatHistory(messages);
            var threadId = unchecked((((int)(story.Id % int.MaxValue)) * 541) ^ attempt);
            var evalResult = await callCenter.CallAgentAsync(
                storyId: story.Id,
                threadId: threadId,
                agent: evaluatorAgent,
                history: history,
                options: new CallOptions
                {
                    Operation = "story_evaluation",
                    Timeout = TimeSpan.FromSeconds(120),
                    MaxRetries = 0,
                    UseResponseChecker = false,
                    AllowFallback = true,
                    AskFailExplanation = true
                }).ConfigureAwait(false);

            if (!evalResult.Success)
            {
                var failureReason = string.IsNullOrWhiteSpace(evalResult.FailureReason)
                    ? "Valutazione fallita."
                    : evalResult.FailureReason!;

                if (IsProviderContextLimitError(failureReason))
                {
                    hadContextLimitFailure = true;
                    if (contextReductionRetry < maxContextReductionRetries)
                    {
                        contextReductionRetry++;
                        var parsedOutputTokens = 0;
                        var parsedInputTokens = 0;
                        var parsedSafetyTokens = 0;

                        if (TryParseProviderContextLimitError(
                                failureReason,
                                out var parsedContextLimit,
                                out parsedOutputTokens,
                                out parsedInputTokens,
                                out parsedSafetyTokens))
                        {
                            if (parsedContextLimit > 0)
                            {
                                contextLimitTokens = parsedContextLimit;
                            }
                        }

                        var effectiveOutputBudget = parsedOutputTokens > 0 ? parsedOutputTokens : expectedOutputTokens;
                        storyTextForEvaluation = BuildEvaluationStoryTextForBudget(
                            storyText,
                            systemMessage,
                            contextLimitTokens,
                            effectiveOutputBudget,
                            extraSafetyTokens: Math.Max(128 * contextReductionRetry, parsedSafetyTokens > 0 ? parsedSafetyTokens : 0));

                        messages = new List<ConversationMessage>
                        {
                            new ConversationMessage { Role = "system", Content = systemMessage },
                            new ConversationMessage { Role = "user", Content = BuildStoryEvaluatorUserMessage(storyTextForEvaluation) }
                        };

                        _customLogger?.Append(
                            $"evaluate_story_{story.Id}_agent_{agentId}",
                            $"[{story.Id}] Context limit retry {contextReductionRetry}/{maxContextReductionRetries}: nuova lunghezza testo={storyTextForEvaluation.Length}; context_limit={contextLimitTokens}; output_budget={effectiveOutputBudget}; input_stimato={parsedInputTokens}; margine={parsedSafetyTokens}");
                        continue;
                    }

                    parseError = failureReason;
                    break;
                }

                _customLogger?.MarkLatestModelResponseResult("FAILED", failureReason);
                return (false, 0, failureReason);
            }

            evalText = NormalizeEvaluatorOutput((evalResult.ResponseText ?? string.Empty));
            messages.Add(new ConversationMessage { Role = "assistant", Content = evalText });
                if (TryParseEvaluationText(evalText, out parsed, out parseError))
                {
                    _customLogger?.MarkLatestModelResponseResult(
                        "SUCCESS",
                        null);
                    evalOk = true;
                    break;
                }
                _customLogger?.MarkLatestModelResponseResult("FAILED", parseError);

                if (attempt < maxAttemptsFinalEvaluation)
                {
                    messages.Add(new ConversationMessage
                    {
                        Role = "user",
                        Content =
                            "Formato non valido. Devi rispondere SOLO nel formato obbligatorio, senza markup né elenchi. " +
                            "Ripeti la valutazione rispettando esattamente le 4 intestazioni e i punteggi 1-5." +
                            " Le intestazioni richieste sono (in questo ordine):\nCoerenza narrativa\nOriginalità\nImpatto emotivo\nAzione"
                    });
                }
        }

        if (!evalOk)
        {
            if (hadContextLimitFailure)
            {
                var chunked = await EvaluateStoryWithChunkedTextProtocolAsync(
                    story,
                    evaluatorAgent,
                    systemMessage,
                    callCenter,
                    contextLimitTokens,
                    expectedOutputTokens).ConfigureAwait(false);

                if (chunked.success)
                {
                    parsed = chunked.parsed;
                    evalText = chunked.aggregatedText;
                    evalOk = true;
                }
                else
                {
                    return (false, 0, chunked.error ?? (parseError ?? "Formato valutazione non valido."));
                }
            }
            else
            {
            return (false, 0, parseError ?? "Formato valutazione non valido.");
            }
        }

        var totalScore = (double)(parsed.NarrativeScore10 + parsed.OriginalityScore10 + parsed.EmotionalScore10 + parsed.ActionScore10);
        var lengthPenaltyCharsLimit =
            string.Equals(evaluatorAgent.Role, "story_evaluator", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(0, _storyEvaluationOptions.LengthPenaltyNoPenaltyChars)
                : 0;

        long savedEvalId;
        try
        {
            savedEvalId = _database.AddStoryEvaluation(
                story.Id,
                parsed.NarrativeScore10, parsed.NarrativeExplanation,
                parsed.OriginalityScore10, parsed.OriginalityExplanation,
                parsed.EmotionalScore10, parsed.EmotionalExplanation,
                parsed.ActionScore10, parsed.ActionExplanation,
                totalScore,
                rawJson: evalText,
                modelId: modelId,
                agentId: agentId,
                lengthPenaltyCharsLimit: lengthPenaltyCharsLimit);
        }
        catch (Exception ex)
        {
            return (false, 0, $"Errore nel salvataggio della valutazione: {ex.Message}");
        }

        var savedScore = _database.GetStoryEvaluations(story.Id)
            .FirstOrDefault(e => e.Id == savedEvalId)?.TotalScore ?? totalScore;

        return (true, savedScore, null);
    }

    private int ResolveEvaluatorContextLimitTokens(int? modelId)
    {
        const int fallback = 8192;
        try
        {
            if (!modelId.HasValue || modelId.Value <= 0)
            {
                return fallback;
            }

            var model = _database.GetModelInfoById(modelId.Value);
            if (model == null)
            {
                return fallback;
            }

            var candidates = new[]
            {
                model.ContextToUse,
                model.MaxContextTokens,
                model.MaxContext
            };

            var resolved = candidates.FirstOrDefault(v => v > 0);
            return resolved > 0 ? resolved : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string BuildStoryEvaluatorUserMessage(string storyText)
    {
        var safeText = string.IsNullOrWhiteSpace(storyText) ? string.Empty : storyText.Trim();
        return $"TESTO:\n\n{safeText}\n\nVALUTA";
    }

    private static string BuildEvaluationStoryTextForBudget(
        string fullStoryText,
        string systemMessage,
        int contextLimitTokens,
        int expectedOutputTokens,
        int extraSafetyTokens)
    {
        var safeStory = fullStoryText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(safeStory))
        {
            return string.Empty;
        }

        var safeContextLimit = Math.Max(2048, contextLimitTokens);
        var safeOutputTokens = Math.Max(32, expectedOutputTokens);
        var systemTokens = EstimateTokenCount(systemMessage);
        var wrapperTokens = EstimateTokenCount("TESTO:\n\n\n\nVALUTA");
        var safetyTokens = 384 + Math.Max(0, extraSafetyTokens);
        var budgetForStoryTokens = safeContextLimit - safeOutputTokens - systemTokens - wrapperTokens - safetyTokens;

        if (budgetForStoryTokens < 256)
        {
            budgetForStoryTokens = 256;
        }

        return TrimTextToApproxTokenBudget(safeStory, budgetForStoryTokens);
    }

    private static string TrimTextToApproxTokenBudget(string text, int tokenBudget)
    {
        var safeText = text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(safeText))
        {
            return string.Empty;
        }

        var safeBudget = Math.Max(1, tokenBudget);
        var estimatedTokens = EstimateTokenCount(safeText);
        if (estimatedTokens <= safeBudget)
        {
            return safeText.Trim();
        }

        var source = safeText.Trim();
        if (source.Length <= 800)
        {
            return source;
        }

        var targetChars = Math.Max(400, safeBudget * 4);
        if (targetChars >= source.Length)
        {
            return source;
        }

        var headChars = Math.Max(200, (int)Math.Round(targetChars * 0.65));
        headChars = Math.Min(headChars, source.Length - 200);
        var tailChars = Math.Max(200, targetChars - headChars);
        if (headChars + tailChars > source.Length)
        {
            tailChars = Math.Max(200, source.Length - headChars);
        }

        var head = source[..headChars].TrimEnd();
        var tail = source[^tailChars..].TrimStart();
        return $"{head}\n\n[...testo omesso per vincolo contesto...]\n\n{tail}";
    }

    private static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        // Conservative estimate for mixed Italian text + punctuation + instructions.
        return Math.Max(1, (int)Math.Ceiling(text.Trim().Length / 3.0));
    }

    private static bool IsProviderContextLimitError(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.Contains("maximum context length", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("\"param\":\"input_tokens\"", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("input tokens", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("prompt troppo lungo per il contesto", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseProviderContextLimitError(
        string? reason,
        out int contextLimitTokens,
        out int outputTokens,
        out int inputTokens,
        out int safetyTokens)
    {
        contextLimitTokens = 0;
        outputTokens = 0;
        inputTokens = 0;
        safetyTokens = 0;
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var text = reason!;
        var ctxMatch = Regex.Match(text, @"maximum\s+context\s+length\s+is\s+(\d+)\s+tokens", RegexOptions.IgnoreCase);
        var outMatch = Regex.Match(text, @"requested\s+(\d+)\s+output\s+tokens", RegexOptions.IgnoreCase);
        var inMatch = Regex.Match(text, @"at\s+least\s+(\d+)\s+input\s+tokens", RegexOptions.IgnoreCase);
        var numCtxMatch = Regex.Match(text, @"num_ctx\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        var inputStimatoMatch = Regex.Match(text, @"input_stimato\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        var margineMatch = Regex.Match(text, @"margine_sicurezza\s*=\s*(\d+)", RegexOptions.IgnoreCase);

        if (ctxMatch.Success && int.TryParse(ctxMatch.Groups[1].Value, out var ctx))
        {
            contextLimitTokens = ctx;
        }
        else if (numCtxMatch.Success && int.TryParse(numCtxMatch.Groups[1].Value, out var ctxVllm))
        {
            contextLimitTokens = ctxVllm;
        }

        if (outMatch.Success && int.TryParse(outMatch.Groups[1].Value, out var output))
        {
            outputTokens = output;
        }

        if (inMatch.Success && int.TryParse(inMatch.Groups[1].Value, out var input))
        {
            inputTokens = input;
        }
        else if (inputStimatoMatch.Success && int.TryParse(inputStimatoMatch.Groups[1].Value, out var inputVllm))
        {
            inputTokens = inputVllm;
        }

        if (margineMatch.Success && int.TryParse(margineMatch.Groups[1].Value, out var margin))
        {
            safetyTokens = margin;
        }

        return contextLimitTokens > 0 || outputTokens > 0 || inputTokens > 0 || safetyTokens > 0;
    }

    private async Task<(bool success, ParsedEvaluation parsed, string aggregatedText, string? error)> EvaluateStoryWithChunkedTextProtocolAsync(
        StoryRecord story,
        Agent evaluatorAgent,
        string systemMessage,
        ICallCenter callCenter,
        int contextLimitTokens,
        int expectedOutputTokens)
    {
        var fullStoryText = !string.IsNullOrWhiteSpace(story.StoryRevised) ? story.StoryRevised! : (story.StoryRaw ?? string.Empty);
        var emptyParsed = new ParsedEvaluation(0, string.Empty, 0, string.Empty, 0, string.Empty, 0, string.Empty);
        if (string.IsNullOrWhiteSpace(fullStoryText))
        {
            return (false, emptyParsed, string.Empty, "Storia vuota");
        }

        var maxChunkTokens = Math.Max(320, contextLimitTokens - expectedOutputTokens - EstimateTokenCount(systemMessage) - 512);
        var maxChunkChars = Math.Max(1200, maxChunkTokens * 3);
        var chunks = SplitTextIntoEvaluationChunks(fullStoryText, maxChunkChars);
        if (chunks.Count == 0)
        {
            return (false, emptyParsed, string.Empty, "Chunking valutazione non ha prodotto segmenti.");
        }

        var parsedChunks = new List<ParsedEvaluation>(chunks.Count);
        var runId = $"evaluate_story_{story.Id}_agent_{evaluatorAgent.Id}";
        _customLogger?.Append(runId, $"[{story.Id}] story_evaluator fallback chunk attivato: chunks={chunks.Count}; max_chunk_chars={maxChunkChars}");

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunkText = chunks[i];
            var chunkMessages = new ChatHistory(new[]
            {
                new ConversationMessage { Role = "system", Content = systemMessage },
                new ConversationMessage { Role = "user", Content = BuildStoryEvaluatorUserMessage(chunkText) }
            });

            var threadId = unchecked((((int)(story.Id % int.MaxValue)) * 541) ^ (7000 + i + 1));
            var evalResult = await callCenter.CallAgentAsync(
                storyId: story.Id,
                threadId: threadId,
                agent: evaluatorAgent,
                history: chunkMessages,
                options: new CallOptions
                {
                    Operation = "story_evaluation_chunk",
                    Timeout = TimeSpan.FromSeconds(120),
                    MaxRetries = 0,
                    UseResponseChecker = false,
                    AllowFallback = true,
                    AskFailExplanation = true
                }).ConfigureAwait(false);

            if (!evalResult.Success)
            {
                return (false, emptyParsed, string.Empty, evalResult.FailureReason ?? $"Valutazione chunk {i + 1}/{chunks.Count} fallita");
            }

            var normalized = NormalizeEvaluatorOutput(evalResult.ResponseText ?? string.Empty);
            if (!TryParseEvaluationText(normalized, out var parsedChunk, out var parseError))
            {
                return (false, emptyParsed, string.Empty, parseError ?? $"Formato valutazione chunk {i + 1}/{chunks.Count} non valido");
            }

            parsedChunks.Add(parsedChunk);
        }

        var aggregated = AggregateChunkEvaluations(parsedChunks);
        var aggregatedText = BuildEvaluationTextFromParsed(aggregated);
        return (true, aggregated, aggregatedText, null);
    }

    private static List<string> SplitTextIntoEvaluationChunks(string text, int maxChunkChars)
    {
        var source = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(source))
        {
            return new List<string>();
        }

        var maxChars = Math.Max(800, maxChunkChars);
        if (source.Length <= maxChars)
        {
            return new List<string> { source };
        }

        var paragraphs = Regex.Split(source, @"\r?\n\s*\r?\n")
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        if (paragraphs.Count == 0)
        {
            return new List<string> { TrimTextToApproxTokenBudget(source, maxChars / 3) };
        }

        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            var candidateLength = current.Length == 0
                ? paragraph.Length
                : current.Length + Environment.NewLine.Length * 2 + paragraph.Length;

            if (candidateLength > maxChars && current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }

            if (paragraph.Length > maxChars)
            {
                var start = 0;
                while (start < paragraph.Length)
                {
                    var len = Math.Min(maxChars, paragraph.Length - start);
                    var slice = paragraph.Substring(start, len).Trim();
                    if (!string.IsNullOrWhiteSpace(slice))
                    {
                        chunks.Add(slice);
                    }
                    start += len;
                }
                continue;
            }

            if (current.Length > 0)
            {
                current.AppendLine();
                current.AppendLine();
            }
            current.Append(paragraph);
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString().Trim());
        }

        return chunks.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    }

    private static ParsedEvaluation AggregateChunkEvaluations(IReadOnlyList<ParsedEvaluation> chunks)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return new ParsedEvaluation(0, string.Empty, 0, string.Empty, 0, string.Empty, 0, string.Empty);
        }

        int Avg(Func<ParsedEvaluation, int> selector) => (int)Math.Round(chunks.Average(selector));
        string Merge(Func<ParsedEvaluation, string> selector)
            => string.Join(" | ", chunks.Select(selector).Where(s => !string.IsNullOrWhiteSpace(s)).Take(4));

        return new ParsedEvaluation(
            Math.Clamp(Avg(c => c.NarrativeScore10), 0, 10),
            Merge(c => c.NarrativeExplanation),
            Math.Clamp(Avg(c => c.OriginalityScore10), 0, 10),
            Merge(c => c.OriginalityExplanation),
            Math.Clamp(Avg(c => c.EmotionalScore10), 0, 10),
            Merge(c => c.EmotionalExplanation),
            Math.Clamp(Avg(c => c.ActionScore10), 0, 10),
            Merge(c => c.ActionExplanation));
    }

    private static string BuildEvaluationTextFromParsed(ParsedEvaluation parsed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Coerenza narrativa");
        sb.AppendLine($"Punteggio: {parsed.NarrativeScore10}/10");
        sb.AppendLine($"Motivazione: {parsed.NarrativeExplanation}");
        sb.AppendLine();
        sb.AppendLine("Originalità");
        sb.AppendLine($"Punteggio: {parsed.OriginalityScore10}/10");
        sb.AppendLine($"Motivazione: {parsed.OriginalityExplanation}");
        sb.AppendLine();
        sb.AppendLine("Impatto emotivo");
        sb.AppendLine($"Punteggio: {parsed.EmotionalScore10}/10");
        sb.AppendLine($"Motivazione: {parsed.EmotionalExplanation}");
        sb.AppendLine();
        sb.AppendLine("Azione");
        sb.AppendLine($"Punteggio: {parsed.ActionScore10}/10");
        sb.AppendLine($"Motivazione: {parsed.ActionExplanation}");
        return sb.ToString().Trim();
    }

    private void TryEnqueueAutoFormatAfterEvaluation(long storyId)
    {
        try
        {
            if (storyId <= 0) return;

            var (count, average) = _database.GetStoryEvaluationStats(storyId);
            if (count < AutoFormatMinEvaluations) return;
            if (average <= AutoFormatMinAverageScore) return;

            var metadata = new Dictionary<string, string>
            {
                ["evaluationCount"] = count.ToString(),
                ["evaluationAvg"] = average.ToString("F2")
            };

            TryEnqueueTransformStoryRawToTagged(
                storyId,
                trigger: "evaluation_saved",
                priority: 2,
                extraMetadata: metadata);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Auto-format enqueue failed for story {StoryId}", storyId);
        }
    }

    public bool TryEnqueueTransformStoryRawToTagged(
        long storyId,
        string trigger,
        int priority = 2,
        IDictionary<string, string>? extraMetadata = null,
        string? runIdOverride = null)
    {
        if (storyId <= 0) return false;
        if (_commandDispatcher == null) return false;
        if (_kernelFactory == null) return false;

        var story = _database.GetStoryById(storyId);
        if (story == null || story.Deleted) return false;
        if (!string.IsNullOrWhiteSpace(story.StoryTagged)) return false;
        if (string.IsNullOrWhiteSpace(story.StoryRaw)) return false;

        if (IsFormatterCommandQueued(storyId)) return false;

        var metadata = extraMetadata != null
            ? new Dictionary<string, string>(extraMetadata, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        metadata["storyId"] = storyId.ToString();
        metadata["operation"] = "format_story_auto";
        metadata["trigger"] = trigger;

        var runId = string.IsNullOrWhiteSpace(runIdOverride)
            ? $"format_story_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : runIdOverride;

        _commandDispatcher.Enqueue(
            "TransformStoryRawToTagged",
            async ctx =>
            {
                try
                {
                    var cmd = new AddVoiceTagsToStoryCommand(
                        storyId,
                        _database,
                        _kernelFactory,
                        storiesService: this,
                        logger: _customLogger,
                        tuning: _tuning);

                    return await cmd.ExecuteAsync(ctx.CancellationToken, ctx.RunId);
                }
                catch (Exception ex)
                {
                    return new CommandResult(false, ex.Message);
                }
            },
            runId: runId,
            threadScope: "story/format",
            metadata: metadata,
            priority: priority);

        if (metadata.TryGetValue("evaluationAvg", out var avgStr) &&
            double.TryParse(avgStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var avgValue))
        {
            _logger?.LogInformation(
                "Auto-tag enqueued for story {StoryId} (avg={Avg:F2}, trigger={Trigger})",
                storyId,
                avgValue,
                trigger);
        }
        else
        {
            _logger?.LogInformation(
                "TransformStoryRawToTagged enqueued for story {StoryId} (trigger={Trigger})",
                storyId,
                trigger);
        }

        return true;
    }

    private bool IsFormatterCommandQueued(long storyId)
    {
        if (storyId <= 0 || _commandDispatcher == null) return false;

        try
        {
            return _commandDispatcher.GetActiveCommands().Any(s =>
                s.Metadata != null &&
                s.Metadata.TryGetValue("storyId", out var sid) &&
                string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                (
                    string.Equals(s.OperationName, "TransformStoryRawToTagged", StringComparison.OrdinalIgnoreCase) ||
                    (s.Metadata.TryGetValue("operation", out var op) && op.Contains("format_story", StringComparison.OrdinalIgnoreCase)) ||
                    s.RunId.StartsWith($"format_story_{storyId}_", StringComparison.OrdinalIgnoreCase)
                ));
        }
        catch
        {
            return false;
        }
    }
    private bool TryTransitionStoryToEvaluatedIfRevised(long storyId, string? runId)
    {
        if (storyId <= 0) return true;
        var latestStory = _database.GetStoryById(storyId);
        if (latestStory == null)
        {
            _logger?.LogWarning("Story {StoryId} missing before transitioning to evaluated", storyId);
            return false;
        }

        if (!string.Equals(latestStory.Status, "revised", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryChangeStatus(storyId, "evaluate_story", runId);
    }

    private void TrySendStoryAfterEvaluationEmail(long storyId, int evaluationCount, double averageScore)
    {
        try
        {
            var sendEnabled = _storyEvaluationOptions.send_story_after_evaluation ?? _storyEvaluationOptions.SendStoryAfterEvaluation;
            var threshold = _storyEvaluationOptions.send_story_after_evaluation_threshold ?? _storyEvaluationOptions.SendStoryAfterEvaluationThreshold;
            var averageScore100 = averageScore * 100.0 / 40.0;
            if (!sendEnabled) return;
            if (storyId <= 0) return;
            if (averageScore100 <= threshold) return;
            if (!_storyEvalEmailSent.TryAdd(storyId, 1)) return;

            var recipientsRaw = (_storyEvaluationOptions.send_story_after_evaluation_recipients
                                 ?? _storyEvaluationOptions.SendStoryAfterEvaluationRecipients
                                 ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(recipientsRaw)) return;

            var story = _database.GetStoryById(storyId);
            if (story == null) return;

            var revisedText = !string.IsNullOrWhiteSpace(story.StoryRevised)
                ? story.StoryRevised!
                : (story.StoryRaw ?? string.Empty);
            var planText = string.IsNullOrWhiteSpace(story.NrePlanSummary)
                ? "(piano non disponibile)"
                : story.NrePlanSummary!;

            var subject = $"Story valutata > soglia - #{storyId} - {(string.IsNullOrWhiteSpace(story.Title) ? "Untitled" : story.Title)}";
            var safeTitle = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(story.Title) ? "Untitled" : story.Title);
            var safePlan = WebUtility.HtmlEncode(planText ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "<br/>");
            var safeStory = WebUtility.HtmlEncode(revisedText ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "<br/>");
            var bodyHtml =
$@"<!doctype html>
<html>
<head>
  <meta charset=""utf-8"" />
</head>
<body style=""margin:0;padding:24px;background:#ece7dc;color:#2d2418;font-family:Georgia,'Times New Roman',serif;"">
  <div style=""max-width:900px;margin:0 auto;"">
    <div style=""padding:16px 18px;margin-bottom:14px;background:#f6f1e6;border:1px solid #d9cdb8;border-radius:8px;"">
      <div><strong>StoryId:</strong> {storyId}</div>
      <div><strong>Titolo:</strong> {safeTitle}</div>
      <div><strong>Valutazione media:</strong> {averageScore100:F1}/100</div>
      <div><strong>Numero valutazioni:</strong> {evaluationCount}</div>
    </div>

    <div style=""padding:22px 24px;background:linear-gradient(180deg,#f8f2e7 0%,#f2e6d4 100%);border:1px solid #cdbb9d;border-radius:10px;box-shadow:0 6px 18px rgba(62,42,17,.15);"">
      <h3 style=""margin:0 0 10px 0;font-size:19px;font-weight:700;"">Piano</h3>
      <div style=""line-height:1.65;font-size:16px;white-space:normal;"">{safePlan}</div>
      <hr style=""margin:18px 0;border:none;border-top:1px solid #cdbb9d;"" />
      <h3 style=""margin:0 0 10px 0;font-size:19px;font-weight:700;"">Testo Revised</h3>
      <div style=""line-height:1.75;font-size:17px;white-space:normal;text-align:justify;"">{safeStory}</div>
    </div>
  </div>
</body>
</html>";

            using var message = new MailMessage();
            message.From = new MailAddress(string.IsNullOrWhiteSpace(_storyEvaluationOptions.SmtpFrom)
                ? "noreply@localhost"
                : _storyEvaluationOptions.SmtpFrom!.Trim());
            foreach (var recipient in recipientsRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(recipient))
                {
                    message.To.Add(recipient);
                }
            }
            if (message.To.Count == 0) return;

            message.Subject = subject;
            message.Body = bodyHtml;
            message.IsBodyHtml = true;

            var host = (_storyEvaluationOptions.SmtpHost ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(host)) return;

            using var smtp = new SmtpClient(host, _storyEvaluationOptions.SmtpPort)
            {
                EnableSsl = _storyEvaluationOptions.SmtpUseSsl
            };

            var smtpUser = (_storyEvaluationOptions.SmtpUsername ?? string.Empty).Trim();
            var smtpPass = _storyEvaluationOptions.SmtpPassword ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(smtpUser))
            {
                smtp.Credentials = new NetworkCredential(smtpUser, smtpPass);
            }
            else
            {
                smtp.UseDefaultCredentials = true;
            }

            smtp.Send(message);
            _logger?.LogInformation(
                "Story evaluation email inviata per story {StoryId} (avg={Avg:F2}, count={Count})",
                storyId,
                averageScore100,
                evaluationCount);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Invio email post-evaluation fallito per story {StoryId}", storyId);
            _storyEvalEmailSent.TryRemove(storyId, out _);
        }
    }
    private static string NormalizeEvaluatorOutput(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var t = text;

        // Remove common "thinking" blocks.
        t = Regex.Replace(t, "(?is)<think>.*?</think>", " ");
        t = Regex.Replace(t, "(?is)\\[think\\].*?\\[/think\\]", " ");
        t = Regex.Replace(t, "(?im)^\\s*(thinking|reasoning)\\s*:\\s*.*$", " ");

        // Preserve content inside fenced code blocks (models often wrap the whole evaluation in ```...```).
        // We only strip the fence markers.
        t = Regex.Replace(t, "(?is)```(?:[a-z0-9_+-]+)?\\s*(.*?)\\s*```", "$1");

        // Collapse whitespace (line breaks are not meaningful for parsing).
        t = Regex.Replace(t, "\\s+", " ").Trim();
        return t;
    }

    private sealed record ParsedEvaluation(
        int NarrativeScore10,
        string NarrativeExplanation,
        int OriginalityScore10,
        string OriginalityExplanation,
        int EmotionalScore10,
        string EmotionalExplanation,
        int ActionScore10,
        string ActionExplanation);

    private static bool TryParseEvaluationText(string text, out ParsedEvaluation parsed, out string? error)
    {
        parsed = new ParsedEvaluation(0, string.Empty, 0, string.Empty, 0, string.Empty, 0, string.Empty);
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Risposta di valutazione vuota.";
            return false;
        }

        var normalized = NormalizeEvaluatorOutput(text);

        // Flexible headings (accept missing accents / minor variations).
        // IMPORTANT: parse them sequentially in the required order to avoid false positives
        // (e.g., the word "azione" mentioned inside another section's comment).
        var hNarr = new Regex(@"\bcoerenza\s+narrativ[aà]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hOrig = new Regex(@"\boriginalit[aà]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hEmot = new Regex(@"\bimpatto\s+emotiv[oòaà]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hAzione = new Regex(@"\bazione\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        (int idx, int len)? FindAfter(Regex rx, int startAt)
        {
            if (startAt < 0) startAt = 0;
            if (startAt >= normalized.Length) return null;
            var m = rx.Match(normalized, startAt);
            return m.Success ? (m.Index, m.Length) : null;
        }

        var narr = FindAfter(hNarr, 0);
        if (narr == null)
        {
            error = "Formato valutazione non valido: manca la sezione 'Coerenza narrativa'.";
            return false;
        }

        var orig = FindAfter(hOrig, narr.Value.idx + narr.Value.len);
        if (orig == null)
        {
            error = "Formato valutazione non valido: manca la sezione 'Originalità'.";
            return false;
        }

        var emot = FindAfter(hEmot, orig.Value.idx + orig.Value.len);
        if (emot == null)
        {
            error = "Formato valutazione non valido: manca la sezione 'Impatto emotivo'.";
            return false;
        }

        var azione = FindAfter(hAzione, emot.Value.idx + emot.Value.len);
        if (azione == null)
        {
            error = "Formato valutazione non valido: manca la sezione 'Azione'.";
            return false;
        }

        (int score5, string explanation) ExtractSection(int start, int end)
        {
            var seg = normalized.Substring(start, Math.Max(0, end - start)).Trim();
            if (string.IsNullOrWhiteSpace(seg)) return (-1, string.Empty);

            var mScore = Regex.Match(seg, @"\b([0-5])\b");
            if (!mScore.Success) return (-1, string.Empty);
            var score = int.Parse(mScore.Groups[1].Value);

            // Explanation is optional.
            var rest = seg.Substring(mScore.Index + mScore.Length).Trim();
            rest = rest.TrimStart('-', ':', '.', ')', ']', ' ');
            return (score, rest);
        }

        var (nScore5, nExp) = ExtractSection(narr.Value.idx + narr.Value.len, orig.Value.idx);
        var (oScore5, oExp) = ExtractSection(orig.Value.idx + orig.Value.len, emot.Value.idx);
        var (eScore5, eExp) = ExtractSection(emot.Value.idx + emot.Value.len, azione.Value.idx);
        var (aScore5, aExp) = ExtractSection(azione.Value.idx + azione.Value.len, normalized.Length);

        if (nScore5 < 0 || oScore5 < 0 || eScore5 < 0 || aScore5 < 0)
        {
            error = "Formato valutazione non valido: punteggi non trovati o fuori range (atteso 0-5 per sezione).";
            return false;
        }

        parsed = new ParsedEvaluation(
            nScore5 * 2, nExp,
            oScore5 * 2, oExp,
            eScore5 * 2, eExp,
            aScore5 * 2, aExp);

        return true;
    }

    private void TryLogEvaluationResult(string runId, long storyId, string? agentName, bool success, string message)
    {
        try
        {
            var entry = new TinyGenerator.Models.LogEntry
            {
                Ts = DateTime.UtcNow.ToString("o"),
                Level = success ? "Information" : "Warning",
                Category = "StoryEvaluation",
                Message = $"[{storyId}] {message}",
                ThreadScope = "story_evaluation",
                ThreadId = 0,
                StoryId = storyId > 0 && storyId <= int.MaxValue ? (int?)storyId : null,
                AgentName = agentName,
                Result = success ? "SUCCESS" : "FAILED",
                DurationSecs = 1
            };
            _database.InsertLogsAsync(new[] { entry }).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch { }
    }

    public async Task<(bool success, double score, string? error)> EvaluateActionPacingWithAgentAsync(long storyId, int agentId)
    {
        if (_kernelFactory == null)
            return (false, 0, "Kernel factory non disponibile");

        var story = GetStoryById(storyId);
        if (story == null || string.IsNullOrWhiteSpace(story.StoryRaw))
            return (false, 0, "Storia non trovata o priva di contenuto");

        var agent = _database.GetAgentById(agentId);
        if (agent == null || !agent.IsActive)
            return (false, 0, "Agente valutatore non trovato");

        if (!agent.ModelId.HasValue)
            return (false, 0, "Modello non configurato per l'agente valutatore");

        var modelInfo = _database.GetModelInfoById(agent.ModelId.Value);
        var modelName = modelInfo?.Name;
        if (string.IsNullOrWhiteSpace(modelName))
            return (false, 0, "Modello associato all'agente non disponibile");

        var allowedPlugins = ParseAgentSkills(agent)?.ToList() ?? new List<string>();
        if (!allowedPlugins.Any())
            return (false, 0, "L'agente valutatore non ha strumenti abilitati");

        var runId = $"actioneval_{story.Id}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        var evaluationOpId = LogScope.GenerateOperationId();
        using var scope = LogScope.Push($"action_evaluation_{story.Id}", evaluationOpId, null, null, agent.Description, agent.Role, storyId: story.Id);

        try
        {
            var systemMessage = !string.IsNullOrWhiteSpace(agent.SystemPrompt)
                ? agent.SystemPrompt
                : (ComposeSystemMessage(agent) ?? string.Empty);

            var prompt = BuildActionPacingPrompt(story);

            var reactExecution = await ExecuteReActWithStandardPathAsync(
                runId: runId,
                roleCode: agent.Role ?? "action_evaluator",
                agent: agent,
                modelName: modelName,
                prompt: prompt,
                systemMessage: systemMessage,
                responseChecker: _responseChecker,
                maxIterations: 100,
                orchestratorFactory: () => _kernelFactory.CreateOrchestrator(modelName, allowedPlugins, agent.Id),
                ct: CancellationToken.None).ConfigureAwait(false);

            if (!reactExecution.success)
            {
                var error = reactExecution.error ?? "Valutazione azione/ritmo fallita";
                _customLogger?.Append(runId, $"[{storyId}] Valutazione fallita: {error}", "Error");
                return (false, 0, error);
            }

            _customLogger?.Append(runId, $"[{storyId}] Valutazione azione/ritmo completata");
            return (true, 0, null);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante EvaluateActionPacingWithAgent per storia {StoryId} agente {AgentId}", storyId, agentId);
            return (false, 0, ex.Message);
        }
        finally
        {
            _customLogger?.MarkCompleted(runId);
        }
    }

    private async Task<(bool success, string? error)> ExecuteReActWithStandardPathAsync(
        string runId,
        string roleCode,
        Agent agent,
        string modelName,
        string prompt,
        string systemMessage,
        ResponseCheckerService? responseChecker,
        int maxIterations,
        Func<HybridLangChainOrchestrator> orchestratorFactory,
        CancellationToken ct)
    {
        if (_kernelFactory == null)
        {
            return (false, "Kernel factory non disponibile");
        }

        var initialModelId = agent.ModelId ?? 0;
        if (initialModelId <= 0)
        {
            return (false, $"Agente {agent.Description} senza modello configurato");
        }

        try
        {
            var executionOrchestrator = new ModelExecutionOrchestrator(_kernelFactory, _scopeFactory, _customLogger);
            var result = await executionOrchestrator.ExecuteAsync(
                new ModelExecutionRequest
                {
                    RoleCode = roleCode,
                    Agent = agent,
                    InitialModelId = initialModelId,
                    InitialModelName = modelName,
                    WorkInput = prompt,
                    RunId = runId,
                    ChunkIndex = 1,
                    ChunkCount = 1,
                    WorkLabel = "react_loop",
                    Options = new ModelExecutionOptions
                    {
                        MaxAttemptsPerModel = 1,
                        RetryDelayBaseSeconds = 0,
                        EnableFallback = false,
                        EnableDiagnosis = false
                    },
                    WorkAsync = async (bridge, token) =>
                    {
                        var orchestrator = orchestratorFactory();
                        var loop = new ReActLoopOrchestrator(
                            orchestrator,
                            _customLogger,
                            maxIterations: maxIterations,
                            runId: runId,
                            modelBridge: bridge,
                            systemMessage: systemMessage,
                            responseChecker: responseChecker,
                            agentRole: agent.Role);

                        var reactResult = await loop.ExecuteAsync(prompt, token).ConfigureAwait(false);
                        if (!reactResult.Success)
                        {
                            return ModelWorkResult.Fail(
                                reactResult.Error ?? "ReAct loop fallito",
                                reactResult.FinalResponse);
                        }

                        var text = string.IsNullOrWhiteSpace(reactResult.FinalResponse)
                            ? "__react_tool_success__"
                            : reactResult.FinalResponse;
                        return ModelWorkResult.Ok(text);
                    }
                },
                ct).ConfigureAwait(false);

            return (true, result.OutputText);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ExecuteReActWithStandardPathAsync fallito per ruolo {Role}", roleCode);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Generates TTS audio for a story and saves it to the specified folder.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> GenerateTtsForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        // Back-compat overload. Prefer using the storyId-only overload.
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        return await GenerateTtsForStoryInternalAsync(storyId, folderName, dispatcherRunId, targetStatusId: null, CancellationToken.None);
    }

    /// <summary>
    /// Generates TTS audio for a story, resolving the folder from the story record.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> GenerateTtsForStoryByIdAsync(long storyId, string? dispatcherRunId = null)
    {
        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderName = !string.IsNullOrWhiteSpace(story.Folder)
            ? story.Folder
            : new DirectoryInfo(EnsureStoryFolder(story)).Name;

        return await GenerateTtsForStoryInternalAsync(storyId, folderName, dispatcherRunId, targetStatusId: null, CancellationToken.None);
    }

    private async Task<(bool success, string? error)> GenerateTtsForStoryInternalAsync(long storyId, string folderName, string? dispatcherRunId, int? targetStatusId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        // Usa il nuovo flusso basato su tts_schema.json e timeline
        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, targetStatusId.HasValue ? new StoryStatus { Id = targetStatusId.Value } : null);

        var deleteCmd = new DeleteTtsCommand(this);
        var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
        if (!cleanupOk)
        {
            return (false, cleanupMessage ?? "Impossibile cancellare i file TTS esistenti");
        }

        // Run synchronously when dispatcherRunId is provided so we can report progress
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            var result = await GenerateTtsAudioWithProgressAsync(context, dispatcherRunId, cancellationToken);
            if (result.success && targetStatusId.HasValue)
            {
                try
                {
                    _database.UpdateStoryById(storyId, statusId: targetStatusId.Value, updateStatus: true);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to update story {Id} to status {StatusId}", storyId, targetStatusId.Value);
                }
            }
            return result;
        }

        var (success, message) = await StartTtsAudioGenerationAsync(context);
        return (success, message);
    }

    public sealed record EnqueueResult(bool Enqueued, string Message, string? RunId = null);

    public string? EnqueueGenerateTtsAudioCommand(long storyId, string trigger, int priority = 3)
    {
        var result = TryEnqueueGenerateTtsAudioCommandInternal(storyId, trigger, priority, targetStatusId: null);
        return result.Enqueued ? result.RunId : null;
    }

    public EnqueueResult TryEnqueueGenerateTtsAudioCommand(long storyId, string trigger, int priority = 3)
        => TryEnqueueGenerateTtsAudioCommandInternal(storyId, trigger, priority, targetStatusId: null);

    internal EnqueueResult TryEnqueueGenerateTtsAudioCommandInternal(long storyId, string trigger, int priority, int? targetStatusId)
    {
        try
        {
            if (storyId <= 0)
                return new EnqueueResult(false, "StoryId non valido.");
            if (_commandDispatcher == null)
                return new EnqueueResult(false, "CommandDispatcher non disponibile (app non inizializzata o servizio non registrato).");

            var story = GetStoryById(storyId);
            if (story == null)
                return new EnqueueResult(false, $"Storia {storyId} non trovata.");

            var folderName = !string.IsNullOrWhiteSpace(story.Folder)
                ? story.Folder
                : new DirectoryInfo(EnsureStoryFolder(story)).Name;

                // De-dup: don't enqueue if already queued/running.
                try
                {
                    static bool IsBlockingStatus(CommandSnapshot s)
                        => string.Equals(s.Status, "queued", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(s.Status, "running", StringComparison.OrdinalIgnoreCase);

                    static bool IsStatusChainWrapper(CommandSnapshot s)
                    {
                        if (s.Metadata != null && s.Metadata.TryGetValue("chainMode", out var chainMode))
                        {
                            return string.Equals(chainMode, "1", StringComparison.OrdinalIgnoreCase);
                        }
                        return s.ThreadScope != null &&
                               s.ThreadScope.StartsWith("story/status_chain", StringComparison.OrdinalIgnoreCase);
                    }

                    var existing = _commandDispatcher.GetActiveCommands().FirstOrDefault(s =>
                        s.Metadata != null &&
                        s.Metadata.TryGetValue("storyId", out var sid) &&
                        string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                        !IsStatusChainWrapper(s) &&
                        IsBlockingStatus(s) &&
                        (string.Equals(s.OperationName, "generate_tts_audio", StringComparison.OrdinalIgnoreCase) ||
                         s.RunId.StartsWith($"generate_tts_audio_{storyId}_", StringComparison.OrdinalIgnoreCase)));

                if (existing != null && !string.IsNullOrWhiteSpace(existing.RunId))
                {
                    var status = string.IsNullOrWhiteSpace(existing.Status) ? "" : $" (stato: {existing.Status})";
                    return new EnqueueResult(false, $"Generazione audio TTS già accodata/in esecuzione{status} (run {existing.RunId}).", existing.RunId);
                }
            }
            catch
            {
                // Best-effort: if snapshots fail, still allow enqueue.
            }

            var runId = $"generate_tts_audio_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            _commandDispatcher.Enqueue(
                "generate_tts_audio",
                async ctx =>
                {
                    var (ok, err) = await GenerateTtsForStoryInternalAsync(storyId, folderName, ctx.RunId, targetStatusId, ctx.CancellationToken);
                    var isAutoTrigger = !string.IsNullOrWhiteSpace(trigger) &&
                        trigger.Contains("auto", StringComparison.OrdinalIgnoreCase);
                    if (ok)
                    {
                        _database.ClearAutoTtsFailure(storyId);
                        var story = GetStoryById(storyId);
                        var allowNext = story == null || ApplyStatusTransitionWithCleanup(story, "tts_generated", ctx.RunId);

                        // Requirement: if TTS audio generation succeeds, enqueue music/ambience/fx individually with lower priority.
                        if (allowNext)
                        {
                            EnqueuePostTtsAudioFollowups(storyId, trigger: "tts_audio_generated", priority: Math.Max(priority + 1, 4));
                        }
                    }
                    else if (isAutoTrigger)
                    {
                        _database.MarkAutoTtsFailure(storyId, err);
                    }

                    return new CommandResult(ok, ok ? "Generazione audio TTS completata." : err);
                },
                runId: runId,
                threadScope: "story/tts_audio",
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = storyId.ToString(),
                    ["operation"] = "generate_tts_audio",
                    ["trigger"] = trigger,
                    ["folder"] = folderName,
                    ["targetStatusId"] = targetStatusId?.ToString() ?? string.Empty
                },
                priority: priority);

            return new EnqueueResult(true, $"Generazione audio TTS accodata (run {runId}).", runId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enqueue generate_tts_audio for story {StoryId}", storyId);
        }

        return new EnqueueResult(false, "Errore inatteso durante l'accodamento della generazione audio TTS (vedi log applicazione).");
    }

    public bool EnqueueFinalMixPipeline(long storyId, string trigger, int priority = 7)
    {
        try
        {
            if (storyId <= 0) return false;
            if (_commandDispatcher == null) return false;

            var story = GetStoryById(storyId);
            if (story == null || story.Deleted) return false;

            var storyFolderPath = EnsureStoryFolder(story);
            var folderName = !string.IsNullOrWhiteSpace(story.Folder)
                ? story.Folder
                : new DirectoryInfo(storyFolderPath).Name;

            var basePriority = Math.Max(1, priority);

            void EnqueueIfNotQueued(string operationName, string runPrefix, Func<CommandContext, Task<CommandResult>> handler, int opPriority, bool batch = false)
            {
                try
                {
                    var alreadyQueued = _commandDispatcher.GetActiveCommands().Any(s =>
                        s.Metadata != null &&
                        s.Metadata.TryGetValue("storyId", out var sid) &&
                        string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(s.OperationName, operationName, StringComparison.OrdinalIgnoreCase) ||
                         s.RunId.StartsWith(runPrefix, StringComparison.OrdinalIgnoreCase)));

                    if (alreadyQueued) return;
                }
                catch
                {
                    // If snapshots fail, still try to enqueue.
                }

                var runId = $"{runPrefix}{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                _commandDispatcher.Enqueue(
                    operationName,
                    handler,
                    runId: runId,
                    threadScope: $"story/{operationName}",
                    metadata: new Dictionary<string, string>
                    {
                        ["storyId"] = storyId.ToString(),
                        ["operation"] = operationName,
                        ["trigger"] = trigger,
                        ["folder"] = folderName
                    },
                    priority: Math.Max(1, opPriority),
                    batch: batch);
            }

            EnqueueIfNotQueued(
                "generate_tts_schema",
                $"generate_tts_schema_{storyId}_",
                async _ =>
                {
                    var (ok, msg) = await GenerateTtsSchemaJsonAsync(storyId);
                    return new CommandResult(ok, ok ? "Schema TTS generato." : msg);
                },
                basePriority);

            // Avoid pre-enqueueing dependent steps (tts/audio/video) based on a stale schema snapshot:
            // generate_tts_schema may regenerate or fail and invalidate downstream prerequisites.
            // Subsequent steps are auto-enqueued only after successful prerequisites.
            _logger?.LogInformation(
                "Final mix pipeline queued with gated sequencing for story {StoryId}: generate_tts_schema first, dependent steps deferred to autolaunch.",
                storyId);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enqueue final mix pipeline for story {StoryId}", storyId);
            return false;
        }
    }

    public long? GetTopStoryForAudioPipeline()
    {
        try
        {
            var candidates = _database.GetTopStoriesByEvaluation(10);
            if (candidates == null || candidates.Count == 0) return null;

            foreach (var candidate in candidates)
            {
                if (candidate.GeneratedMixedAudio) continue;
                var story = GetStoryById(candidate.Id);
                if (story == null || story.Deleted) continue;
                if (story.AutoTtsFailed) continue;
                if (story.GeneratedAmbient && story.GeneratedEffects && story.GeneratedMusic && story.GeneratedTts) continue;
                return candidate.Id;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Errore nel calcolo della top story per audio pipeline");
        }

        return null;
    }

    public bool TryEnqueueFullAudioPipelineForTopStory(string trigger, int priority = 7)
    {
        var storyId = GetTopStoryForAudioPipeline();
        if (!storyId.HasValue) return false;

        return EnqueueFinalMixPipeline(storyId.Value, trigger, priority);
    }

    public bool TryEnqueueGenerateTtsSchemaOnly(long storyId, string trigger, int priority = 3)
    {
        if (storyId <= 0 || _commandDispatcher == null) return false;

        var story = GetStoryById(storyId);
        if (story == null || story.Deleted) return false;
        if (!string.Equals(story.Status, "tagged", StringComparison.OrdinalIgnoreCase)) return false;
        if (story.GeneratedTtsJson) return false;

        if (IsCommandQueued(storyId, "generate_tts_schema", $"generate_tts_schema_{storyId}_"))
        {
            return false;
        }

        var folderName = !string.IsNullOrWhiteSpace(story.Folder)
            ? story.Folder
            : new DirectoryInfo(EnsureStoryFolder(story)).Name;

        var runId = $"generate_tts_schema_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        _commandDispatcher.Enqueue(
            "generate_tts_schema",
            async ctx =>
            {
                var (ok, msg) = await GenerateTtsSchemaJsonAsync(storyId);
                return new CommandResult(ok, ok ? "Schema TTS generato" : msg ?? "Errore generazione schema TTS");
            },
            runId: runId,
            threadScope: "story/generate_tts_schema",
            metadata: new Dictionary<string, string>
            {
                ["storyId"] = storyId.ToString(),
                ["operation"] = "generate_tts_schema",
                ["trigger"] = trigger,
                ["folder"] = folderName
            },
            priority: Math.Max(1, priority));

        _logger?.LogInformation(
            "Auto TTS schema enqueued for tagged story {StoryId} (trigger={Trigger})",
            storyId,
            trigger);

        return true;
    }

    private bool IsCommandQueued(long storyId, string operationName, string runPrefix)
    {
        if (storyId <= 0 || _commandDispatcher == null) return false;

        try
        {
            return _commandDispatcher.GetActiveCommands().Any(s =>
                s.Metadata != null &&
                s.Metadata.TryGetValue("storyId", out var sid) &&
                string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(s.OperationName, operationName, StringComparison.OrdinalIgnoreCase) ||
                 s.RunId.StartsWith(runPrefix, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

        public void EnqueuePostTtsAudioFollowups(long storyId, string trigger, int priority = 4)
        {
            try
            {
                if (storyId <= 0) return;
            if (_commandDispatcher == null) return;

            var story = GetStoryById(storyId);
            if (story == null) return;

            var folderName = !string.IsNullOrWhiteSpace(story.Folder)
                ? story.Folder
                : new DirectoryInfo(EnsureStoryFolder(story)).Name;

            void EnqueueIfNotQueued(string operationName, string runPrefix, Func<CommandContext, Task<CommandResult>> handler, bool batch = false)
            {
                try
                {
                    var alreadyQueued = _commandDispatcher.GetActiveCommands().Any(s =>
                        s.Metadata != null &&
                        s.Metadata.TryGetValue("storyId", out var sid) &&
                        string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(s.OperationName, operationName, StringComparison.OrdinalIgnoreCase) ||
                         s.RunId.StartsWith(runPrefix, StringComparison.OrdinalIgnoreCase)));

                    if (alreadyQueued) return;
                }
                catch
                {
                    // If snapshots fail, still try to enqueue.
                }

                var runId = $"{runPrefix}{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                _commandDispatcher.Enqueue(
                    operationName,
                    handler,
                    runId: runId,
                    threadScope: $"story/{operationName}",
                    metadata: new Dictionary<string, string>
                    {
                        ["storyId"] = storyId.ToString(),
                        ["operation"] = operationName,
                        ["trigger"] = trigger,
                        ["folder"] = folderName
                    },
                    priority: priority,
                    batch: batch);
            }

            EnqueueIfNotQueued(
                "generate_music",
                $"generate_music_{storyId}_",
                async ctx =>
                {
                    var (ok, err) = await GenerateMusicForStoryAsync(storyId, folderName, ctx.RunId);
                    return new CommandResult(ok, ok ? "Generazione musica completata." : err);
                },
                batch: true);

            var autoAmbience = IsAmbienceGenerationEnabled()
                && (_audioGenerationOptions?.CurrentValue?.Ambience?.AutolaunchNextCommand ?? true);
            if (autoAmbience)
            {
                EnqueueIfNotQueued(
                    "generate_ambience_audio",
                    $"generate_ambience_audio_{storyId}_",
                    async ctx =>
                    {
                        var (ok, err) = await GenerateAmbienceForStoryAsync(storyId, folderName, ctx.RunId);
                        return new CommandResult(ok, ok ? "Generazione audio ambientale completata." : err);
                    },
                    batch: true);
            }

            var autoFx = _audioGenerationOptions?.CurrentValue?.Fx?.AutolaunchNextCommand ?? true;
            if (autoFx)
            {
                EnqueueIfNotQueued(
                    "generate_fx_audio",
                    $"generate_fx_audio_{storyId}_",
                    async ctx =>
                    {
                        var (ok, err) = await GenerateFxForStoryAsync(storyId, folderName, ctx.RunId);
                        return new CommandResult(ok, ok ? "Generazione effetti sonori completata." : err);
                    },
                    batch: true);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enqueue post-TTS followups for story {StoryId}", storyId);
        }
    }

    /// <summary>
    /// Removes markdown formatting characters from text.
    /// Strips: **bold**, *italic*, __bold__, _italic_, ~~strikethrough~~, `code`, [links](url), # headers, etc.
    /// </summary>
    private static string StripMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var result = text;

        // Remove links [text](url) -> text
        result = Regex.Replace(result, @"\[([^\]]+)\]\([^\)]+\)", "$1");

        // Remove images ![alt](url) -> empty
        result = Regex.Replace(result, @"!\[([^\]]*)\]\([^\)]+\)", "");

        // Remove inline code `code` -> code
        result = Regex.Replace(result, @"`([^`]+)`", "$1");

        // Remove code blocks ```code``` -> code
        result = Regex.Replace(result, @"```[^\n]*\n(.*?)\n```", "$1", RegexOptions.Singleline);

        // Remove bold **text** or __text__ -> text
        result = Regex.Replace(result, @"\*\*([^\*]+)\*\*", "$1");
        result = Regex.Replace(result, @"__([^_]+)__", "$1");

        // Remove italic *text* or _text_ -> text
        result = Regex.Replace(result, @"\*([^\*]+)\*", "$1");
        result = Regex.Replace(result, @"_([^_]+)_", "$1");

        // Remove strikethrough ~~text~~ -> text
        result = Regex.Replace(result, @"~~([^~]+)~~", "$1");

        // Remove headers # text -> text
        result = Regex.Replace(result, @"^#{1,6}\s+", "", RegexOptions.Multiline);

        // Remove horizontal rules --- or *** or ___
        result = Regex.Replace(result, @"^(\*{3,}|-{3,}|_{3,})$", "", RegexOptions.Multiline);

        // Remove blockquotes > text -> text
        result = Regex.Replace(result, @"^>\s+", "", RegexOptions.Multiline);

        // Remove list markers - or * or + or numbers
        result = Regex.Replace(result, @"^[\*\-\+]\s+", "", RegexOptions.Multiline);
        result = Regex.Replace(result, @"^\d+\.\s+", "", RegexOptions.Multiline);

        return result;
    }

    public string? EnqueueReviseStoryCommand(long storyId, string trigger, int priority = 2, bool force = false)
    {
        try
        {
            if (storyId <= 0) return null;
            if (_commandDispatcher == null) return null;

            // De-dup (optional): skip if already queued/running unless force=true.
            if (!force)
            {
                try
                {
                    var alreadyQueued = _commandDispatcher.GetActiveCommands().Any(s =>
                        s.Metadata != null &&
                        s.Metadata.TryGetValue("storyId", out var sid) &&
                        string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(s.OperationName, "revise_story", StringComparison.OrdinalIgnoreCase) ||
                         s.RunId.StartsWith($"revise_story_{storyId}_", StringComparison.OrdinalIgnoreCase)));
                    if (alreadyQueued) return null;
                }
                catch
                {
                    // best-effort
                }
            }

            var runId = $"revise_story_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            _commandDispatcher.Enqueue(
                "revise_story",
                async ctx =>
                {
                    try
                    {
                        var story = GetStoryById(storyId);
                        if (story == null)
                        {
                            return new CommandResult(false, "Story not found");
                        }

                        var raw = story.StoryRaw ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(raw))
                        {
                            return new CommandResult(false, "Story raw is empty");
                        }

                        var allowNext = true;

                        var revisionAgent = _database.ListAgents()
                            .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                                        (
                                            a.Role.Equals("story_editor", StringComparison.OrdinalIgnoreCase) ||
                                            a.Role.Equals("revisor", StringComparison.OrdinalIgnoreCase)
                                        ))
                            .OrderByDescending(a => a.Role != null && a.Role.Equals("story_editor", StringComparison.OrdinalIgnoreCase))
                            .ThenBy(a => a.Id)
                            .FirstOrDefault();

                        var criticExtractor = _database.ListAgents()
                            .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                                        a.Role.Equals("critic_extractor", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(a => a.Id)
                            .FirstOrDefault();

                        // If no revisor available, fall back to raw -> revised so the pipeline can continue.
                        if (revisionAgent == null)
                        {
                            _database.UpdateStoryRevised(storyId, StripMarkdown(raw));
                            allowNext = ApplyStatusTransitionWithCleanup(story, "revised", runId);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, allowNext
                                ? "No revisor configured: copied raw to story_revised and enqueued evaluations."
                                : "No revisor configured: copied raw to story_revised.");
                        }

                        if (!revisionAgent.ModelId.HasValue)
                        {
                            _database.UpdateStoryRevised(storyId, StripMarkdown(raw));
                            allowNext = ApplyStatusTransitionWithCleanup(story, "revised", runId);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped_no_model", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, allowNext
                                ? "Revisor has no model: copied raw to story_revised and enqueued evaluations."
                                : "Revisor has no model: copied raw to story_revised.");
                        }

                        var modelInfo = _database.GetModelInfoById(revisionAgent.ModelId.Value);
                        if (string.IsNullOrWhiteSpace(modelInfo?.Name))
                        {
                            _database.UpdateStoryRevised(storyId, StripMarkdown(raw));
                            allowNext = ApplyStatusTransitionWithCleanup(story, "revised", runId);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped_no_model_name", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, allowNext
                                ? "Revisor model not found: copied raw to story_revised and enqueued evaluations."
                                : "Revisor model not found: copied raw to story_revised.");
                        }

                        if (_kernelFactory == null)
                        {
                            return new CommandResult(false, "Kernel factory non disponibile");
                        }

                        var systemPrompt = (revisionAgent.UserPrompt ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(systemPrompt))
                        {
                            _database.UpdateStoryRevised(storyId, StripMarkdown(raw));
                            allowNext = ApplyStatusTransitionWithCleanup(story, "revised", runId);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped_empty_prompt", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, allowNext
                                ? "Revisor prompt empty: copied raw to story_revised and enqueued evaluations."
                                : "Revisor prompt empty: copied raw to story_revised.");
                        }

                        if (_scopeFactory == null)
                        {
                            return new CommandResult(false, "IServiceScopeFactory non disponibile per revisione");
                        }

                        using var modelScope = _scopeFactory.CreateScope();
                        var callCenter = modelScope.ServiceProvider.GetService<ICallCenter>();
                        if (callCenter == null)
                        {
                            return new CommandResult(false, "ICallCenter non disponibile per revisione");
                        }

                        string? extractedCriticIssues = null;
                        var evaluationsForCritic = (_database.GetStoryEvaluations(storyId) ?? new List<StoryEvaluation>())
                            .Where(e => e != null && e.Id > 0)
                            .OrderBy(e => e.Id)
                            .ToList();

                        if (criticExtractor != null && criticExtractor.ModelId.HasValue && evaluationsForCritic.Count > 0)
                        {
                            var criticPrompt = (criticExtractor.UserPrompt ?? string.Empty).Trim();
                            if (!string.IsNullOrWhiteSpace(criticPrompt))
                            {
                                try
                                {
                                    var criticHistory = new ChatHistory();
                                    criticHistory.AddSystem(criticPrompt);
                                    criticHistory.AddUser(BuildCriticExtractorInput(raw, evaluationsForCritic));

                                    var criticThreadIdRaw = unchecked((((int)(storyId % int.MaxValue)) * 131) ^ 0x43E1);
                                    var criticThreadId = criticThreadIdRaw == int.MinValue ? 1 : Math.Max(1, Math.Abs(criticThreadIdRaw));
                                    var criticResult = await callCenter.CallAgentAsync(
                                        storyId: storyId,
                                        threadId: criticThreadId,
                                        agent: criticExtractor,
                                        history: criticHistory,
                                        options: new CallOptions
                                        {
                                            Operation = "story_critic_extractor",
                                            Timeout = TimeSpan.FromSeconds(90),
                                            MaxRetries = 1,
                                            UseResponseChecker = false,
                                            AllowFallback = true,
                                            AskFailExplanation = true,
                                            DeterministicChecks =
                                            {
                                                new CheckEmpty
                                                {
                                                    Options = Options.Create<object>(new Dictionary<string, object>
                                                    {
                                                        ["ErrorMessage"] = "critic_extractor: lista criticita vuota"
                                                    })
                                                }
                                            }
                                        },
                                        cancellationToken: ctx.CancellationToken).ConfigureAwait(false);

                                    if (criticResult.Success && !string.IsNullOrWhiteSpace(criticResult.ResponseText))
                                    {
                                        extractedCriticIssues = NormalizeCriticExtractorIssues(criticResult.ResponseText);
                                        _customLogger?.Append(runId, $"[story {storyId}] Critic extractor completed.");
                                    }
                                    else
                                    {
                                        _customLogger?.Append(runId,
                                            $"[story {storyId}] Critic extractor failed or empty: {criticResult.FailureReason ?? "errore sconosciuto"}",
                                            "warn");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger?.LogWarning(ex, "Critic extractor failed for story {StoryId}", storyId);
                                }
                            }
                        }

                        // Use smaller chunks for the revisor to reduce repetition risk and improve responsiveness.
                        // Prepare all chunks up-front and validate we are not losing/skipping text.
                        var chunks = SplitIntoRevisionChunks(raw, approxChunkChars: 1000);
                        if (chunks.Count == 0)
                        {
                            return new CommandResult(false, "Revision chunking produced zero chunks");
                        }

                        // Sanity check: total chunk sizes (core + trailing separators) should match the normalized input length.
                        // Note: SplitIntoRevisionChunks normalizes CRLF -> LF internally, so compare to the same normalization.
                        var normalizedRaw = raw.Replace("\r\n", "\n");
                        var totalCore = chunks.Sum(c => c.Text.Length);
                        var totalSep = chunks.Sum(c => c.TrailingSeparator?.Length ?? 0);
                        var totalChunkChars = totalCore + totalSep;
                        var diff = Math.Abs(totalChunkChars - normalizedRaw.Length);

                        // Allow a small tolerance (defensive). If it's large, something is wrong and we should stop.
                        if (diff > 64)
                        {
                            _customLogger?.Append(runId,
                                $"[story {storyId}] Revision chunking size mismatch: normalizedRawLen={normalizedRaw.Length}, totalChunkChars={totalChunkChars}, diff={diff}. Aborting revision to avoid loops.",
                                "error");
                            return new CommandResult(false, "Revision chunking mismatch; aborting");
                        }
                        else if (diff > 0)
                        {
                            _customLogger?.Append(runId,
                                $"[story {storyId}] Revision chunking minor size mismatch: normalizedRawLen={normalizedRaw.Length}, totalChunkChars={totalChunkChars}, diff={diff}.",
                                "warn");
                        }
                        var revisedBuilder = new StringBuilder(raw.Length + 256);

                        var previousStart = -1;

                        for (var i = 0; i < chunks.Count; i++)
                        {
                            ctx.CancellationToken.ThrowIfCancellationRequested();
                            var chunk = chunks[i];

                            // Safety: never send the same chunk twice in one run.
                            if (chunk.Start <= previousStart)
                            {
                                _customLogger?.Append(runId, $"[story {storyId}] Revision chunk ordering issue: idx={i} start={chunk.Start} prevStart={previousStart}. Stopping to avoid duplicate chunks.", "warn");
                                break;
                            }
                            previousStart = chunk.Start;

                            try
                            {
                                _customLogger?.Append(runId, $"[story {storyId}] Revising chunk {i + 1}/{chunks.Count} start={chunk.Start} len={chunk.Text.Length}");
                            }
                            catch { }

                            // Publish progress to the global "Comandi in esecuzione" panel.
                            try
                            {
                                _commandDispatcher?.UpdateStep(ctx.RunId, i + 1, chunks.Count, $"chunk {i + 1}/{chunks.Count}");
                            }
                            catch { }

                            var isStoryEditor = !string.IsNullOrWhiteSpace(revisionAgent.Role) &&
                                revisionAgent.Role.Equals("story_editor", StringComparison.OrdinalIgnoreCase);
                            var systemPromptWithChunk = systemPrompt + $"\n\n(chunk: {i + 1}/{chunks.Count})";
                            var history = new ChatHistory();
                            history.AddSystem(systemPromptWithChunk);
                            history.AddUser(BuildRevisionChunkUserInput(
                                chunk.Text,
                                chunkIndex: i + 1,
                                chunkCount: chunks.Count,
                                criticIssues: extractedCriticIssues,
                                includeCriticSection: isStoryEditor));
                            var chunkThreadIdRaw = unchecked((((int)(storyId % int.MaxValue)) * 977) ^ (i + 1) ^ 0x5A17);
                            var chunkThreadId = chunkThreadIdRaw == int.MinValue ? 1 : Math.Max(1, Math.Abs(chunkThreadIdRaw));
                            var callResult = await callCenter.CallAgentAsync(
                                storyId: storyId,
                                threadId: chunkThreadId,
                                agent: revisionAgent,
                                history: history,
                                options: new CallOptions
                                {
                                    Operation = isStoryEditor ? "story_editor_chunk" : "story_revisor_chunk",
                                    Timeout = TimeSpan.FromSeconds(90),
                                    MaxRetries = 1,
                                    UseResponseChecker = false,
                                    AllowFallback = true,
                                    AskFailExplanation = true,
                                    DeterministicChecks =
                                    {
                                        new CheckEmpty
                                        {
                                            Options = Options.Create<object>(new Dictionary<string, object>
                                            {
                                                ["ErrorMessage"] = "editor_chunk: risposta vuota"
                                            })
                                        }
                                    }
                                },
                                cancellationToken: ctx.CancellationToken).ConfigureAwait(false);

                            var stepId = RequestIdGenerator.Generate();
                            _logger?.LogInformation("[StepID: {StepId}][Story: {StoryId}] Revisione chunk {ChunkNum}/{Total} via CallCenter", stepId, storyId, i + 1, chunks.Count);

                            var revisedChunk = (callResult.ResponseText ?? string.Empty).Trim();
                            if (!callResult.Success)
                            {
                                _customLogger?.Append(runId, $"[story {storyId}] Revision agent failed on chunk {i + 1}/{chunks.Count}: {callResult.FailureReason ?? "errore sconosciuto"}", "warn");
                            }

                            if (string.IsNullOrWhiteSpace(revisedChunk))
                            {
                                // If the model returned nothing, fall back to original chunk.
                                revisedChunk = chunk.Text;
                            }

                            revisedBuilder.Append(revisedChunk);

                            // Preserve original chunk boundary whitespace (newline/space/etc.) so words don't get glued.
                            if (!string.IsNullOrEmpty(chunk.TrailingSeparator) &&
                                !revisedChunk.EndsWith(chunk.TrailingSeparator, StringComparison.Ordinal))
                            {
                                revisedBuilder.Append(chunk.TrailingSeparator);
                            }
                        }

                        var revised = StripMarkdown(revisedBuilder.ToString());
                          _database.UpdateStoryRevised(storyId, revised);

                          // After revision, advance status and enqueue evaluations regardless.
                          allowNext = ApplyStatusTransitionWithCleanup(story, "revised", runId);
                          EnqueueAutomaticStoryEvaluations(storyId, trigger: "revised_saved", priority: Math.Max(priority + 1, 3));

                          var message = allowNext
                             ? "Story revised saved and evaluations enqueued."
                             : "Story revised saved (status transition deferred); evaluations enqueued.";

                          return new CommandResult(true, message);
                    }
                    catch (OperationCanceledException)
                    {
                        return new CommandResult(false, "Revision cancelled");
                    }
                    catch (Exception ex)
                    {
                        return new CommandResult(false, ex.Message);
                    }
                },
                runId: runId,
                threadScope: "story/revision",
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = storyId.ToString(),
                    ["operation"] = "revise_story",
                    ["trigger"] = trigger
                },
                priority: priority);

            return runId;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enqueue revise_story for story {StoryId}", storyId);
            return null;
        }
    }

    public string? EnqueueStateDrivenPostEpisodePipeline(long storyId, string trigger, int priority = 2)
    {
        try
        {
            if (storyId <= 0) return null;
            if (_commandDispatcher == null) return null;
            if (_kernelFactory == null) return null;

            var story = _database.GetStoryById(storyId);
            if (story == null) return null;
            if (!story.SerieId.HasValue || !story.SerieEpisode.HasValue) return null;

            var serieId = story.SerieId.Value;
            var episodeNumber = story.SerieEpisode.Value;

            var runId = StateDrivenPipelineHelpers.NewRunId("canon_extractor", storyId);
            _commandDispatcher.Enqueue(
                "canon_extractor",
                ctx =>
                {
                    var cmd = new CanonExtractorCommand(
                        storyId,
                        serieId,
                        episodeNumber,
                        runId,
                        _database,
                        _kernelFactory,
                        _commandDispatcher,
                        _customLogger,
                        _scopeFactory);
                    return cmd.ExecuteAsync(ctx.CancellationToken);
                },
                runId: runId,
                threadScope: $"series/{serieId}/episode/{episodeNumber}",
                metadata: new Dictionary<string, string>(StateDrivenPipelineHelpers.BuildMetadata(storyId, serieId, episodeNumber, "canon_extractor"))
                {
                    ["trigger"] = trigger
                },
                priority: Math.Max(1, priority));

            return runId;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enqueue state-driven pipeline for story {StoryId}", storyId);
            return null;
        }
    }

    public int StoryEvaluationsEnqueuer(long storyId, string trigger, int priority = 2, int maxEvaluators = 2)
    {
        try
        {
            if (storyId <= 0) return 0;
            if (_commandDispatcher == null) return 0;

            // De-dup: skip if any evaluation already queued/running for this story.
            try
            {
                var alreadyQueued = _commandDispatcher.GetActiveCommands().Any(s =>
                    !string.Equals(s.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(s.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(s.Status, "cancelled", StringComparison.OrdinalIgnoreCase) &&
                    s.Metadata != null &&
                    s.Metadata.TryGetValue("storyId", out var sid) &&
                    string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.OperationName, "evaluate_story", StringComparison.OrdinalIgnoreCase));
                if (alreadyQueued) return 0;
            }
            catch
            {
                // best-effort
            }

            var evaluators = _database.ListAgents()
                .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                    (a.Role.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) ||
                     a.Role.Equals("evaluator", StringComparison.OrdinalIgnoreCase) ||
                     a.Role.Equals("writer_evaluator", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(a => a.Id)
                .Take(Math.Max(1, maxEvaluators))
                .ToList();

            if (evaluators.Count == 0) return 0;

            var enqueued = 0;
            foreach (var evaluator in evaluators)
            {
                try
                {
                    var runId = $"evaluate_story_{storyId}_agent_{evaluator.Id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                    var metadata = new Dictionary<string, string>
                    {
                        ["storyId"] = storyId.ToString(),
                        ["agentId"] = evaluator.Id.ToString(),
                        ["agentName"] = evaluator.Name ?? string.Empty,
                        ["operation"] = "evaluate_story",
                        ["trigger"] = trigger
                    };

                    _commandDispatcher.Enqueue(
                        "evaluate_story",
                        async ctx =>
                        {
                            var (success, score, error) = await EvaluateStoryWithAgentAsync(storyId, evaluator.Id);
                            var msg = success ? $"Valutazione completata. Score: {score:F2}" : $"Valutazione fallita: {error}";
                            return new CommandResult(success, msg);
                        },
                        runId: runId,
                        threadScope: $"story/evaluate/agent_{evaluator.Id}",
                        metadata: metadata,
                        priority: priority);

                    enqueued++;
                }
                catch
                {
                    // best-effort per evaluator
                }
            }

            return enqueued;
        }
        catch
        {
            return 0;
        }
    }

    public string? EnqueueDeleteStoryCommand(long storyId, string trigger, int priority = 2)
    {
        try
        {
            if (storyId <= 0) return null;
            if (_commandDispatcher == null) return null;

            // De-dup: skip if already queued/running.
            try
            {
                var alreadyQueued = _commandDispatcher.GetActiveCommands().Any(s =>
                    s.Metadata != null &&
                    s.Metadata.TryGetValue("storyId", out var sid) &&
                    string.Equals(sid, storyId.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.OperationName, "delete_story", StringComparison.OrdinalIgnoreCase));
                if (alreadyQueued) return null;
            }
            catch
            {
                // best-effort
            }

            var runId = $"delete_story_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            _commandDispatcher.Enqueue(
                "delete_story",
                ctx =>
                {
                    var ok = DeleteStoryCompletelySingle(storyId, trigger, out var msg);
                    return Task.FromResult(new CommandResult(ok, ok ? "Story deleted" : $"Delete failed: {msg}"));
                },
                runId: runId,
                threadScope: "story/delete",
                metadata: new Dictionary<string, string>
                {
                    ["storyId"] = storyId.ToString(),
                    ["operation"] = "delete_story",
                    ["trigger"] = trigger
                },
                priority: priority);

            return runId;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enqueue delete_story for story {StoryId}", storyId);
            return null;
        }
    }

    private sealed record RevisionChunk(int Start, string Text, string TrailingSeparator);

    private static string BuildCriticExtractorInput(string storyText, IReadOnlyList<StoryEvaluation> evaluations)
    {
        var sb = new StringBuilder(Math.Max(1024, storyText?.Length ?? 0));
        sb.AppendLine("STORIA ORIGINALE (riferimento):");
        sb.AppendLine(storyText ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("REPORT VALUTATORI COMPLETI:");
        sb.AppendLine();

        for (var i = 0; i < evaluations.Count; i++)
        {
            var e = evaluations[i];
            sb.AppendLine($"VALUTATORE #{i + 1}");
            if (!string.IsNullOrWhiteSpace(e.AgentName))
            {
                sb.AppendLine($"Agent: {e.AgentName}");
            }
            sb.AppendLine($"TotalScore: {e.TotalScore:F2}");
            sb.AppendLine($"NarrativeCoherenceScore: {e.NarrativeCoherenceScore}");
            if (!string.IsNullOrWhiteSpace(e.NarrativeCoherenceDefects))
                sb.AppendLine($"NarrativeCoherenceDefects: {e.NarrativeCoherenceDefects}");
            sb.AppendLine($"OriginalityScore: {e.OriginalityScore}");
            if (!string.IsNullOrWhiteSpace(e.OriginalityDefects))
                sb.AppendLine($"OriginalityDefects: {e.OriginalityDefects}");
            sb.AppendLine($"EmotionalImpactScore: {e.EmotionalImpactScore}");
            if (!string.IsNullOrWhiteSpace(e.EmotionalImpactDefects))
                sb.AppendLine($"EmotionalImpactDefects: {e.EmotionalImpactDefects}");
            sb.AppendLine($"ActionScore: {e.ActionScore}");
            if (!string.IsNullOrWhiteSpace(e.ActionDefects))
                sb.AppendLine($"ActionDefects: {e.ActionDefects}");
            if (!string.IsNullOrWhiteSpace(e.RawJson))
            {
                sb.AppendLine("RawJson:");
                sb.AppendLine(e.RawJson);
            }
            sb.AppendLine();
        }

        sb.AppendLine("Output richiesto:");
        sb.AppendLine("- Elenco puntato di criticita' concrete da correggere.");
        sb.AppendLine("- Nessun riassunto della storia.");
        sb.AppendLine("- Nessun complimento o giudizio generico.");
        return sb.ToString();
    }

    private static string BuildRevisionChunkUserInput(string chunkText, int chunkIndex, int chunkCount, string? criticIssues, bool includeCriticSection)
    {
        var sb = new StringBuilder(chunkText?.Length ?? 256);
        if (includeCriticSection && !string.IsNullOrWhiteSpace(criticIssues))
        {
            sb.AppendLine("CRITICITA' DA CORREGGERE (generate da critic_extractor):");
            sb.AppendLine(criticIssues.Trim());
            sb.AppendLine();
        }

        sb.AppendLine($"TESTO DA REVISIONARE (chunk {chunkIndex}/{chunkCount}):");
        sb.AppendLine(chunkText ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("Restituisci solo il testo revisionato del chunk, senza spiegazioni.");
        return sb.ToString();
    }

    private static string? NormalizeCriticExtractorIssues(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!text.StartsWith("{", StringComparison.Ordinal))
        {
            return text;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("issues", out var issuesNode) ||
                issuesNode.ValueKind != JsonValueKind.Array)
            {
                return text;
            }

            var lines = new List<string>();
            foreach (var item in issuesNode.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    lines.Add("- " + value.Trim());
                }
            }

            return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return text;
        }
    }

    private static List<RevisionChunk> SplitIntoRevisionChunks(string text, int approxChunkChars)
    {
        var result = new List<RevisionChunk>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var normalized = text.Replace("\r\n", "\n");
        var i = 0;
        while (i < normalized.Length)
        {
            var remaining = normalized.Length - i;
            var take = Math.Min(approxChunkChars, remaining);
            var end = i + take;

            if (end >= normalized.Length)
            {
                // Last chunk: keep as-is.
                var last = normalized.Substring(i, normalized.Length - i);
                result.Add(new RevisionChunk(i, last, TrailingSeparator: string.Empty));
                break;
            }

            // Prefer cutting at a newline within the window.
            var cutPos = normalized.LastIndexOf('\n', end - 1, take);

            // If no newline, prefer cutting on whitespace to avoid splitting words.
            if (cutPos <= i)
            {
                cutPos = FindCutOnWhitespace(normalized, i, end);
            }

            if (cutPos <= i)
            {
                // As a last resort, cut at end (may split a long token, but avoids infinite loops).
                var fallback = normalized.Substring(i, take);
                result.Add(new RevisionChunk(i, fallback, TrailingSeparator: string.Empty));
                i += take;
                continue;
            }

            // Capture trailing whitespace (newline/space/etc.) as separator and strip it from the chunk text.
            var separatorStart = cutPos;
            var separatorLen = 0;
            while (separatorStart + separatorLen < normalized.Length &&
                   char.IsWhiteSpace(normalized[separatorStart + separatorLen]))
            {
                separatorLen++;

                // Prevent pathological huge separators.
                if (separatorLen >= 16) break;
            }

            var chunkCore = normalized.Substring(i, cutPos - i);
            var trailing = separatorLen > 0
                ? normalized.Substring(separatorStart, separatorLen)
                : string.Empty;

            result.Add(new RevisionChunk(i, chunkCore, TrailingSeparator: trailing));
            i = separatorStart + separatorLen;
        }

        return result;
    }

    private static int FindCutOnWhitespace(string text, int start, int preferredEnd)
    {
        // Look backward for whitespace to cut cleanly.
        for (var j = preferredEnd - 1; j > start; j--)
        {
            if (char.IsWhiteSpace(text[j])) return j;
        }

        // If none, look forward a bit for whitespace (avoid cutting a word if we're in the middle).
        var forwardLimit = Math.Min(text.Length, preferredEnd + 256);
        for (var j = preferredEnd; j < forwardLimit; j++)
        {
            if (char.IsWhiteSpace(text[j])) return j;
        }

        return -1;
    }

    private void EnqueueAutomaticStoryEvaluations(long storyId, string trigger, int priority)
    {
        try
        {
            if (storyId <= 0) return;
            if (_commandDispatcher == null) return;

              var evaluators = _database.ListAgents()
                  .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                      (a.Role.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) ||
                       a.Role.Equals("evaluator", StringComparison.OrdinalIgnoreCase) ||
                       a.Role.Equals("writer_evaluator", StringComparison.OrdinalIgnoreCase)))
                  .OrderBy(a => a.Id)
                  .Take(2)
                  .ToList();

            if (evaluators.Count == 0) return;

            foreach (var evaluator in evaluators)
            {
                try
                {
                    var runId = $"evaluate_story_{storyId}_agent_{evaluator.Id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                    var metadata = new Dictionary<string, string>
                    {
                        ["storyId"] = storyId.ToString(),
                        ["agentId"] = evaluator.Id.ToString(),
                        ["agentName"] = evaluator.Name ?? string.Empty,
                        ["operation"] = "evaluate_story",
                        ["trigger"] = trigger
                    };

                    _commandDispatcher.Enqueue(
                        "evaluate_story",
                        async ctx =>
                        {
                            var (success, score, error) = await EvaluateStoryWithAgentAsync(storyId, evaluator.Id);
                            var msg = success ? $"Valutazione completata. Score: {score:F2}" : $"Valutazione fallita: {error}";
                            return new CommandResult(success, msg);
                        },
                        runId: runId,
                        threadScope: $"story/evaluate/agent_{evaluator.Id}",
                        metadata: metadata,
                        priority: priority);
                }
                catch
                {
                    // best-effort per-evaluator
                }
            }
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Generates ambient audio for a story using AudioCraft based on the 'ambience' fields in tts_schema.json.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> GenerateAmbienceForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        if (!IsAmbienceGenerationEnabled())
        {
            return (true, "Generazione rumori ambientali disattivata da impostazioni.");
        }

        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, null);

        var deleteCmd = new DeleteAmbienceCommand(this);
        var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
        if (!cleanupOk)
        {
            return (false, cleanupMessage ?? "Impossibile cancellare i rumori ambientali esistenti");
        }
        
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            var (dispatcherSuccess, dispatcherMessage) = await GenerateAmbienceAudioInternalAsync(context, dispatcherRunId);
            if (dispatcherSuccess)
            {
                // In batch-worker mode the status command wrapper may not run in-process:
                // advance status here as well to keep the chain coherent.
                TryChangeStatus(story.Id, "generate_ambience_audio", dispatcherRunId);
            }
            return (dispatcherSuccess, dispatcherMessage);
        }
        var (success, message) = await StartAmbienceAudioGenerationAsync(context);
        return (success, message);
    }

    public Task<(bool success, string? message)> GenerateTtsSchemaJsonAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new GenerateTtsSchemaCommand(this));
    }

    public Task<(bool success, string? message)> RepairTtsAudioMetadataAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new RepairTtsSchemaAudioMetadataCommand(this));
    }

    public Task<(bool success, string? message)> AssignVoicesAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new AssignVoicesCommand(this));
    }

    /// <summary>
    /// Normalizza i nomi dei personaggi nel file tts_schema.json usando la lista dei personaggi della storia.
    /// </summary>
    public Task<(bool success, string? message)> NormalizeCharacterNamesAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new NormalizeCharacterNamesCommand(this));
    }

    private async Task<(bool success, List<StoryCharacter> characters, string? error)> TryAutoExtractCharactersAsync(
        StoryRecord story,
        int maxAttempts)
    {
        if (story == null) return (false, new List<StoryCharacter>(), "Storia non trovata");
        if (_kernelFactory == null) return (false, new List<StoryCharacter>(), "Kernel factory non disponibile");
        if (maxAttempts <= 0) return (false, new List<StoryCharacter>(), "Tentativi estrazione personaggi disabilitati");

        var storyText = !string.IsNullOrWhiteSpace(story.StoryRevised)
            ? story.StoryRevised
            : story.StoryRaw;
        if (string.IsNullOrWhiteSpace(storyText))
            return (false, new List<StoryCharacter>(), "Testo storia non disponibile per estrazione personaggi");

        int attempts = 0;
        string? lastError = null;

        Agent? author = null;
        if (story.AgentId.HasValue)
        {
            author = _database.GetAgentById(story.AgentId.Value);
            if (author != null && !author.IsActive)
            {
                author = null;
            }
        }

        if (author?.ModelId.HasValue == true)
        {
            var modelInfo = _database.GetModelInfoById(author.ModelId.Value);
            if (!string.IsNullOrWhiteSpace(modelInfo?.Name))
            {
                attempts++;
                var result = await ExtractCharactersWithModelAsync(
                    modelInfo.Name,
                    storyText,
                    author.Temperature,
                    author.TopP,
                    author.RepeatPenalty,
                    author.TopK,
                    author.RepeatLastN,
                    author.NumPredict,
                    null);
                if (result.success)
                {
                    return result;
                }
                lastError = result.error;
            }
        }

        if (attempts >= maxAttempts)
        {
            return (false, new List<StoryCharacter>(), lastError ?? "Estrazione personaggi fallita");
        }

        if (_scopeFactory == null)
        {
            return (false, new List<StoryCharacter>(), lastError ?? "IServiceScopeFactory non disponibile");
        }

        using var fallbackScope = _scopeFactory.CreateScope();
        var fallbackService = fallbackScope.ServiceProvider.GetService<ModelFallbackService>();
        if (fallbackService == null)
        {
            return (false, new List<StoryCharacter>(), lastError ?? "ModelFallbackService non disponibile");
        }

        var triedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (author?.ModelId.HasValue == true)
        {
            var modelInfo = _database.GetModelInfoById(author.ModelId.Value);
            if (!string.IsNullOrWhiteSpace(modelInfo?.Name))
            {
                triedNames.Add(modelInfo.Name);
            }
        }

        var remaining = Math.Max(0, maxAttempts - attempts);
        int triedFallback = 0;

        var (fallbackResult, _) = await fallbackService.ExecuteWithFallbackAsync(
            "writer",
            author?.ModelId,
            async modelRole =>
            {
                var modelName = modelRole.Model?.Name;
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    throw new InvalidOperationException("Fallback ModelRole has no Model.Name");
                }

                return await ExtractCharactersWithModelAsync(
                    modelName,
                    storyText,
                    author?.Temperature,
                    modelRole.TopP ?? author?.TopP,
                    author?.RepeatPenalty,
                    modelRole.TopK ?? author?.TopK,
                    author?.RepeatLastN,
                    author?.NumPredict,
                    modelRole.Instructions);
            },
            validateResult: r => r.success,
            shouldTryModelRole: modelRole =>
            {
                if (triedFallback >= remaining) return false;
                var name = modelRole.Model?.Name;
                if (string.IsNullOrWhiteSpace(name)) return false;
                if (!triedNames.Add(name)) return false;
                triedFallback++;
                return true;
            },
            agentId: author?.Id);

        if (fallbackResult.success)
        {
            return fallbackResult;
        }

        return (false, new List<StoryCharacter>(), fallbackResult.error ?? lastError ?? "Estrazione personaggi fallita");
    }

    private async Task<(bool success, List<StoryCharacter> characters, string? error)> ExtractCharactersWithModelAsync(
        string modelName,
        string storyText,
        double? temperature,
        double? topP,
        double? repeatPenalty,
        int? topK,
        int? repeatLastN,
        int? numPredict,
        string? instructionsOverride)
    {
        if (_kernelFactory == null)
        {
            return (false, new List<StoryCharacter>(), "Kernel factory non disponibile");
        }

        var systemPrompt = "Sei un assistente che estrae la lista personaggi da una storia. " +
                           "Rispondi SOLO con un JSON array. Ogni elemento deve avere: " +
                           "name, gender (male|female|robot|alien), age, title, role, aliases (array). " +
                           "Non aggiungere testo extra.";
        if (!string.IsNullOrWhiteSpace(instructionsOverride))
        {
            systemPrompt += "\n" + instructionsOverride.Trim();
        }

        var messages = new List<ConversationMessage>
        {
            new ConversationMessage { Role = "system", Content = systemPrompt },
            new ConversationMessage
            {
                Role = "user",
                Content = "TESTO:\n\n" + storyText + "\n\n" +
                          "Restituisci solo il JSON array dei personaggi."
            }
        };

        var stepId = RequestIdGenerator.Generate();
        _logger?.LogInformation("[StepID: {StepId}] Generazione personaggi da modello {Model}", stepId, modelName);
        string responseText;
        var usedStandardPath = false;

        if (_scopeFactory != null)
        {
            using var scope = _scopeFactory.CreateScope();
            var callCenter = scope.ServiceProvider.GetService<ICallCenter>()
                ?? ServiceLocator.Services?.GetService<ICallCenter>();
            if (callCenter != null)
            {
                usedStandardPath = true;
                var syntheticAgent = new Agent
                {
                    Name = "Character Extractor",
                    Role = "character_extractor",
                    ModelName = modelName,
                    Temperature = temperature,
                    TopP = topP,
                    RepeatPenalty = repeatPenalty,
                    TopK = topK,
                    RepeatLastN = repeatLastN,
                    NumPredict = numPredict,
                    IsActive = true
                };

                var history = new ChatHistory(messages);
                var callResult = await callCenter.CallAgentAsync(
                    storyId: 0,
                    threadId: unchecked((modelName ?? string.Empty).GetHashCode(StringComparison.Ordinal) ^ 0x4C51),
                    agent: syntheticAgent,
                    history: history,
                    options: new CallOptions
                    {
                        Operation = "extract_characters",
                        Timeout = TimeSpan.FromSeconds(90),
                        MaxRetries = 1,
                        UseResponseChecker = false,
                        AllowFallback = false,
                        AskFailExplanation = true,
                        DeterministicChecks =
                        {
                            new CheckExtractableJsonArray
                            {
                                Options = Options.Create<object>(new Dictionary<string, object>
                                {
                                    ["ErrorMessage"] = "JSON personaggi non trovato"
                                })
                            }
                        }
                    }).ConfigureAwait(false);

                responseText = callResult.ResponseText ?? string.Empty;
            }
            else
            {
                responseText = string.Empty;
            }
        }
        else
        {
            responseText = string.Empty;
        }

        if (!usedStandardPath)
        {
            _customLogger?.MarkLatestModelResponseResult("FAILED", "ICallCenter non disponibile per estrazione personaggi");
            return (false, new List<StoryCharacter>(), "ICallCenter non disponibile per estrazione personaggi");
        }

        var json = ExtractJsonArray(responseText);
        if (string.IsNullOrWhiteSpace(json))
        {
            _customLogger?.MarkLatestModelResponseResult("FAILED", "JSON personaggi non trovato");
            return (false, new List<StoryCharacter>(), "JSON personaggi non trovato");
        }

        var (characters, parseError) = StoryCharacterParser.TryFromJson(json);
        if (parseError != null || characters.Count == 0)
        {
            _customLogger?.MarkLatestModelResponseResult("FAILED", parseError ?? "Lista personaggi vuota");
            return (false, new List<StoryCharacter>(), parseError ?? "Lista personaggi vuota");
        }

        EnsureNarratorCharacter(characters);
        _customLogger?.MarkLatestModelResponseResult("SUCCESS", null);
        return (true, characters, null);
    }

    private sealed class CheckExtractableJsonArray : CheckBase
    {
        public override string Rule => "Output deve contenere un array JSON estraibile.";
        public override string GenericErrorDescription => "JSON personaggi non trovato";

        public override IDeterministicResult Execute(string textToCheck)
        {
            var started = DateTime.UtcNow;
            var json = ExtractJsonArray(textToCheck);
            return new DeterministicResult
            {
                Successed = !string.IsNullOrWhiteSpace(json),
                Message = string.IsNullOrWhiteSpace(json) ? "JSON personaggi non trovato" : "ok",
                CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
            };
        }
    }

    private static string? ExtractJsonArray(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var source = text.Trim();
        source = Regex.Replace(source, "(?is)```(?:json)?\\s*(.*?)\\s*```", "$1").Trim();

        static string? TryParseArrayFromJson(string candidate)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.GetRawText();
                }

                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var key in new[] { "personaggi", "characters", "Personaggi", "Characters" })
                    {
                        if (doc.RootElement.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        {
                            return arr.GetRawText();
                        }
                    }
                }
            }
            catch
            {
                // ignore and fallback to substring extraction
            }

            return null;
        }

        var parsedWhole = TryParseArrayFromJson(source);
        if (!string.IsNullOrWhiteSpace(parsedWhole))
        {
            return parsedWhole;
        }

        var arrayStart = source.IndexOf('[');
        var arrayEnd = source.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart)
        {
            var arrayCandidate = source.Substring(arrayStart, arrayEnd - arrayStart + 1).Trim();
            var parsedArray = TryParseArrayFromJson(arrayCandidate);
            if (!string.IsNullOrWhiteSpace(parsedArray))
            {
                return parsedArray;
            }
        }

        var objStart = source.IndexOf('{');
        var objEnd = source.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
        {
            var objCandidate = source.Substring(objStart, objEnd - objStart + 1).Trim();
            var parsedObject = TryParseArrayFromJson(objCandidate);
            if (!string.IsNullOrWhiteSpace(parsedObject))
            {
                return parsedObject;
            }
        }

        return null;
    }

    private static void EnsureNarratorCharacter(List<StoryCharacter> characters)
    {
        if (characters.Any(c => string.Equals(c.Name, "Narratore", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(c.Name, "Narrator", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        characters.Insert(0, new StoryCharacter
        {
            Name = "Narratore",
            Gender = "male",
            Role = "narrator"
        });
    }

    /// <summary>
    /// Normalizza i sentimenti nel file tts_schema.json ai valori supportati dal TTS.
    /// </summary>
    public Task<(bool success, string? message)> NormalizeSentimentsAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new NormalizeSentimentsCommand(this));
    }

    public Task<(bool success, string? message)> DeleteFinalMixAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteFinalMixCommand(this));
    }

    public Task<(bool success, string? message)> DeleteMusicAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteMusicCommand(this));
    }

    public Task<(bool success, string? message)> DeleteFxAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteFxCommand(this));
    }

    public Task<(bool success, string? message)> DeleteAmbienceAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteAmbienceCommand(this));
    }

    public Task<(bool success, string? message)> DeleteTtsSchemaAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteTtsSchemaCommand(this));
    }

    public Task<(bool success, string? message)> DeleteTtsAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteTtsCommand(this));
    }

    public Task<(bool success, string? message)> DeleteStoryTaggedAsync(long storyId)
    {
        return ExecuteStoryCommandAsync(
            storyId,
            new DeleteStoryTaggedCommand(this));
    }

    /// <summary>
    /// Esegue un test tts_schema sullo storyId specificato per tutti i modelli abilitati e aggiorna il campo TtsScore.
    /// </summary>
    public async Task<(bool success, string message)> TestTtsSchemaAllModelsAsync(long storyId, CancellationToken cancellationToken = default)
    {
        var story = _database.GetStoryById(storyId);
        if (story == null) return (false, $"Story {storyId} non trovata");
        if (_multiStepOrchestrator == null) return (false, "MultiStepOrchestrator non disponibile");

        var models = _database.ListModels()
            .Where(m => m.Enabled && !m.NoTools)
            .OrderBy(m => m.TestDurationSeconds ?? double.MaxValue)
            .ToList();
        if (models.Count == 0) return (false, "Nessun modello abilitato trovato");

        var results = new List<string>();
        var anyFail = false;

        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (ok, msg) = await RunTtsSchemaForModelAsync(story, model, cancellationToken);
                results.Add($"{model.Name}: {(ok ? "ok" : "fail")} ({msg})");
                if (!ok) anyFail = true;
            }
            catch (Exception ex)
            {
                anyFail = true;
                results.Add($"{model.Name}: fail ({ex.Message})");
                _logger?.LogError(ex, "Errore test tts_schema per il modello {Model}", model.Name);
            }
        }

        var summary = string.Join("; ", results);
        return (!anyFail, summary);
    }

    private async Task<(bool success, string message)> RunTtsSchemaForModelAsync(StoryRecord story, ModelInfo model, CancellationToken cancellationToken)
    {
        if (_multiStepOrchestrator == null)
        {
            return (false, "MultiStepOrchestrator non disponibile");
        }

        if (string.IsNullOrWhiteSpace(story.StoryTagged))
        {
            return (false, "Il testo taggato della storia e vuoto");
        }

        var ttsAgent = _database.ListAgents()
            .FirstOrDefault(a => a.IsActive && a.Role?.Equals("tts_json", StringComparison.OrdinalIgnoreCase) == true && a.MultiStepTemplateId.HasValue);

        if (ttsAgent == null)
        {
            return (false, "Nessun agente tts_json con template multi-step configurato");
        }

        var template = _database.GetStepTemplateById(ttsAgent.MultiStepTemplateId!.Value);
        if (template == null || !string.Equals(template.TaskType, "tts_schema", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Template multi-step TTS non trovato o con task_type errato");
        }

        if (!model.Id.HasValue)
        {
            return (false, "Modello senza Id valido");
        }

        var modelId = model.Id.Value;

        // Prepara cartella storia
        var folderPath = EnsureStoryFolder(story);

        var configPayload = JsonSerializer.Serialize(new
        {
            workingFolder = folderPath,
            storyText = story.StoryTagged ?? string.Empty
        });

        var threadId = LogScope.CurrentThreadId ?? Environment.CurrentManagedThreadId;
        var sw = Stopwatch.StartNew();
        const string testGroup = "tts";
        int? runId = null;
        int? stepId = null;
        long? executionId = null;
        var totalSteps = CountSteps(template.StepPrompt);

        try
        {
            runId = _database.CreateTestRun(
                modelId,
                testGroup,
                description: $"tts_schema story {story.Id}",
                passed: false,
                durationMs: null,
                notes: null,
                testFolder: folderPath);

            if (runId.HasValue)
            {
                stepId = _database.AddTestStep(
                    runId.Value,
                    1,
                    "tts_schema",
                    JsonSerializer.Serialize(new { storyId = story.Id, model = model.Name }));
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Impossibile creare i record di test TTS per il modello {Model}", model.Name);
        }

        try
        {
            executionId = await _multiStepOrchestrator.StartTaskExecutionAsync(
                taskType: "tts_schema",
                entityId: story.Id,
                stepPrompt: template.StepPrompt,
                executorAgentId: ttsAgent.Id,
                checkerAgentId: null,
                configOverrides: configPayload,
                initialContext: story.StoryTagged ?? string.Empty,
                threadId: threadId,
                templateInstructions: string.IsNullOrWhiteSpace(template.Instructions) ? null : template.Instructions,
                executorModelOverrideId: modelId);

            if (executionId.HasValue)
            {
                await _multiStepOrchestrator.ExecuteAllStepsAsync(executionId.Value, threadId, null, cancellationToken);
            }

            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            var success = File.Exists(schemaPath);
            var score = success ? 10 : 0;
            _database.UpdateModelTtsScore(modelId, score);

            if (stepId.HasValue)
            {
                try
                {
                    var outputJson = success
                        ? JsonSerializer.Serialize(new { message = "tts_schema generato" })
                        : null;
                    var error = success ? null : "File tts_schema.json non trovato";
                    _database.UpdateTestStepResult(stepId.Value, success, outputJson, error, sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Impossibile aggiornare lo step di test TTS per il modello {Model}", model.Name);
                }
            }

            if (runId.HasValue)
            {
                try
                {
                    _database.UpdateTestRunResult(runId.Value, success, sw.ElapsedMilliseconds);
                    _database.RecalculateModelGroupScore(modelId, testGroup);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Impossibile aggiornare il run di test TTS per il modello {Model}", model.Name);
                }
            }

            return (success, success ? "tts_schema generato" : "File tts_schema.json non trovato");
        }
        catch (Exception ex)
        {
            // Calcola punteggio parziale in base allo step raggiunto
            var partialScore = 0.0;
            if (executionId.HasValue)
            {
                try
                {
                    var exec = _database.GetTaskExecutionById(executionId.Value);
                    var completedSteps = exec != null ? Math.Max(0, exec.CurrentStep - 1) : 0;

                    var steps = _database.GetTaskExecutionSteps(executionId.Value);
                    if (steps != null && steps.Count > 0)
                    {
                        var maxStepRecorded = steps.Max(s => s.StepNumber);
                        completedSteps = Math.Max(completedSteps, maxStepRecorded);
                    }

                    if (totalSteps > 0)
                    {
                        partialScore = Math.Round((double)completedSteps / totalSteps * 10.0, 1);
                    }
                }
                catch (Exception innerEx)
                {
                    _logger?.LogWarning(innerEx, "Impossibile calcolare punteggio parziale per il modello {Model}", model.Name);
                }
            }

                    _database.UpdateModelTtsScore(modelId, partialScore);

            if (stepId.HasValue)
            {
                try
                {
                    var errorMsg = ex.Message;
                    _database.UpdateTestStepResult(stepId.Value, false, null, errorMsg, sw.ElapsedMilliseconds);
                }
                catch (Exception innerEx)
                {
                    _logger?.LogWarning(innerEx, "Impossibile aggiornare il fallimento del test TTS per il modello {Model}", model.Name);
                }
            }

            if (runId.HasValue)
            {
                try
                {
                    var errMsg = ex.Message;
                    if (partialScore > 0 && totalSteps > 0)
                    {
                        errMsg = $"Step incompleti: punteggio parziale {partialScore:0.0}/10. Dettaglio: {ex.Message}";
                    }

                    _database.UpdateTestRunResult(runId.Value, false, sw.ElapsedMilliseconds);
                    _database.UpdateTestRunNotes(runId.Value, errMsg);
                    _database.RecalculateModelGroupScore(modelId, testGroup);
                }
                catch (Exception innerEx)
                {
                    _logger?.LogWarning(innerEx, "Impossibile chiudere il run di test TTS per il modello {Model}", model.Name);
                }
            }

            return (false, ex.Message);
        }
        finally
        {
            sw.Stop();
        }
    }

    private static int CountSteps(string stepPrompt)
    {
        if (string.IsNullOrWhiteSpace(stepPrompt)) return 0;
        var lines = stepPrompt.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        foreach (var l in lines)
        {
            var trimmed = l.TrimStart();
            if (char.IsDigit(trimmed.FirstOrDefault()))
            {
                count++;
            }
        }
        return count;
    }

    private static string CleanTtsText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var cleaned = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Skip lines that are only bracketed metadata (e.g., [PERSONAGGIO: ...])
            if (Regex.IsMatch(trimmed, @"^\[[^\]]+\]$")) continue;
            // Remove inline bracketed metadata
            var withoutTags = Regex.Replace(trimmed, @"\[[^\]]+\]", string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(withoutTags))
            {
                cleaned.Add(withoutTags);
            }
        }
        var result = string.Join(" ", cleaned);
        
        // Normalize punctuation for TTS:
        // - Keep essential punctuation for pauses and intonation: . , ; : ? !
        // - Convert multiple dots (... or ..) to single dot
        // - Normalize whitespace
        result = Regex.Replace(result, @"\.{2,}", ".");  // Multiple dots ? single dot
        result = Regex.Replace(result, @"\s+", " ");  // Normalize whitespace
        return result.Trim();
    }

    private static readonly HashSet<char> TtsSchemaDisallowedTextChars = new()
    {
        '*', '-', '_', '"', '(', ')',
        // Quote-like punctuation that often degrades TTS reading
        '«', '»', '“', '”', '„', '‟'
    };

    private static string FilterTtsSchemaTextField(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // Normalize typographic apostrophes to ASCII apostrophe.
            if (ch is '’' or '‘')
            {
                sb.Append('\'');
                continue;
            }
            if (TtsSchemaDisallowedTextChars.Contains(ch))
                continue;
            sb.Append(ch);
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static void SanitizeTtsSchemaTextFields(JsonObject root)
    {
        if (root == null) return;

        if (!root.TryGetPropertyValue("timeline", out var timelineNode))
            root.TryGetPropertyValue("Timeline", out timelineNode);

        if (timelineNode is not JsonArray timelineArray)
            return;

        foreach (var item in timelineArray.OfType<JsonObject>())
        {
            try
            {
                if (!IsPhraseEntry(item))
                    continue;

                SanitizeStringProperties(item,
                    "text", "Text",
                    "ambience", "Ambience", "ambience_description", "ambienceDescription",
                    "ambient_sound_description", "AmbientSoundDescription", "ambientSoundDescription",
                    "ambient_sound_tags", "AmbientSoundTags", "ambientSoundTags",
                    "ambientSounds", "AmbientSounds", "ambient_sounds",
                    "fx_tags", "FxTags", "fxTags",
                    "fx_description", "FxDescription", "fxDescription",
                    "music_tags", "MusicTags", "musicTags",
                    "music_description", "MusicDescription", "musicDescription");
            }
            catch
            {
                // best-effort: never fail the save path
            }
        }
    }

    private static void SanitizeStringProperties(JsonObject obj, params string[] keys)
    {
        if (obj == null || keys == null || keys.Length == 0) return;

        var target = new HashSet<string>(keys.Where(k => !string.IsNullOrWhiteSpace(k)), StringComparer.OrdinalIgnoreCase);
        if (target.Count == 0) return;

        foreach (var kvp in obj.ToList())
        {
            if (!target.Contains(kvp.Key))
                continue;

            if (kvp.Value is JsonValue v && v.TryGetValue<string>(out var s))
            {
                obj[kvp.Key] = FilterTtsSchemaTextField(s);
            }
        }
    }

    private void SaveSanitizedTtsSchemaJson(string schemaPath, string json)
    {
        if (string.IsNullOrWhiteSpace(schemaPath)) return;

        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root != null)
            {
                SanitizeTtsSchemaTextFields(root);
                File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
                return;
            }
        }
        catch
        {
            // fall back to raw write
        }

        File.WriteAllText(schemaPath, json);
    }

    private async Task SaveSanitizedTtsSchemaJsonAsync(string schemaPath, string json)
    {
        if (string.IsNullOrWhiteSpace(schemaPath)) return;

        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root != null)
            {
                SanitizeTtsSchemaTextFields(root);
                await File.WriteAllTextAsync(schemaPath, root.ToJsonString(SchemaJsonOptions));
                return;
            }
        }
        catch
        {
            // fall back to raw write
        }

        await File.WriteAllTextAsync(schemaPath, json);
    }

    /// <summary>
    /// Remove timeline phrase entries whose cleaned TTS text is empty (e.g. text is only "..." or brackets/metadata).
    /// This prevents empty/dialog placeholders from being persisted in tts_schema.json and avoids producing entries without audio/timing.
    /// </summary>
    private static void PruneEmptyTextPhrases(JsonArray timelineArray)
    {
        if (timelineArray == null) return;
        var toRemove = new List<JsonNode>();
        foreach (var node in timelineArray.OfType<JsonObject>())
        {
            try
            {
                if (!IsPhraseEntry(node)) continue;
                if (!TryReadPhrase(node, out var character, out var text, out var emotion)) continue;
                var clean = CleanTtsText(text);
                if (string.IsNullOrWhiteSpace(clean))
                {
                    toRemove.Add(node);
                }
            }
            catch
            {
                // best-effort: ignore parse errors
            }
        }

        foreach (var n in toRemove)
        {
            try { timelineArray.Remove(n); } catch { }
        }
    }

    private string EnsureStoryFolder(StoryRecord story)
    {
        var baseFolder = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder");
        Directory.CreateDirectory(baseFolder);

        if (string.IsNullOrWhiteSpace(story.Folder))
        {
            var paddedId = story.Id.ToString("D5");
            var folderName = $"{paddedId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            folderName = SanitizeFolderName(folderName);
            _database.UpdateStoryFolder(story.Id, folderName);
            story.Folder = folderName;
        }

        var folderPath = Path.Combine(baseFolder, story.Folder);
        Directory.CreateDirectory(folderPath);
        return folderPath;
    }

    // Helper: delete a file if exists (best-effort)
    private void DeleteIfExists(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to delete file {Path}", path);
        }
    }

    private string? ResolveStoryFolderPath(StoryRecord story)
    {
        if (story == null || string.IsNullOrWhiteSpace(story.Folder)) return null;
        return Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", story.Folder);
    }

    internal bool ApplyStatusTransitionWithCleanup(StoryRecord story, string statusCode, string? runId = null)
    {
        if (story == null || string.IsNullOrWhiteSpace(statusCode)) return true;

        var status = _database.GetStoryStatusByCode(statusCode);
        if (status == null)
        {
            _logger?.LogWarning("Status code '{StatusCode}' not found in stories_status", statusCode);
            return true;
        }

        try
        {
            _database.UpdateStoryById(story.Id, statusId: status.Id, updateStatus: true);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to update story {StoryId} to status {StatusCode}", story.Id, statusCode);
        }

        if (!status.DeleteNextItems)
        {
            return true;
        }

        var folderPath = ResolveStoryFolderPath(story);
        try
        {
            CleanupNextItemsForStatus(story.Id, statusCode, folderPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Cleanup for status {StatusCode} failed for story {StoryId}", statusCode, story.Id);
        }

        return false;
    }

    private void CleanupNextItemsForStatus(long storyId, string statusCode, string? folderPath)
    {
        switch (statusCode)
        {
            case "revised":
                _database.DeleteEvaluationsForStory(storyId);
                _database.ClearStoryTagged(storyId);
                ResetGeneratedFlagsForMissingFolder(storyId);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    CleanAllGeneratedAssetsForTtsSchemaRegeneration(storyId, folderPath);
                }
                break;
            case "evaluated":
                _database.ClearStoryTagged(storyId);
                ResetGeneratedFlagsForMissingFolder(storyId);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    CleanAllGeneratedAssetsForTtsSchemaRegeneration(storyId, folderPath);
                }
                break;
            case "tagged":
                ResetGeneratedFlagsForMissingFolder(storyId);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    CleanAllGeneratedAssetsForTtsSchemaRegeneration(storyId, folderPath);
                }
                break;
            case "tts_schema_generated":
                ResetGeneratedFlagsAfterTtsSchema(storyId);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    CleanAudioAfterTtsSchemaGenerated(storyId, folderPath);
                }
                break;
            case "tts_generated":
                ResetGeneratedFlagsAfterTts(storyId);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    CleanAmbienceForRegeneration(storyId, folderPath);
                    CleanFxForRegeneration(storyId, folderPath);
                    CleanMusicForRegeneration(storyId, folderPath);
                    DeleteFinalMixAssets(storyId, folderPath);
                }
                break;
            case "ambient_generated":
                ResetGeneratedFlagsAfterAmbient(storyId);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    CleanFxForRegeneration(storyId, folderPath);
                    CleanMusicForRegeneration(storyId, folderPath);
                    DeleteFinalMixAssets(storyId, folderPath);
                }
                break;
            case "fx_generated":
                ResetGeneratedFlagsAfterFx(storyId);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    CleanMusicForRegeneration(storyId, folderPath);
                    DeleteFinalMixAssets(storyId, folderPath);
                }
                break;
            default:
                break;
        }
    }

    private void ResetGeneratedFlagsForMissingFolder(long storyId)
    {
        try { _database.UpdateStoryGeneratedTtsJson(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedTts(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedEffects(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedAmbient(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
    }

    private void ResetGeneratedFlagsAfterTtsSchema(long storyId)
    {
        try { _database.UpdateStoryGeneratedTts(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedAmbient(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedEffects(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
    }

    private void ResetGeneratedFlagsAfterTts(long storyId)
    {
        try { _database.UpdateStoryGeneratedAmbient(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedEffects(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
    }

    private void ResetGeneratedFlagsAfterAmbient(long storyId)
    {
        try { _database.UpdateStoryGeneratedEffects(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
    }

    private void ResetGeneratedFlagsAfterFx(long storyId)
    {
        try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
        try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
    }

    // Clean assets and flags before regenerating the TTS schema (full regeneration).
    // Deletes existing TTS/music/fx/ambience files and final mix, removes the old schema
    // and resets generated_* flags in the DB (best-effort).
    private void CleanAllGeneratedAssetsForTtsSchemaRegeneration(long storyId, string folderPath)
    {
        try
        {
            // Reset flags before regeneration
            try { _database.UpdateStoryGeneratedTtsJson(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedTts(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedEffects(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedAmbient(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }

            var schemaPath = Path.Combine(folderPath, "tts_schema.json");

            // If schema exists, parse it to find referenced files to delete
            if (File.Exists(schemaPath))
            {
                try
                {
                    var json = File.ReadAllText(schemaPath);
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root != null)
                    {
                        if (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode))
                        {
                            if (timelineNode is JsonArray arr)
                            {
                                foreach (var item in arr.OfType<JsonObject>())
                                {
                                    var fn = item.TryGetPropertyValue("fileName", out var f1) ? f1?.ToString() : null;
                                    if (string.IsNullOrWhiteSpace(fn)) fn = item.TryGetPropertyValue("FileName", out var f2) ? f2?.ToString() : fn;
                                    // Keep historical TTS files on disk for voiceId+text reuse.

                                    var amb = item.TryGetPropertyValue("ambienceFile", out var a1) ? a1?.ToString() : null;
                                    if (!string.IsNullOrWhiteSpace(amb)) DeleteIfExists(Path.Combine(folderPath, amb));

                                    var fx = item.TryGetPropertyValue("fxFile", out var x1) ? x1?.ToString() : null;
                                    if (string.IsNullOrWhiteSpace(fx)) fx = item.TryGetPropertyValue("fx_file", out var x2) ? x2?.ToString() : fx;
                                    if (!string.IsNullOrWhiteSpace(fx)) DeleteIfExists(Path.Combine(folderPath, fx));

                                    var music = item.TryGetPropertyValue("musicFile", out var m1) ? m1?.ToString() : null;
                                    if (!string.IsNullOrWhiteSpace(music)) DeleteIfExists(Path.Combine(folderPath, music));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unable to parse existing tts_schema.json for cleanup in {Folder}", folderPath);
                }

                // delete the schema itself to ensure fresh generation
                DeleteIfExists(schemaPath);
            }

            // delete final mix files
            DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
            DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during cleanup before TTS schema regeneration for story {Id}", storyId);
        }
    }

    // Clean only TTS audio files and remove timing/file refs for timeline entries.
    private void CleanBeforeTtsAudioGeneration(long storyId, string folderPath)
    {
        try
        {
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (!File.Exists(schemaPath))
            {
                // Nothing to clean in schema, but still remove final mix and music/fx/ambience
                DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
                DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));
                try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
                return;
            }

            var json = File.ReadAllText(schemaPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null) return;

            if (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode))
            {
                if (timelineNode is JsonArray arr)
                {
                    foreach (var item in arr.OfType<JsonObject>())
                    {
                        // only reset timing fields; keep existing file references to allow reuse
                        item.Remove("startMs");
                        item.Remove("StartMs");
                        item.Remove("durationMs");
                        item.Remove("DurationMs");
                        item.Remove("endMs");
                        item.Remove("EndMs");

                        // remove regenerated metadata that should be recalculated
                        item.Remove("ambientSoundsDuration");
                        item.Remove("ambient_sounds_duration");
                        item.Remove("fxFile");
                        item.Remove("fx_file");
                        item.Remove("music_file");
                    }
                }
            }

            // Save modified schema
            try
            {
                SanitizeTtsSchemaTextFields(root);
                var updated = root.ToJsonString(SchemaJsonOptions);
                File.WriteAllText(schemaPath, updated);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Unable to save cleaned tts_schema.json for story {Id}", storyId);
            }

            // delete music/fx/ambience files and final mix
            // attempt to delete any file with known prefixes as well
            foreach (var pat in new[] { "music_", "music_auto_", "fx_", "ambience_", "ambi_", "final_mix" })
            {
                try
                {
                    var files = Directory.GetFiles(folderPath).Where(f => Path.GetFileName(f).StartsWith(pat, StringComparison.OrdinalIgnoreCase));
                    foreach (var f in files) DeleteIfExists(f);
                }
                catch { }
            }

            // update flags
            try { _database.UpdateStoryGeneratedTts(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedEffects(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedAmbient(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during TTS audio cleanup for story {Id}", storyId);
        }
    }

    private void CleanAudioAfterTtsSchemaGenerated(long storyId, string folderPath)
    {
        try
        {
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (File.Exists(schemaPath))
            {
                try
                {
                    var json = File.ReadAllText(schemaPath);
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root != null && (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode)))
                    {
                        if (timelineNode is JsonArray arr)
                        {
                            foreach (var item in arr.OfType<JsonObject>())
                            {
                                var fn = item.TryGetPropertyValue("fileName", out var f1) ? f1?.ToString() : null;
                                if (string.IsNullOrWhiteSpace(fn)) fn = item.TryGetPropertyValue("FileName", out var f2) ? f2?.ToString() : fn;
                                // Keep historical TTS files on disk for voiceId+text reuse.

                                item.Remove("fileName");
                                item.Remove("FileName");
                                item.Remove("startMs");
                                item.Remove("StartMs");
                                item.Remove("durationMs");
                                item.Remove("DurationMs");
                                item.Remove("endMs");
                                item.Remove("EndMs");

                                var ambientFile =
                                    ReadString(item, "ambient_sound_file") ??
                                    ReadString(item, "AmbientSoundFile") ??
                                    ReadString(item, "ambientSoundFile") ??
                                    ReadString(item, "ambientSoundsFile") ??
                                    ReadString(item, "AmbientSoundsFile") ??
                                    ReadString(item, "ambienceFile") ??
                                    ReadString(item, "ambience_file");
                                if (!string.IsNullOrWhiteSpace(ambientFile))
                                {
                                    DeleteIfExists(Path.Combine(folderPath, ambientFile));
                                }
                                item.Remove("ambient_sound_file");
                                item.Remove("AmbientSoundFile");
                                item.Remove("ambientSoundFile");
                                item.Remove("ambientSoundsFile");
                                item.Remove("AmbientSoundsFile");
                                item.Remove("ambient_sound_source_path");
                                item.Remove("ambientSoundSourcePath");
                                item.Remove("AmbientSoundSourcePath");
                                item.Remove("ambient_sound_id");
                                item.Remove("ambientSoundId");
                                item.Remove("AmbientSoundId");
                                item.Remove("ambienceFile");
                                item.Remove("AmbienceFile");
                                item.Remove("ambience_file");

                                var fxFile = ReadString(item, "fx_file") ?? ReadString(item, "fxFile") ?? ReadString(item, "FxFile");
                                if (!string.IsNullOrWhiteSpace(fxFile))
                                {
                                    DeleteIfExists(Path.Combine(folderPath, fxFile));
                                }
                                item.Remove("fxFile");
                                item.Remove("FxFile");
                                item.Remove("fx_file");
                                item.Remove("fx_source_path");
                                item.Remove("fxSourcePath");
                                item.Remove("FxSourcePath");
                                item.Remove("fx_sound_id");
                                item.Remove("fxSoundId");
                                item.Remove("FxSoundId");

                                var musicFile = ReadString(item, "music_file") ?? ReadString(item, "musicFile") ?? ReadString(item, "MusicFile");
                                if (!string.IsNullOrWhiteSpace(musicFile))
                                {
                                    DeleteIfExists(Path.Combine(folderPath, musicFile));
                                }
                                item.Remove("musicFile");
                                item.Remove("MusicFile");
                                item.Remove("music_file");
                                item.Remove("music_source_path");
                                item.Remove("musicSourcePath");
                                item.Remove("MusicSourcePath");
                                item.Remove("music_sound_id");
                                item.Remove("musicSoundId");
                                item.Remove("MusicSoundId");
                            }
                        }
                    }

                    if (root != null)
                    {
                        SanitizeTtsSchemaTextFields(root);
                        File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unable to update schema when cleaning audio after tts_schema generation for story {Id}", storyId);
                }
            }

            // delete final mix
            DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
            DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));

            // update flags
            ResetGeneratedFlagsAfterTtsSchema(storyId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error cleaning audio after tts_schema generation for story {Id}", storyId);
        }
    }

    private void CleanMusicForRegeneration(long storyId, string folderPath)
    {
        try
        {
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (File.Exists(schemaPath))
            {
                try
                {
                    var json = File.ReadAllText(schemaPath);
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root != null && (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode)))
                    {
                        if (timelineNode is JsonArray arr)
                        {
                            foreach (var item in arr.OfType<JsonObject>())
                            {
                                var music = item.TryGetPropertyValue("musicFile", out var m1) ? m1?.ToString() : null;
                                if (!string.IsNullOrWhiteSpace(music)) DeleteIfExists(Path.Combine(folderPath, music));

                                item.Remove("musicFile");
                                item.Remove("musicStartMs");
                                item.Remove("musicDuration");
                                item.Remove("musicDurationMs");
                                item.Remove("music_file");
                                item.Remove("music_start_ms");
                                item.Remove("music_duration");
                            }
                        }
                    }

                    if (root != null)
                    {
                        SanitizeTtsSchemaTextFields(root);
                        File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unable to update schema when cleaning music for story {Id}", storyId);
                }
            }

            // delete final mix
            DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
            DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));

            // update flags
            try { _database.UpdateStoryGeneratedMusic(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error cleaning music for story {Id}", storyId);
        }
    }

    private void CleanFxForRegeneration(long storyId, string folderPath)
    {
        try
        {
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (File.Exists(schemaPath))
            {
                try
                {
                    var json = File.ReadAllText(schemaPath);
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root != null && (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode)))
                    {
                        if (timelineNode is JsonArray arr)
                        {
                            foreach (var item in arr.OfType<JsonObject>())
                            {
                                var fx = item.TryGetPropertyValue("fxFile", out var f1) ? f1?.ToString() : null;
                                if (string.IsNullOrWhiteSpace(fx)) fx = item.TryGetPropertyValue("fx_file", out var f2) ? f2?.ToString() : fx;
                                if (!string.IsNullOrWhiteSpace(fx)) DeleteIfExists(Path.Combine(folderPath, fx));

                                item.Remove("fxFile");
                                item.Remove("fx_file");
                                item.Remove("fxDuration");
                                item.Remove("FxDuration");
                                item.Remove("fxDurationMs");
                                item.Remove("fx_duration");
                                item.Remove("fx_duration_ms");
                            }
                        }
                    }

                    if (root != null)
                    {
                        SanitizeTtsSchemaTextFields(root);
                        File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unable to update schema when cleaning FX for story {Id}", storyId);
                }
            }

            // update flags
            try { _database.UpdateStoryGeneratedEffects(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error cleaning FX for story {Id}", storyId);
        }
    }

    private void CleanAmbienceForRegeneration(long storyId, string folderPath)
    {
        try
        {
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (File.Exists(schemaPath))
            {
                try
                {
                    var json = File.ReadAllText(schemaPath);
                    var root = JsonNode.Parse(json) as JsonObject;
                    if (root != null && (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode)))
                    {
                        if (timelineNode is JsonArray arr)
                        {
                            foreach (var item in arr.OfType<JsonObject>())
                            {
                                // Clean old ambience_file references (legacy)
                                var amb = item.TryGetPropertyValue("ambienceFile", out var a1) ? a1?.ToString() : null;
                                if (!string.IsNullOrWhiteSpace(amb)) DeleteIfExists(Path.Combine(folderPath, amb));
                                amb = item.TryGetPropertyValue("ambience_file", out var a2) ? a2?.ToString() : null;
                                if (!string.IsNullOrWhiteSpace(amb)) DeleteIfExists(Path.Combine(folderPath, amb));
                                
                                // Clean new ambient_sound_file references (snake_case)
                                var asf = item.TryGetPropertyValue("ambient_sound_file", out var a3) ? a3?.ToString() : null;
                                if (!string.IsNullOrWhiteSpace(asf)) DeleteIfExists(Path.Combine(folderPath, asf));

                                item.Remove("ambienceFile");
                                item.Remove("AmbienceFile");
                                item.Remove("ambience_file");
                                item.Remove("ambient_sound_file");
                                item.Remove("ambientSoundFile");
                                item.Remove("AmbientSoundFile");
                                item.Remove("ambient_sound_source_path");
                                item.Remove("ambientSoundSourcePath");
                                item.Remove("AmbientSoundSourcePath");
                                item.Remove("ambient_sound_id");
                                item.Remove("ambientSoundId");
                                item.Remove("AmbientSoundId");
                            }
                        }
                    }

                    if (root != null)
                    {
                        SanitizeTtsSchemaTextFields(root);
                        File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Unable to update schema when cleaning ambient sounds for story {Id}", storyId);
                }
            }

            // delete final mix
            DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
            DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));

            // update flags
            try { _database.UpdateStoryGeneratedAmbient(storyId, false); } catch { }
            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error cleaning ambient sounds for story {Id}", storyId);
        }
    }

    private static readonly string[] FinalMixRootKeys = new[]
    {
        "finalMixFile",
        "finalMix",
        "finalMixMp3",
        "finalMixWav",
        "final_mix",
        "final_mix_file",
        "final_mix_mp3",
        "final_mix_wav",
        "audioMasterFile",
        "audio_master_file",
        "mixedAudioFile"
    };

    private static readonly string[] FinalMixTimelineKeys = new[]
    {
        "finalMixFile",
        "finalMix",
        "finalMixMp3",
        "finalMixWav",
        "mixFile",
        "mix_file",
        "audioMasterFile",
        "audio_master_file"
    };

    private (bool success, string? message) DeleteTtsSchemaAssets(long storyId, string folderPath)
    {
        try
        {
            CleanAllGeneratedAssetsForTtsSchemaRegeneration(storyId, folderPath);
            return (true, "Schema TTS cancellato");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante la cancellazione dello schema TTS per la storia {Id}", storyId);
            return (false, ex.Message);
        }
    }

    private bool RemoveFinalMixReferencesFromSchema(string folderPath)
    {
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");
        if (!File.Exists(schemaPath))
            return false;

        try
        {
            var json = File.ReadAllText(schemaPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null)
                return false;

            var modified = RemovePropertiesCaseInsensitive(root, FinalMixRootKeys);

            if (root.TryGetPropertyValue("timeline", out var timelineNode) || root.TryGetPropertyValue("Timeline", out timelineNode))
            {
                if (timelineNode is JsonArray arr)
                {
                    foreach (var item in arr.OfType<JsonObject>())
                    {
                        if (RemovePropertiesCaseInsensitive(item, FinalMixTimelineKeys))
                        {
                            modified = true;
                        }
                    }
                }
            }

            if (modified)
            {
                SanitizeTtsSchemaTextFields(root);
                File.WriteAllText(schemaPath, root.ToJsonString(SchemaJsonOptions));
            }

            return modified;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unable to remove final mix references from schema for folder {Folder}", folderPath);
            return false;
        }
    }

    private static bool RemovePropertiesCaseInsensitive(JsonObject node, params string[] propertyNames)
    {
        if (node == null || propertyNames == null || propertyNames.Length == 0)
            return false;

        var targets = propertyNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (targets.Count == 0)
            return false;

        var keysToRemove = node
            .Where(kvp => targets.Any(target => string.Equals(kvp.Key, target, StringComparison.OrdinalIgnoreCase)))
            .Select(kvp => kvp.Key)
            .ToList();

        var removed = false;
        foreach (var key in keysToRemove)
        {
            removed |= node.Remove(key);
        }

        return removed;
    }

    private (bool success, string? message) DeleteFinalMixAssets(long storyId, string folderPath)
    {
        try
        {
            RemoveFinalMixReferencesFromSchema(folderPath);

            DeleteIfExists(Path.Combine(folderPath, "final_mix.mp3"));
            DeleteIfExists(Path.Combine(folderPath, "final_mix.wav"));

            try { _database.UpdateStoryGeneratedMixedAudio(storyId, false); } catch { }

            return (true, "Mix finale cancellato");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante la cancellazione del mix finale per la storia {Id}", storyId);
            return (false, ex.Message);
        }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        foreach (var c in invalidChars)
        {
            name = name.Replace(c, '_');
        }
        return name.Trim().Replace(" ", "_").ToLowerInvariant();
    }

    /// <summary>
    /// Prefix all existing story folders with the story id (if not already prefixed),
    /// renaming directories on disk and updating the DB `Folder` field.
    /// Returns the number of folders renamed.
    /// </summary>
    public async Task<int> PrefixAllStoryFoldersWithIdAsync()
    {
        var baseFolder = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder");
        Directory.CreateDirectory(baseFolder);

        var stories = _database.GetAllStories() ?? new List<StoryRecord>();
        int updated = 0;

        foreach (var story in stories)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(story.Folder))
                    continue;

                var currentFolder = story.Folder;
                // Normalize various existing prefixes to the new 5-digit padded format.
                var idPrefixNumeric = $"{story.Id}_"; // e.g., "3_foo"
                var idPrefixStory = $"story_{story.Id}_"; // e.g., "story_3_foo"
                var idPrefixPadded = $"{story.Id.ToString("D5")}_"; // e.g., "00003_foo"

                string targetFolderName;

                if (currentFolder.StartsWith(idPrefixPadded, StringComparison.OrdinalIgnoreCase))
                {
                    // already in desired format
                    continue;
                }
                else if (currentFolder.StartsWith(idPrefixNumeric, StringComparison.OrdinalIgnoreCase))
                {
                    // strip numeric prefix and re-prefix with padded id
                    var rest = currentFolder.Substring(idPrefixNumeric.Length);
                    targetFolderName = SanitizeFolderName($"{story.Id.ToString("D5")}_{rest}");
                }
                else if (currentFolder.StartsWith(idPrefixStory, StringComparison.OrdinalIgnoreCase))
                {
                    var rest = currentFolder.Substring(idPrefixStory.Length);
                    targetFolderName = SanitizeFolderName($"{story.Id.ToString("D5")}_{rest}");
                }
                else
                {
                    // no recognized prefix, just add padded id before existing name
                    targetFolderName = SanitizeFolderName($"{story.Id.ToString("D5")}_{currentFolder}");
                }

                var oldPath = Path.Combine(baseFolder, currentFolder);
                if (!Directory.Exists(oldPath))
                {
                    _database.UpdateStoryFolder(story.Id, targetFolderName);
                    updated++;
                    continue;
                }

                var newPath = Path.Combine(baseFolder, targetFolderName);

                // If target exists, append a suffix to avoid conflicts
                if (Directory.Exists(newPath))
                {
                    newPath = Path.Combine(baseFolder, SanitizeFolderName($"{targetFolderName}_{DateTime.UtcNow:yyyyMMddHHmmss}"));
                }

                Directory.Move(oldPath, newPath);
                _database.UpdateStoryFolder(story.Id, Path.GetFileName(newPath));
                updated++;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to rename folder for story {Id}", story.Id);
            }
        }

        await Task.CompletedTask;
        return updated;
    }

    private static IEnumerable<string>? ParseAgentSkills(Agent agent)
    {
        if (agent == null || string.IsNullOrWhiteSpace(agent.Skills))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(agent.Skills);
            if (parsed == null || parsed.Count == 0)
                return null;
            return parsed.Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s));
        }
        catch
        {
            return null;
        }
    }

    private string? ComposeSystemMessage(Agent agent)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.UserPrompt))
            parts.Add(agent.UserPrompt);

        if (!string.IsNullOrWhiteSpace(agent.ExecutionPlan))
        {
            var plan = LoadExecutionPlan(agent.ExecutionPlan);
            if (!string.IsNullOrWhiteSpace(plan))
                parts.Add(plan);
        }

        if (!string.IsNullOrWhiteSpace(agent.SystemPrompt))
            parts.Add(agent.SystemPrompt);

        if (parts.Count == 0)
        {
            return "You are a TTS schema generator. Use only the available tools to build the narrator and characters timeline and confirm at the end.";
        }

        return string.Join("\n\n", parts);
    }

    private string BuildTtsJsonPrompt(StoryRecord story)
    {
        // For the TTS JSON generator we now do NOT pass the story text inline.
        // Instead, the model must use the read_story_part function to retrieve segments.
        // This allows the model to process large stories in chunks and reduces token overhead.
        var builder = new StringBuilder();
        builder.AppendLine("You are generating a TTS schema (timeline of narration and character dialogue) for a story.");
        builder.AppendLine("Follow the agent instructions. Use read_story_part to fetch the story (part_index from 0 upward until is_last=true) and build the schema with add_narration / add_phrase, then call confirm once finished.");
        builder.AppendLine("Reproduce the text faithfully and assign each sentence to narration or a character; do not invent content.");
        return builder.ToString();
    }

    private string BuildStoryEvaluationPrompt(StoryRecord story, bool isCoherenceEvaluation = false)
    {
        var builder = new StringBuilder();
        
        if (isCoherenceEvaluation)
        {
            // Prompt for chunk-based coherence evaluation
            builder.AppendLine("You are analyzing a story's coherence using chunk-based fact extraction and comparison.");
            builder.AppendLine("You must NOT copy the full story text into this prompt.");
            builder.AppendLine("Follow this workflow for each chunk:");
            builder.AppendLine("1. Use read_story_part(part_index=N) to read the chunk (start with 0)");
            builder.AppendLine("2. Extract objective facts and save with extract_chunk_facts");
            builder.AppendLine("3. Retrieve previous facts with get_chunk_facts and get_all_previous_facts");
            builder.AppendLine("4. Compare facts and calculate coherence scores with calculate_coherence");
            builder.AppendLine("5. Continue until you receive \"is_last\": true");
            builder.AppendLine("When all chunks are processed, call finalize_global_coherence to save the final score.");
            builder.AppendLine("Be thorough and document all inconsistencies found.");
        }
        else
        {
            // Prompt for standard evaluation with EvaluatorTool
            builder.AppendLine("You are evaluating a story and must record your judgement using the available tools.");
            builder.AppendLine("You must NOT copy the full story text into this prompt.");
            builder.AppendLine("Instead, use the read_story_part function to retrieve segments of the story (start with part_index=0 and continue requesting parts until you receive \"is_last\": true).");
            builder.AppendLine("Do not invent story content; rely only on the chunks returned by read_story_part.");
            builder.AppendLine("After you have reviewed the necessary sections, call the evaluate_full_story function exactly once. The story_id is managed internally; do not pass it.");
            builder.AppendLine("If you finish your review but fail to call evaluate_full_story, the orchestrator will remind you and ask again up to 3 times - you MUST call the function before the evaluation completes.");
            builder.AppendLine("Populate the following scores (0-10): narrative_coherence_score, originality_score, emotional_impact_score, action_score.");
            builder.AppendLine("All score fields MUST be integers between 0 and 10 (use 0 if you cannot determine a score). Do NOT send strings like \"None\".");
            builder.AppendLine("Also include the corresponding *_defects values (empty string or \"None\" is acceptable if there are no defects).");
            builder.AppendLine("Do not return an overall evaluation text - the system will compute the aggregate score automatically.");
            builder.AppendLine("Ensure every score and defect field is present in the final tool call, even if the defect description is empty.");
        }
        
        return builder.ToString();
    }

    private string BuildActionPacingPrompt(StoryRecord story)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are evaluating ACTION and PACING of a story chunk-by-chunk using the available tools.");
        builder.AppendLine("Workflow:");
        builder.AppendLine("1) Read chunk with read_story_part(part_index=N) starting from 0 and increment until is_last=true (do NOT invent chunks).");
        builder.AppendLine("2) Extract action/pacing profile with extract_action_profile(chunk_number=N, chunk_text=returned content).");
        builder.AppendLine("3) Compute scores (0.0-1.0):");
        builder.AppendLine("   - action_score: quantity/quality of action");
        builder.AppendLine("   - pacing_score: narrative rhythm of the chunk");
        builder.AppendLine("4) Record scores with calculate_action_pacing(chunk_number=N, action_score=..., pacing_score=..., notes=\"short notes\").");
        builder.AppendLine("5) Repeat until you receive is_last=true.");
        builder.AppendLine("6) Finalize with finalize_global_pacing(pacing_score=..., notes=\"brief summary\").");
        builder.AppendLine();
        builder.AppendLine("Important:");
        builder.AppendLine("- Always use the tools; do not answer in free text unless summarizing.");
        builder.AppendLine("- Do not skip finalize_global_pacing at the end.");
        builder.AppendLine();
        builder.AppendLine("Story metadata:");
        builder.AppendLine($"ID: {story.Id}");
        builder.AppendLine($"Prompt: {story.Prompt}");
        return builder.ToString();
    }

    private string? LoadExecutionPlan(string? planName)
    {
        if (string.IsNullOrWhiteSpace(planName))
            return null;

        try
        {
            var planPath = Path.Combine(Directory.GetCurrentDirectory(), "execution_plans", planName);
            if (File.Exists(planPath))
            {
                return File.ReadAllText(planPath);
            }
        }
        catch { }

        return null;
    }

    private IStoryCommand? CreateCommandForStatus(StoryStatus status)
    {
        return _mainCommands.CreateCommandForStatus(status);
    }

    private string? GetOperationNameForStatus(StoryStatus status)
    {
        return _mainCommands.GetOperationNameForStatus(status);
    }

    private void TryPopulateAgentMetadata(StoryStatus status, Dictionary<string, string> metadata)
    {
        if (status == null || metadata == null)
            return;

        string? role = null;
        if (string.Equals(status.OperationType, "agent_call", StringComparison.OrdinalIgnoreCase))
        {
            role = status.AgentType;
        }
        else if (string.Equals(status.OperationType, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            var fn = status.FunctionName?.ToLowerInvariant();
            role = fn switch
            {
                "add_voice_tags_to_story" => "formatter",
                "add_ambient_tags_to_story" => "ambient_expert",
                "add_fx_tags_to_story" => "fx_expert",
                "add_music_tags_to_story" => "music_expert",
                _ => null
            };
        }

        if (string.IsNullOrWhiteSpace(role))
            return;

        try
        {
            var agent = _database.ListAgents()
                .FirstOrDefault(a => a.IsActive && string.Equals(a.Role, role, StringComparison.OrdinalIgnoreCase));
            if (agent == null)
                return;

            if (!metadata.ContainsKey("agentName"))
                metadata["agentName"] = agent.Description ?? role;
            if (!metadata.ContainsKey("agentRole"))
                metadata["agentRole"] = agent.Role ?? role;

            if (agent.ModelId.HasValue && !metadata.ContainsKey("modelName"))
            {
                var model = _database.GetModelInfoById(agent.ModelId.Value);
                if (!string.IsNullOrWhiteSpace(model?.Name))
                    metadata["modelName"] = model.Name!;
            }
        }
        catch
        {
            // best-effort metadata only
        }
    }

    internal interface IStoryCommand : TinyGenerator.Services.Commands.ICommand
    {
        bool RequireStoryText { get; }
        bool EnsureFolder { get; }
        bool HandlesStatusTransition { get; }
        Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context);
    }

    internal sealed record StoryCommandContext(StoryRecord Story, string FolderPath, StoryStatus? TargetStatus, CancellationToken CancellationToken = default);

    private static readonly System.Threading.AsyncLocal<string?> _dispatcherRunId = new();
    private static readonly System.Threading.AsyncLocal<CancellationToken?> _dispatcherCancellation = new();

    internal string? CurrentDispatcherRunId => _dispatcherRunId.Value;
    internal CancellationToken? CurrentDispatcherCancellationToken => _dispatcherCancellation.Value;

    private sealed class DispatcherRunScope : IDisposable
    {
        private readonly string? _previous;
        private readonly CancellationToken? _previousToken;
        private bool _disposed;

        public DispatcherRunScope(string? runId, CancellationToken? cancellationToken = null)
        {
            _previous = _dispatcherRunId.Value;
            _previousToken = _dispatcherCancellation.Value;
            _dispatcherRunId.Value = runId;
            _dispatcherCancellation.Value = cancellationToken;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _dispatcherRunId.Value = _previous;
            _dispatcherCancellation.Value = _previousToken;
        }
    }

    private IDisposable BeginDispatcherRunScope(string? runId, CancellationToken? cancellationToken = null)
        => new DispatcherRunScope(runId, cancellationToken);

    // GenerateTtsSchemaCommand moved to Services/Commands/GenerateTtsSchemaCommand.cs

    private void AssignMusicFilesFromLibrary(JsonObject rootNode, StoryRecord story, string folderPath)
    {
        if (rootNode == null) return;
        if (story == null) return;
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        // Get timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
            return;

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0) return;

        var musicLibraryFolder = GetMusicLibraryFolderForStory(story);
        if (string.IsNullOrWhiteSpace(musicLibraryFolder) || !Directory.Exists(musicLibraryFolder))
            return;

        var libraryFiles = Directory
            .GetFiles(musicLibraryFolder)
            .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (libraryFiles.Count == 0) return;

        var index = BuildMusicLibraryIndex(libraryFiles);
        var usedDestFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in phraseEntries)
        {
            var existing = ReadString(entry, "music_file") ?? ReadString(entry, "musicFile") ?? ReadString(entry, "MusicFile");
            if (!string.IsNullOrWhiteSpace(existing)) usedDestFileNames.Add(existing);
        }

        for (var i = 0; i < phraseEntries.Count; i++)
        {
            var entry = phraseEntries[i];
            var musicDesc = ReadString(entry, "music_description") ?? ReadString(entry, "musicDescription") ?? ReadString(entry, "MusicDescription");
            if (string.IsNullOrWhiteSpace(musicDesc))
                continue;

            if (string.Equals(musicDesc.Trim(), "silence", StringComparison.OrdinalIgnoreCase))
            {
                entry["music_file"] = null;
                entry["musicFile"] = null;
                continue;
            }

            var currentMusicFile = ReadString(entry, "music_file") ?? ReadString(entry, "musicFile") ?? ReadString(entry, "MusicFile");
            if (!string.IsNullOrWhiteSpace(currentMusicFile) && File.Exists(Path.Combine(folderPath, currentMusicFile)))
                continue;

            var requestedType = InferMusicTypeFromDescription(musicDesc, index.Keys);
            if (string.Equals(requestedType, "silence", StringComparison.OrdinalIgnoreCase))
            {
                entry["music_file"] = null;
                entry["musicFile"] = null;
                continue;
            }

            var selected = SelectMusicFileDeterministic(index, requestedType, usedDestFileNames, seedA: story.Id, seedB: i);
            if (string.IsNullOrWhiteSpace(selected) || !File.Exists(selected))
                continue;

            var destFileName = Path.GetFileName(selected);
            if (string.IsNullOrWhiteSpace(destFileName))
                continue;

            var destPath = Path.Combine(folderPath, destFileName);
            try
            {
                if (!File.Exists(destPath))
                    File.Copy(selected, destPath, overwrite: false);
            }
            catch
            {
                continue;
            }

            entry["music_file"] = destFileName;
            entry["musicFile"] = destFileName;
            usedDestFileNames.Add(destFileName);
        }
    }

    // NormalizeCharacterNamesCommand moved to Services/Commands/NormalizeCharacterNamesCommand.cs

    // NormalizeSentimentsCommand moved to Services/Commands/NormalizeSentimentsCommand.cs

    // AssignVoicesCommand moved to Services/Commands/AssignVoicesCommand.cs

    private async Task<bool> ApplyVoiceAssignmentFallbacksAsync(string schemaPath)
    {
        try
        {
            if (!File.Exists(schemaPath)) return false;
            var content = await File.ReadAllTextAsync(schemaPath);
            var schema = JsonSerializer.Deserialize<TtsSchema>(content, SchemaJsonOptions);
            if (schema?.Characters == null || schema.Characters.Count == 0)
                return false;

            var catalogVoices = _database.ListTtsVoices(onlyEnabled: true);
            if (catalogVoices == null || catalogVoices.Count == 0)
                return false;

            var updated = false;
            var usedVoiceIds = new HashSet<string>(
                schema.Characters
                    .Where(c => !string.IsNullOrWhiteSpace(c.VoiceId))
                    .Select(c => c.VoiceId!),
                StringComparer.OrdinalIgnoreCase);

            var narrator = schema.Characters.FirstOrDefault(c => string.Equals(c.Name, "narratore", StringComparison.OrdinalIgnoreCase));
            if (narrator != null && string.IsNullOrWhiteSpace(narrator.VoiceId))
            {
                var defaultVoiceId = GetNarratorDefaultVoiceId();
                var narratorVoice = !string.IsNullOrWhiteSpace(defaultVoiceId)
                    ? FindVoiceByNameOrId(catalogVoices, defaultVoiceId)
                    : null;
                if (narratorVoice != null)
                {
                    ApplyVoiceToCharacter(narrator, narratorVoice);
                    usedVoiceIds.Add(narratorVoice.VoiceId);
                    updated = true;
                }
                else
                {
                    _logger?.LogWarning("Voce narratore di default non trovata nella tabella tts_voices");
                }
            }

            foreach (var character in schema.Characters)
            {
                if (!string.IsNullOrWhiteSpace(character.VoiceId))
                    continue;
                if (string.IsNullOrWhiteSpace(character.Gender))
                    continue;

                var fallbackVoice = PickVoiceForGender(character.Gender, catalogVoices, usedVoiceIds);
                if (fallbackVoice != null)
                {
                    ApplyVoiceToCharacter(character, fallbackVoice);
                    usedVoiceIds.Add(fallbackVoice.VoiceId);
                    updated = true;
                }
            }

            if (updated)
            {
                var normalized = JsonSerializer.Serialize(schema, SchemaJsonOptions);
                await SaveSanitizedTtsSchemaJsonAsync(schemaPath, normalized);
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore durante ApplyVoiceAssignmentFallbacksAsync per schema {SchemaPath}", schemaPath);
            return false;
        }
    }

    // Delete* + cascading cleanup commands moved to Services/Commands/*.cs


    private static void ApplyVoiceToCharacter(TtsCharacter character, TinyGenerator.Models.TtsVoice voice)
    {
        character.Voice = voice.Name;
        character.VoiceId = voice.VoiceId;
        if (string.IsNullOrWhiteSpace(character.Gender) && !string.IsNullOrWhiteSpace(voice.Gender))
        {
            character.Gender = voice.Gender;
        }
    }

    private TinyGenerator.Models.TtsVoice? FindVoiceByNameOrId(List<TinyGenerator.Models.TtsVoice> voices, string preferredName)
    {
        if (voices == null || voices.Count == 0) return null;
        var byName = voices.FirstOrDefault(v => string.Equals(v.Name, preferredName, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;

        return voices.FirstOrDefault(v =>
            !string.IsNullOrWhiteSpace(v.VoiceId) &&
            v.VoiceId.IndexOf(preferredName, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private string? GetNarratorDefaultVoiceId()
    {
        var value = _narratorVoiceOptions?.CurrentValue?.DefaultVoiceId;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private TinyGenerator.Models.TtsVoice? PickVoiceForGender(string gender, List<TinyGenerator.Models.TtsVoice> voices, HashSet<string> usedVoiceIds)
    {
        if (string.IsNullOrWhiteSpace(gender) || voices == null || voices.Count == 0)
            return null;

        var matches = voices
            .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId) &&
                        string.Equals(v.Gender ?? string.Empty, gender, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return null;

        var unused = matches
            .Where(v => !usedVoiceIds.Contains(v.VoiceId!))
            .ToList();

        var pool = unused.Count > 0 ? unused : matches;
        var maxScore = pool.Max(v => v.Score ?? 0);
        var bestCandidates = pool
            .Where(v => Math.Abs((v.Score ?? 0) - maxScore) < 0.0001)
            .ToList();
        if (bestCandidates.Count == 0)
        {
            bestCandidates = pool;
        }

        var index = Random.Shared.Next(bestCandidates.Count);
        return bestCandidates[index];
    }

    private async Task<(bool success, string? message)> StartTtsAudioGenerationAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = $"ttsaudio_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        _customLogger?.Append(runId, $"[{storyId}] Avvio generazione tracce audio nella cartella {context.FolderPath}");

        try
        {
            var (success, message) = await GenerateTtsAudioInternalAsync(context, runId);

            if (success)
            {
                try
                {
                    var story = GetStoryById(storyId);
                    if (story != null)
                    {
                        var allowNext = ApplyStatusTransitionWithCleanup(story, "tts_generated", runId);
                        if (!allowNext)
                        {
                            _customLogger?.Append(runId, $"[{storyId}] delete_next_items attivo: cleanup dopo tts_generated applicato", "info");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Impossibile aggiornare lo stato della storia {Id}", storyId);
                    _customLogger?.Append(runId, $"[{storyId}] Aggiornamento stato fallito: {ex.Message}");
                }
            }

            await (_customLogger?.MarkCompletedAsync(runId, message ?? (success ? "Generazione audio completata" : "Errore generazione audio"))
                ?? Task.CompletedTask);

            return (success, message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore non gestito durante la generazione audio TTS per la storia {Id}", storyId);
            _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
            await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            return (false, $"Errore inatteso: {ex.Message}");
        }
    }

    private async Task<(bool success, string? message)> GenerateTtsAudioInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");

        var ttsPrecheck = await EnsureTtsReadyBeforeGenerationAsync(story.Id, runId);
        if (!ttsPrecheck.success)
        {
            return ttsPrecheck;
        }

        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Try both casing variants for characters
        if (!rootNode.TryGetPropertyValue("characters", out var charactersNode))
            rootNode.TryGetPropertyValue("Characters", out charactersNode);
        if (charactersNode is not JsonArray charactersArray)
        {
            var err = "Lista di personaggi mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Try both casing variants for timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Remove phrase entries that contain only placeholder/empty TTS text (e.g., "...")
        PruneEmptyTextPhrases(timelineArray);

        var characters = BuildCharacterMap(charactersArray);
        if (characters.Count == 0)
        {
            var err = "Nessun personaggio definito nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (characters.Values.All(c => string.IsNullOrWhiteSpace(c.VoiceId)))
        {
            var err = "Nessun personaggio ha una voce assegnata: eseguire prima l'assegnazione delle voci";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi da sintetizzare";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        Directory.CreateDirectory(folderPath);

        int phraseCounter = 0;
        int fileCounter = 1;
        int currentMs = 0;
        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in timelineArray.OfType<JsonObject>())
        {
            if (IsPauseEntry(entry, out var pauseMs))
            {
                currentMs += pauseMs;
                continue;
            }

                if (!TryReadPhrase(entry, out var characterName, out var text, out var emotion))
                    continue;

            phraseCounter++;

            if (!characters.TryGetValue(characterName, out var character) || string.IsNullOrWhiteSpace(character.VoiceId))
            {
                var err = $"Il personaggio '{characterName}' non ha una voce assegnata";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }

            byte[] audioBytes;
            int? durationFromResult;
            string cleanText = string.Empty;
            try
            {
                cleanText = CleanTtsText(text);
                if (string.IsNullOrWhiteSpace(cleanText))
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Salto frase vuota dopo pulizia (character={characterName})");
                    continue;
                }

                var reuse = TryReuseExistingAudioEntry(entry, folderPath, cleanText, character.VoiceId!, characterName, emotion, runId, story.Id, usedFiles);
                if (reuse.reused && !string.IsNullOrWhiteSpace(reuse.fileName))
                {
                    var reuseStartMs = currentMs;
                    var reuseEndMs = reuseStartMs + reuse.durationMs;
                    entry["fileName"] = reuse.fileName;
                    entry["durationMs"] = reuse.durationMs;
                    entry["startMs"] = reuseStartMs;
                    entry["endMs"] = reuseEndMs;
                    currentMs = reuseEndMs + GetPhraseGapMs(cleanText);
                    continue;
                }

                var fileName = BuildAudioFileName(fileCounter++, characterName, usedFiles);
                var filePath = Path.Combine(folderPath, fileName);

                _customLogger?.Append(runId, $"[{story.Id}] Generazione frase {phraseCounter}/{phraseEntries.Count} ({characterName}) -> {fileName}");

                (audioBytes, durationFromResult) = await GenerateAudioBytesAsync(character.VoiceId!, cleanText, emotion);

                await File.WriteAllBytesAsync(filePath, audioBytes);
                
                // Save TTS text to .txt file with same name
                var txtFilePath = Path.ChangeExtension(filePath, ".txt");
                await File.WriteAllTextAsync(txtFilePath, $"voiceId={character.VoiceId}\n{cleanText}");

                var durationMs = durationFromResult ?? TryGetWavDuration(audioBytes) ?? 0;
                var startMs = currentMs;
                var endMs = durationMs > 0 ? startMs + durationMs : startMs;

                entry["fileName"] = fileName;
                entry["durationMs"] = durationMs;
                entry["startMs"] = startMs;
                entry["endMs"] = endMs;

                currentMs = endMs + GetPhraseGapMs(cleanText);
                _customLogger?.Append(runId, $"[{story.Id}] Frase completata: {fileName} ({durationMs} ms)");
            }
            catch (Exception ex)
            {
                var err = $"Errore durante la sintesi della frase '{characterName}': {ex.Message}";
                _logger?.LogError(ex, "Errore TTS per la storia {Id}", story.Id);
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        try
        {
            SanitizeTtsSchemaTextFields(rootNode);
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
            DeleteOrphanTtsAudioFiles(folderPath, timelineArray, usedFiles);
            // Mark story as having generated TTS audio (best-effort)
            try
            {
                _database.UpdateStoryGeneratedTts(story.Id, true);
            }
            catch { }
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = $"Generazione audio completata ({phraseCounter} frasi)";
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    /// <summary>
    /// Generates TTS audio synchronously and reports progress to the CommandDispatcher.
    /// </summary>
    private async Task<(bool success, string? message)> GenerateTtsAudioWithProgressAsync(
        StoryCommandContext context,
        string dispatcherRunId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");

        _customLogger?.Append(dispatcherRunId, $"[{story.Id}] Avvio generazione tracce audio nella cartella {folderPath}");

        var ttsPrecheck = await EnsureTtsReadyBeforeGenerationAsync(story.Id, dispatcherRunId);
        if (!ttsPrecheck.success)
        {
            return ttsPrecheck;
        }

        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Try both casings for characters and timeline
        if (!rootNode.TryGetPropertyValue("characters", out var charactersNode))
            rootNode.TryGetPropertyValue("Characters", out charactersNode);
        if (charactersNode is not JsonArray charactersArray)
        {
            var err = "Lista di personaggi mancante nello schema TTS";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Remove phrase entries that contain only placeholder/empty TTS text (e.g., "...")
        PruneEmptyTextPhrases(timelineArray);

        var characters = BuildCharacterMap(charactersArray);
        if (characters.Count == 0)
        {
            var err = "Nessun personaggio definito nello schema TTS";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (characters.Values.All(c => string.IsNullOrWhiteSpace(c.VoiceId)))
        {
            var err = "Nessun personaggio ha una voce assegnata: eseguire prima l'assegnazione delle voci";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi da sintetizzare";
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        Directory.CreateDirectory(folderPath);

        int phraseCounter = 0;
        int fileCounter = 1;
        int currentMs = 0;
        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Set initial progress (0/total)
        _commandDispatcher?.UpdateStep(dispatcherRunId, 0, phraseEntries.Count);

        foreach (var entry in timelineArray.OfType<JsonObject>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPauseEntry(entry, out var pauseMs))
            {
                currentMs += pauseMs;
                continue;
            }

            if (!TryReadPhrase(entry, out var characterName, out var text, out var emotion))
                continue;

            phraseCounter++;

            // Update progress in CommandDispatcher
            _commandDispatcher?.UpdateStep(dispatcherRunId, phraseCounter, phraseEntries.Count);

            if (!characters.TryGetValue(characterName, out var character) || string.IsNullOrWhiteSpace(character.VoiceId))
            {
                var err = $"Il personaggio '{characterName}' non ha una voce assegnata";
                _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
                return (false, err);
            }

            byte[] audioBytes;
            int? durationFromResult;
            string cleanText = string.Empty;
            try
            {
                cleanText = CleanTtsText(text);
                if (string.IsNullOrWhiteSpace(cleanText))
                {
                    _customLogger?.Append(dispatcherRunId, $"[{story.Id}] Salto frase vuota dopo pulizia (character={characterName})");
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var reuse = TryReuseExistingAudioEntry(entry, folderPath, cleanText, character.VoiceId!, characterName, emotion, dispatcherRunId, story.Id, usedFiles);
                if (reuse.reused && !string.IsNullOrWhiteSpace(reuse.fileName))
                {
                    var reuseStartMs = currentMs;
                    var reuseEndMs = reuseStartMs + reuse.durationMs;
                    entry["fileName"] = reuse.fileName;
                    entry["durationMs"] = reuse.durationMs;
                    entry["startMs"] = reuseStartMs;
                    entry["endMs"] = reuseEndMs;
                    currentMs = reuseEndMs + GetPhraseGapMs(cleanText);
                    continue;
                }

                var fileName = BuildAudioFileName(fileCounter++, characterName, usedFiles);
                var filePath = Path.Combine(folderPath, fileName);

                _customLogger?.Append(dispatcherRunId, $"[{story.Id}] Generazione frase {phraseCounter}/{phraseEntries.Count} ({characterName}) -> {fileName}");

                cancellationToken.ThrowIfCancellationRequested();
                (audioBytes, durationFromResult) = await GenerateAudioBytesAsync(character.VoiceId!, cleanText, emotion);

                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(filePath, audioBytes);
                
                var txtFilePath = Path.ChangeExtension(filePath, ".txt");
                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(txtFilePath, $"voiceId={character.VoiceId}\n{cleanText}");

                var durationMs = durationFromResult ?? TryGetWavDuration(audioBytes) ?? 0;
                var startMs = currentMs;
                var endMs = currentMs + durationMs;
                currentMs = endMs + GetPhraseGapMs(cleanText);

                entry["fileName"] = fileName;
                entry["durationMs"] = durationMs;
                entry["startMs"] = startMs;
                entry["endMs"] = endMs;

                _customLogger?.Append(dispatcherRunId, $"[{story.Id}] Frase completata: {fileName} ({durationMs} ms)");
            }
            catch (Exception ex)
            {
                var err = $"Errore durante la sintesi della frase '{characterName}': {ex.Message}";
                _logger?.LogError(ex, "Errore TTS per la storia {Id}", story.Id);
                _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SanitizeTtsSchemaTextFields(rootNode);
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
            DeleteOrphanTtsAudioFiles(folderPath, timelineArray, usedFiles);
            // Mark story as having generated TTS audio (best-effort)
            try
            {
                _database.UpdateStoryGeneratedTts(story.Id, true);
            }
            catch { }
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = $"Generazione audio completata ({phraseCounter} frasi)";
        _customLogger?.Append(dispatcherRunId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    private (bool reused, string? fileName, int durationMs) TryReuseExistingAudioEntry(
        JsonObject entry,
        string folderPath,
        string cleanText,
        string voiceId,
        string characterName,
        string emotion,
        string runId,
        long storyId,
        HashSet<string> usedFiles)
    {
        var existingFileName = ReadString(entry, "fileName") ??
                               ReadString(entry, "FileName") ??
                               ReadString(entry, "file_name");
        if (!string.IsNullOrWhiteSpace(existingFileName))
        {
            var filePath = Path.Combine(folderPath, existingFileName);
            var txtPath = Path.ChangeExtension(filePath, ".txt");
            if (File.Exists(filePath) && File.Exists(txtPath))
            {
                try
                {
                    var lines = File.ReadAllLines(txtPath);
                    if (lines.Length >= 2)
                    {
                        if (TrySidecarMatch(lines, cleanText, voiceId, characterName, emotion))
                        {
                            var duration = ResolveAudioDurationMs(filePath);
                            usedFiles.Add(existingFileName);
                            _customLogger?.Append(runId, $"[{storyId}] Riutilizzo file audio esistente {existingFileName} (voiceId+testo invariati).");
                            return (true, existingFileName, duration);
                        }
                    }
                }
                catch
                {
                    // Ignore malformed sidecar and continue with broader cache lookup.
                }
            }
        }

        var fromFolder = TryFindReusableAudioInFolder(
            folderPath,
            cleanText,
            voiceId,
            characterName,
            emotion,
            usedFiles);
        if (fromFolder.reused && !string.IsNullOrWhiteSpace(fromFolder.fileName))
        {
            _customLogger?.Append(runId, $"[{storyId}] Riutilizzo da cache cartella: {fromFolder.fileName} (voiceId+testo invariati).");
            return fromFolder;
        }

        return (false, null, 0);
    }

    private static bool TryParseTtsSidecarVoiceId(string headerLine, out string voiceId)
    {
        voiceId = string.Empty;
        if (string.IsNullOrWhiteSpace(headerLine))
            return false;

        var trimmed = headerLine.Trim();
        const string prefix = "voiceId=";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        voiceId = trimmed[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(voiceId);
    }

    private static bool TrySidecarMatch(
        string[] lines,
        string cleanText,
        string voiceId,
        string characterName,
        string emotion)
    {
        if (lines == null || lines.Length < 2)
            return false;

        if (TryParseTtsSidecarVoiceId(lines[0], out var recordedVoiceId))
        {
            if (!string.Equals(recordedVoiceId, voiceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else
        {
            // Backward compatibility for old sidecar format: [Character, Emotion]
            if (!TryParseTtsSidecarHeader(lines[0], out var recordedCharacter, out var recordedEmotion))
            {
                return false;
            }

            if (!string.Equals(recordedCharacter, characterName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalizedRecordedEmotion = NormalizeEmotionForTts(recordedEmotion);
            var normalizedTargetEmotion = NormalizeEmotionForTts(emotion);
            if (!string.Equals(normalizedRecordedEmotion, normalizedTargetEmotion, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var recordedText = string.Join("\n", lines.Skip(1));
        return string.Equals(NormalizeTextForComparison(recordedText), NormalizeTextForComparison(cleanText), StringComparison.Ordinal);
    }

    private (bool reused, string? fileName, int durationMs) TryFindReusableAudioInFolder(
        string folderPath,
        string cleanText,
        string voiceId,
        string characterName,
        string emotion,
        HashSet<string> usedFiles)
    {
        try
        {
            if (!Directory.Exists(folderPath))
                return (false, null, 0);

            var sidecars = Directory.EnumerateFiles(folderPath, "*.txt", SearchOption.TopDirectoryOnly);
            foreach (var txtPath in sidecars)
            {
                var baseName = Path.GetFileNameWithoutExtension(txtPath);
                var wavPath = Path.Combine(folderPath, baseName + ".wav");
                var mp3Path = Path.Combine(folderPath, baseName + ".mp3");
                var audioPath = File.Exists(wavPath) ? wavPath : (File.Exists(mp3Path) ? mp3Path : null);
                if (string.IsNullOrWhiteSpace(audioPath))
                    continue;

                var audioFileName = Path.GetFileName(audioPath);
                if (string.IsNullOrWhiteSpace(audioFileName) || usedFiles.Contains(audioFileName))
                    continue;

                string[] lines;
                try
                {
                    lines = File.ReadAllLines(txtPath);
                }
                catch
                {
                    continue;
                }

                if (!TrySidecarMatch(lines, cleanText, voiceId, characterName, emotion))
                    continue;

                usedFiles.Add(audioFileName);
                var duration = ResolveAudioDurationMs(audioPath);
                return (true, audioFileName, duration);
            }
        }
        catch
        {
            // Best-effort cache lookup
        }

        return (false, null, 0);
    }

    private int ResolveAudioDurationMs(string filePath)
    {
        try
        {
            var ext = Path.GetExtension(filePath);
            if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
            {
                return TryGetWavDurationFromFile(filePath) ?? 0;
            }
        }
        catch
        {
            // Best-effort duration extraction
        }

        return 0;
    }

    private void DeleteOrphanTtsAudioFiles(string folderPath, JsonArray timelineArray, HashSet<string> usedFiles)
    {
        if (!Directory.Exists(folderPath) || timelineArray == null)
            return;

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in timelineArray.OfType<JsonObject>())
        {
            var fn = ReadString(item, "fileName") ?? ReadString(item, "FileName") ?? ReadString(item, "file_name");
            if (!string.IsNullOrWhiteSpace(fn))
            {
                referenced.Add(fn.Trim());
            }
        }

        foreach (var u in usedFiles)
        {
            if (!string.IsNullOrWhiteSpace(u))
            {
                referenced.Add(u.Trim());
            }
        }

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    var ext = Path.GetExtension(path);
                    if (!string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    var name = Path.GetFileName(path);
                    return name.StartsWith("tts_", StringComparison.OrdinalIgnoreCase) ||
                           (name.Length > 4 && char.IsDigit(name[0]) && char.IsDigit(name[1]) && char.IsDigit(name[2]) && name[3] == '_');
                });
        }
        catch
        {
            return;
        }

        foreach (var audioPath in candidates)
        {
            var fileName = Path.GetFileName(audioPath);
            if (referenced.Contains(fileName))
                continue;

            TryDeleteFile(audioPath);
            TryDeleteFile(Path.ChangeExtension(audioPath, ".txt"));
        }
    }

    private static bool TryParseTtsSidecarHeader(string headerLine, out string character, out string emotion)
    {
        character = string.Empty;
        emotion = string.Empty;
        if (string.IsNullOrWhiteSpace(headerLine))
            return false;

        var trimmed = headerLine.Trim();
        if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
        {
            trimmed = trimmed[1..^1];
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        character = parts[0].Trim();
        emotion = parts[1].Trim();
        return true;
    }

    private static string NormalizeTextForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
    }

    private int? TryGetWavDurationFromFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var bytes = File.ReadAllBytes(filePath);
            return TryGetWavDuration(bytes);
        }
        catch
        {
            return null;
        }
    }

    private int? TryGetAudioDurationMsFromFile(string filePath)
    {
        try
        {
            var wavDuration = TryGetWavDurationFromFile(filePath);
            if (wavDuration.HasValue && wavDuration.Value > 0)
            {
                return wavDuration.Value;
            }

            if (!File.Exists(filePath))
            {
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(true); } catch { }
                return null;
            }

            var output = stdoutTask.GetAwaiter().GetResult()?.Trim();
            _ = stderrTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            if (double.TryParse(output, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                return (int)Math.Round(seconds * 1000.0);
            }
        }
        catch
        {
            // best effort
        }

        try
        {
            // Fallback when ffprobe is unavailable: parse "Duration: HH:MM:SS.xx" from ffmpeg stderr.
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{filePath}\" -f null -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(7000))
            {
                try { proc.Kill(true); } catch { }
                return null;
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                var match = Regex.Match(stderr, @"Duration:\s*(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.IgnoreCase);
                if (match.Success &&
                    int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh) &&
                    int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm) &&
                    double.TryParse(match.Groups[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var ss))
                {
                    var totalMs = (int)Math.Round(((hh * 3600) + (mm * 60) + ss) * 1000.0);
                    if (totalMs > 0)
                    {
                        return totalMs;
                    }
                }
            }
        }
        catch
        {
            // best effort
        }

        return null;
    }

    internal static bool IsBatchWorkerProcess()
        => Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--batch-worker", StringComparison.OrdinalIgnoreCase));

    internal void ReportCommandProgress(string runId, int current, int max, string? description, bool emitBatchProgress)
    {
        var safeMax = Math.Max(1, max);
        var safeCurrent = Math.Clamp(current, 0, safeMax);
        var safeDescription = string.IsNullOrWhiteSpace(description)
            ? $"step {safeCurrent}/{safeMax}"
            : description.Replace('\r', ' ').Replace('\n', ' ');

        try
        {
            _commandDispatcher?.UpdateStep(runId, safeCurrent, safeMax, safeDescription);
        }
        catch
        {
            // best-effort
        }

        if (emitBatchProgress)
        {
            Console.WriteLine($"__BATCH_PROGRESS__|{runId}|{safeCurrent}|{safeMax}|{safeDescription}");
        }
    }

// =====================================================================
// Generate Ambience Audio Command
// =====================================================================

    internal Task<(bool success, string? message)> StartAmbienceAudioGenerationAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = $"ambience_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        _customLogger?.Append(runId, $"[{storyId}] Avvio generazione audio ambientale nella cartella {context.FolderPath}");

        _ = Task.Run(async () =>
        {
            try
            {
                var (success, message) = await GenerateAmbienceAudioInternalAsync(context, runId);

                if (success)
                {
                    try
                    {
                        var story = GetStoryById(storyId);
                        if (story != null)
                        {
                            var allowNext = ApplyStatusTransitionWithCleanup(story, "ambient_generated", runId);
                            if (!allowNext)
                            {
                                _customLogger?.Append(runId, $"[{storyId}] delete_next_items attivo: cleanup dopo ambient_generated applicato", "info");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Impossibile aggiornare lo stato della storia {Id}", storyId);
                        _customLogger?.Append(runId, $"[{storyId}] Aggiornamento stato fallito: {ex.Message}");
                    }
                }

                await (_customLogger?.MarkCompletedAsync(runId, message ?? (success ? "Generazione audio ambientale completata" : "Errore generazione audio ambientale"))
                    ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Errore non gestito durante la generazione audio ambientale per la storia {Id}", storyId);
                _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
                await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            }
        });

        return Task.FromResult<(bool success, string? message)>((true, $"Generazione audio ambientale avviata (run {runId}). Monitora i log per i dettagli."));
    }

    internal async Task<(bool success, string? message)> GenerateAmbienceAudioInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");
        var cancellationToken = CurrentDispatcherCancellationToken ?? CancellationToken.None;
        var maxMissingTolerated = Math.Max(0, _audioGenerationOptions?.CurrentValue?.Ambience?.MaxMissingSoundsTolerated ?? 0);

        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Get timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Build list of ambient sound segments: each ambient definition lasts until the next definition.
        var ambienceSegments = ExtractAmbienceSegments(phraseEntries);
        var hasAmbientDefinitions = phraseEntries.Any(HasAmbientSoundRequest);

        if (hasAmbientDefinitions)
        {
            // Persist ambientSoundsDuration before generating WAVs (best-effort).
            try
            {
                SanitizeTtsSchemaTextFields(rootNode);
                var updated = rootNode.ToJsonString(SchemaJsonOptions);
                await File.WriteAllTextAsync(schemaPath, updated);
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Impossibile salvare ambientSoundsDuration: {ex.Message}");
            }
        }

        if (ambienceSegments.Count == 0)
        {
            var msg = "Nessun segmento ambient sounds trovato nella timeline (nessuna propriet� 'ambientSounds' presente)";
            _customLogger?.Append(runId, $"[{story.Id}] {msg}");
            // No segments is a valid completion for this step: persist generated flag.
            try { _database.UpdateStoryGeneratedAmbient(story.Id, true); } catch { }
            return (true, msg);
        }

        _customLogger?.Append(runId, $"[{story.Id}] Trovati {ambienceSegments.Count} segmenti ambient sounds da assegnare da libreria sounds");

        Directory.CreateDirectory(folderPath);

        int segmentCounter = 0;
        int assignedCount = 0;
        int missingCount = 0;
        var emitBatchProgress = IsBatchWorkerProcess();
        ReportCommandProgress(
            runId,
            0,
            ambienceSegments.Count,
            $"Ambient: preparazione ricerca suoni (chunk 0/{Math.Max(1, ambienceSegments.Count)})",
            emitBatchProgress);
        foreach (var segment in ambienceSegments)
        {
            segmentCounter++;
            var durationSeconds = (int)Math.Ceiling(segment.DurationMs / 1000.0);
            if (durationSeconds < 1) durationSeconds = 1;
            _customLogger?.Append(runId, $"[{story.Id}] Ricerca ambient {segmentCounter}/{ambienceSegments.Count}: '{segment.AmbiencePrompt}' ({durationSeconds}s)");

            ReportCommandProgress(
                runId,
                segmentCounter,
                ambienceSegments.Count,
                $"Ambient: ricerca/assegnazione suono chunk {segmentCounter}/{ambienceSegments.Count}",
                emitBatchProgress);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selection = await TrySelectCatalogSoundForSchemaWithAutoSearchAsync(
                    story,
                    runId,
                    soundType: "amb",
                    prompt: segment.AmbiencePrompt,
                    tags: segment.AmbientTags,
                    source: "tts_schema.ambient_sound_description",
                    preferredDurationSeconds: durationSeconds,
                    cancellationToken: cancellationToken);

                if (selection == null)
                {
                    missingCount++;
                    var err = $"Nessun suono ambientale trovato per segmento {segmentCounter} ('{segment.AmbiencePrompt}'). Ricerca online fallita o senza risultati.";
                    _customLogger?.Append(runId, $"[{story.Id}] [WARN] {err}");
                    if (missingCount > maxMissingTolerated)
                    {
                        return (false, $"{err} (mancanti={missingCount}, tollerati={maxMissingTolerated})");
                    }
                    continue;
                }
                var localCopiedPath = EnsureStoryLocalCopyOfCatalogSound(folderPath, selection.SourcePath, story.Id, runId);
                var localFileName = Path.GetFileName(localCopiedPath);
                assignedCount++;
                _customLogger?.Append(runId, $"[{story.Id}] Salvato da sounds: {localFileName}");

                // Save prompt to .txt file with same name
                var promptFileName = Path.ChangeExtension(localFileName, ".txt");
                var promptFilePath = Path.Combine(folderPath, promptFileName);
                await File.WriteAllTextAsync(promptFilePath, segment.AmbiencePrompt);
                _customLogger?.Append(runId, $"[{story.Id}] Salvato prompt: {promptFileName}");

                // Update tts_schema.json: add ambient sound file reference only on the definition record
                if (segment.DefinitionIndex >= 0 && segment.DefinitionIndex < phraseEntries.Count)
                {
                    phraseEntries[segment.DefinitionIndex]["ambient_sound_file"] = localFileName;
                    phraseEntries[segment.DefinitionIndex]["ambientSoundsFile"] = localFileName;
                    phraseEntries[segment.DefinitionIndex]["ambient_sound_source_path"] = localCopiedPath;
                    phraseEntries[segment.DefinitionIndex]["ambientSoundSourcePath"] = localCopiedPath;
                    phraseEntries[segment.DefinitionIndex]["ambient_sound_id"] = selection.Sound.Id;
                    phraseEntries[segment.DefinitionIndex]["ambientSoundId"] = selection.Sound.Id;
                }
            }
            catch (OperationCanceledException)
            {
                var err = "Operazione annullata";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
            catch (Exception ex)
            {
                var err = $"Errore generazione segmento {segmentCounter}: {ex.Message}";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        // Save updated schema
        try
        {
            SanitizeTtsSchemaTextFields(rootNode);
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
            // Mark story as having generated ambience audio (best-effort)
            try { _database.UpdateStoryGeneratedAmbient(story.Id, true); } catch { }
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = $"Generazione audio ambientale completata ({segmentCounter} segmenti)";
        if (missingCount > 0)
        {
            successMsg += $", mancanti={missingCount}, assegnati={assignedCount}";
        }
        ReportCommandProgress(
            runId,
            ambienceSegments.Count,
            ambienceSegments.Count,
            $"Ambient: completato ({ambienceSegments.Count}/{ambienceSegments.Count})",
            emitBatchProgress);
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    private sealed record AmbienceSegment(string AmbiencePrompt, string? AmbientTags, int StartMs, int DurationMs, int DefinitionIndex);

    /// <summary>
    /// Extracts ambient sound segments from timeline entries.
    /// Uses the 'ambient_sounds' property (from [RUMORI: ...] tag) instead of 'ambience'.
    /// The 'ambience' property is now reserved for future image generation.
    /// </summary>
    private static List<AmbienceSegment> ExtractAmbienceSegments(List<JsonObject> entries)
    {
        var segments = new List<AmbienceSegment>();
        if (entries.Count == 0) return segments;

        // Collect ambient definition points (where ambient sounds are explicitly defined).
        var definitionPoints = new List<(int Index, string Prompt, string? Tags, int StartMs)>();
        for (int i = 0; i < entries.Count; i++)
        {
            var ambientSounds = ReadAmbientSoundsDescription(entries[i]);
            if (string.IsNullOrWhiteSpace(ambientSounds)) continue;
            var ambientTags = ReadAmbientSoundsTags(entries[i]);

            var startMs = ReadEntryStartMs(entries[i]);
            definitionPoints.Add((i, ambientSounds!.Trim(), ambientTags, startMs));
        }

        if (definitionPoints.Count == 0) return segments;

        for (int i = 0; i < definitionPoints.Count; i++)
        {
            var current = definitionPoints[i];
            var nextIndex = (i + 1 < definitionPoints.Count) ? definitionPoints[i + 1].Index : entries.Count;
            var nextStartMs = (i + 1 < definitionPoints.Count) ? definitionPoints[i + 1].StartMs : (int?)null;

            int segmentStartMs = current.StartMs;
            int segmentEndMs = nextStartMs.HasValue && nextStartMs.Value > 0
                ? nextStartMs.Value
                : ReadEntryEndMs(entries[Math.Max(0, Math.Min(entries.Count - 1, nextIndex - 1))]);

            var duration = segmentEndMs - segmentStartMs;
            // Persist ambientSoundsDuration on the definition entry, even if duration can't be computed.
            entries[current.Index]["ambientSoundsDuration"] = Math.Max(0, duration);

            if (duration > 0)
            {
                segments.Add(new AmbienceSegment(current.Prompt, current.Tags, segmentStartMs, duration, current.Index));
            }
        }

        return segments;
    }

    private static string? ReadAmbientSoundsDescription(JsonObject entry)
    {
        // Read from standardized snake_case first, then legacy/camelCase.
        // Fallback to tags for schemas that no longer persist ambient descriptions.
        return ReadString(entry, "ambient_sound_description") ??
               ReadString(entry, "ambientSoundDescription") ??
               ReadString(entry, "AmbientSoundDescription") ??
               ReadString(entry, "ambientSounds") ??
               ReadString(entry, "AmbientSounds") ??
               ReadString(entry, "ambient_sounds") ??
               ReadAmbientSoundsTags(entry);
    }

    private static string? ReadAmbientSoundsTags(JsonObject entry)
    {
        return ReadString(entry, "ambient_sound_tags") ??
               ReadString(entry, "ambientSoundTags") ??
               ReadString(entry, "AmbientSoundTags");
    }

    private static string? ReadFxTags(JsonObject entry)
    {
        return ReadString(entry, "fx_tags") ??
               ReadString(entry, "fxTags") ??
               ReadString(entry, "FxTags");
    }

    private static string? ReadMusicTags(JsonObject entry)
    {
        return ReadString(entry, "music_tags") ??
               ReadString(entry, "musicTags") ??
               ReadString(entry, "MusicTags");
    }

    private static int? ReadMusicPreferredDurationSeconds(JsonObject entry)
    {
        if (TryReadNumber(entry, "musicDurationSecs", out var secs) ||
            TryReadNumber(entry, "music_duration_secs", out secs) ||
            TryReadNumber(entry, "MusicDurationSecs", out secs) ||
            TryReadNumber(entry, "musicDuration", out secs) ||
            TryReadNumber(entry, "music_duration", out secs) ||
            TryReadNumber(entry, "MusicDuration", out secs))
        {
            var s = (int)Math.Ceiling(secs);
            if (s > 0) return s;
        }

        if (TryReadNumber(entry, "musicDurationMs", out var ms) ||
            TryReadNumber(entry, "music_duration_ms", out ms) ||
            TryReadNumber(entry, "MusicDurationMs", out ms))
        {
            var s = (int)Math.Ceiling(ms / 1000.0);
            if (s > 0) return s;
        }

        return null;
    }

    private static string NormalizeMusicPromptForSearch(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        // Esempio da ripulire: "18 s | exploration, journey, travel"
        text = Regex.Replace(text, @"^\s*\d+\s*s(ec)?\s*[\|\-:]\s*", string.Empty, RegexOptions.IgnoreCase);
        var pipeIndex = text.IndexOf('|');
        if (pipeIndex >= 0 && pipeIndex < text.Length - 1)
        {
            text = text[(pipeIndex + 1)..].Trim();
        }

        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    private static string? ExtractMusicTagsFromDescription(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var pipeIndex = text.IndexOf('|');
        if (pipeIndex < 0 || pipeIndex >= text.Length - 1)
        {
            return null;
        }

        var candidate = text[(pipeIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        // Deve sembrare una lista tag CSV, non una frase lunga.
        return candidate.Contains(',') ? candidate : null;
    }

    private static bool HasAmbientSoundRequest(JsonObject entry)
    {
        return !string.IsNullOrWhiteSpace(ReadAmbientSoundsDescription(entry)) ||
               !string.IsNullOrWhiteSpace(
                   ReadString(entry, "ambient_sound_file") ??
                   ReadString(entry, "AmbientSoundFile") ??
                   ReadString(entry, "ambientSoundFile") ??
                   ReadString(entry, "ambientSoundsFile") ??
                   ReadString(entry, "AmbientSoundsFile") ??
                   ReadString(entry, "ambient_sounds_file")) ||
               !string.IsNullOrWhiteSpace(
                   ReadString(entry, "ambient_sound_source_path") ??
                   ReadString(entry, "ambientSoundSourcePath") ??
                   ReadString(entry, "AmbientSoundSourcePath"));
    }

    private static bool HasFxRequest(JsonObject entry)
    {
        return !string.IsNullOrWhiteSpace(
                   ReadString(entry, "fxDescription") ??
                   ReadString(entry, "FxDescription") ??
                   ReadString(entry, "fx_description")) ||
               !string.IsNullOrWhiteSpace(ReadFxTags(entry));
    }

    private static bool HasMusicRequest(JsonObject entry)
    {
        return !string.IsNullOrWhiteSpace(
                   ReadString(entry, "music_description") ??
                   ReadString(entry, "musicDescription") ??
                   ReadString(entry, "MusicDescription")) ||
               !string.IsNullOrWhiteSpace(ReadMusicTags(entry));
    }

    private static int ReadEntryStartMs(JsonObject entry)
    {
        if (TryReadNumber(entry, "startMs", out var startVal) || TryReadNumber(entry, "StartMs", out startVal) || TryReadNumber(entry, "start_ms", out startVal))
            return (int)startVal;
        return 0;
    }

    private static int ReadEntryEndMs(JsonObject entry)
    {
        int startMs = ReadEntryStartMs(entry);
        if (TryReadNumber(entry, "endMs", out var endVal) || TryReadNumber(entry, "EndMs", out endVal) || TryReadNumber(entry, "end_ms", out endVal))
        {
            var endMs = (int)endVal;
            if (endMs > 0) return endMs;
        }

        if (TryReadNumber(entry, "durationMs", out var durVal) || TryReadNumber(entry, "DurationMs", out durVal) || TryReadNumber(entry, "duration_ms", out durVal))
        {
            var durationMs = (int)durVal;
            if (durationMs > 0) return startMs + durationMs;
        }

        return startMs;
    }

    private static int ReadAmbientSoundsDurationMs(JsonObject entry)
    {
        if (TryReadNumber(entry, "ambientSoundsDuration", out var d) ||
            TryReadNumber(entry, "AmbientSoundsDuration", out d) ||
            TryReadNumber(entry, "ambient_sounds_duration", out d))
        {
            return (int)d;
        }

        return 0;
    }

    private static bool TimelineContainsTitle(List<JsonObject> entries, string title)
    {
        if (entries == null || entries.Count == 0 || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var titleNorm = NormalizeForTitleMatch(title);
        if (string.IsNullOrWhiteSpace(titleNorm))
        {
            return false;
        }

        foreach (var entry in entries)
        {
            if (!TryReadPhrase(entry, out _, out var text, out _))
            {
                continue;
            }

            var textNorm = NormalizeForTitleMatch(text);
            if (!string.IsNullOrWhiteSpace(textNorm) && textNorm.Contains(titleNorm, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeForTitleMatch(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string BuildOpeningAnnouncement(string? seriesTitle, int? episodeNumber, string storyTitle, string? episodeTitle)
    {
        var cleanedSeries = (seriesTitle ?? string.Empty).Trim();
        var cleanedStoryTitle = (storyTitle ?? string.Empty).Trim();
        var cleanedEpisodeTitle = (episodeTitle ?? string.Empty).Trim();

        var titleForAnnouncement = !string.IsNullOrWhiteSpace(cleanedEpisodeTitle)
            ? cleanedEpisodeTitle
            : StripSeriesEpisodePrefix(cleanedStoryTitle, cleanedSeries, episodeNumber);
        if (string.IsNullOrWhiteSpace(titleForAnnouncement))
        {
            titleForAnnouncement = cleanedStoryTitle;
        }

        var parts = new List<string>();
        void AddPart(string? value)
        {
            var part = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(part))
            {
                return;
            }

            var norm = NormalizeForTitleMatch(part);
            if (string.IsNullOrWhiteSpace(norm))
            {
                return;
            }

            if (parts.Any(p => NormalizeForTitleMatch(p) == norm))
            {
                return;
            }

            parts.Add(part);
        }

        AddPart(cleanedSeries);
        if (episodeNumber.HasValue && !ContainsEpisodeMarker(titleForAnnouncement, episodeNumber.Value))
        {
            AddPart($"Episodio {episodeNumber.Value}");
        }

        if (!string.IsNullOrWhiteSpace(titleForAnnouncement))
        {
            if (string.IsNullOrWhiteSpace(cleanedSeries) ||
                !NormalizeForTitleMatch(titleForAnnouncement).Contains(NormalizeForTitleMatch(cleanedSeries), StringComparison.Ordinal))
            {
                AddPart(titleForAnnouncement);
            }
        }

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(cleanedStoryTitle))
        {
            AddPart(cleanedStoryTitle);
        }

        var announcement = string.Join(". ", parts).Trim();
        if (string.IsNullOrWhiteSpace(announcement))
        {
            return cleanedStoryTitle;
        }

        return announcement.EndsWith('.') ? announcement : $"{announcement}.";
    }

    private static string StripSeriesEpisodePrefix(string title, string seriesTitle, int? episodeNumber)
    {
        var value = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(seriesTitle))
        {
            var escapedSeries = Regex.Escape(seriesTitle.Trim());
            value = Regex.Replace(value, @"^\s*" + escapedSeries + @"\s*[-:|,.]*\s*", string.Empty, RegexOptions.IgnoreCase);
        }

        if (episodeNumber.HasValue)
        {
            var ep = Regex.Escape(episodeNumber.Value.ToString());
            value = Regex.Replace(value, @"^\s*(?:ep(?:isodio)?|episode)\s*\.?\s*" + ep + @"\s*[:\-–—]?\s*", string.Empty, RegexOptions.IgnoreCase);
        }

        return value.Trim(' ', '-', ':', ',', '.', '|');
    }

    private static bool ContainsEpisodeMarker(string value, int episodeNumber)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var ep = Regex.Escape(episodeNumber.ToString());
        var pattern = @"\b(?:ep(?:isodio)?|episode)\s*\.?\s*" + ep + @"\b";
        return Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase);
    }

    // =====================================================================
    // Generate FX Audio Command (Sound Effects)
    // =====================================================================

    internal Task<(bool success, string? message)> StartFxAudioGenerationAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = $"fx_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        _customLogger?.Append(runId, $"[{storyId}] Avvio generazione effetti sonori nella cartella {context.FolderPath}");

        _ = Task.Run(async () =>
        {
            try
            {
                var (success, message) = await GenerateFxAudioInternalAsync(context, runId);

                if (success)
                {
                    try
                    {
                        var story = GetStoryById(storyId);
                        if (story != null)
                        {
                            var allowNext = ApplyStatusTransitionWithCleanup(story, "fx_generated", runId);
                            if (!allowNext)
                            {
                                _customLogger?.Append(runId, $"[{storyId}] delete_next_items attivo: cleanup dopo fx_generated applicato", "info");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Impossibile aggiornare lo stato della storia {Id}", storyId);
                        _customLogger?.Append(runId, $"[{storyId}] Aggiornamento stato fallito: {ex.Message}");
                    }
                }

                await (_customLogger?.MarkCompletedAsync(runId, message ?? (success ? "Generazione effetti sonori completata" : "Errore generazione effetti sonori"))
                    ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Errore non gestito durante la generazione effetti sonori per la storia {Id}", storyId);
                _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
                await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            }
        });

        return Task.FromResult<(bool success, string? message)>((true, $"Generazione effetti sonori avviata (run {runId}). Monitora i log per i dettagli."));
    }

    internal async Task<(bool success, string? message)> GenerateFxAudioInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");
        var cancellationToken = CurrentDispatcherCancellationToken ?? CancellationToken.None;
        var maxMissingTolerated = Math.Max(0, _audioGenerationOptions?.CurrentValue?.Fx?.MaxMissingSoundsTolerated ?? 0);

        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Get timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Find entries with FX properties
        var fxEntries = new List<(int Index, JsonObject Entry, string Description, string? Tags, int Duration)>();
        for (int i = 0; i < phraseEntries.Count; i++)
        {
            var entry = phraseEntries[i];
            var fxDesc = ReadString(entry, "fxDescription") ?? ReadString(entry, "FxDescription") ?? ReadString(entry, "fx_description");
            if (!string.IsNullOrWhiteSpace(fxDesc))
            {
                int fxDuration = 5; // default
                if (TryReadNumber(entry, "fxDuration", out var dur) || TryReadNumber(entry, "FxDuration", out dur) || TryReadNumber(entry, "fx_duration", out dur))
                    fxDuration = (int)dur;
                fxEntries.Add((i, entry, fxDesc, ReadFxTags(entry), fxDuration));
            }
        }

        if (fxEntries.Count == 0)
        {
            var msg = "Nessun effetto sonoro da generare (nessuna propriet� 'fxDescription' presente)";
            _customLogger?.Append(runId, $"[{story.Id}] {msg}");
            // No FX entries is a valid completion for this step: persist generated flag.
            try { _database.UpdateStoryGeneratedEffects(story.Id, true); } catch { }
            return (true, msg);
        }

        _customLogger?.Append(runId, $"[{story.Id}] Trovati {fxEntries.Count} effetti sonori da assegnare da libreria sounds");

        Directory.CreateDirectory(folderPath);

        int fxCounter = 0;
        int assignedCount = 0;
        int missingCount = 0;
        var emitBatchProgress = IsBatchWorkerProcess();
        ReportCommandProgress(
            runId,
            0,
            fxEntries.Count,
            $"FX: preparazione ricerca suoni (chunk 0/{Math.Max(1, fxEntries.Count)})",
            emitBatchProgress);
        foreach (var (index, entry, description, tags, duration) in fxEntries)
        {
            fxCounter++;
            var durationSeconds = duration;
            if (durationSeconds < 1) durationSeconds = 1;

            var currentFxFile = ReadString(entry, "fxFile") ?? ReadString(entry, "FxFile") ?? ReadString(entry, "fx_file");
            var currentFxSourcePath = ReadString(entry, "fx_source_path") ?? ReadString(entry, "fxSourcePath") ?? ReadString(entry, "FxSourcePath");
            if ((!string.IsNullOrWhiteSpace(currentFxFile) && File.Exists(Path.Combine(folderPath, currentFxFile))) ||
                (!string.IsNullOrWhiteSpace(currentFxSourcePath) && File.Exists(currentFxSourcePath)))
            {
                continue;
            }

            _customLogger?.Append(runId, $"[{story.Id}] Ricerca FX {fxCounter}/{fxEntries.Count}: '{description}' ({durationSeconds}s)");

            ReportCommandProgress(
                runId,
                fxCounter,
                fxEntries.Count,
                $"FX: ricerca/assegnazione suono chunk {fxCounter}/{fxEntries.Count}",
                emitBatchProgress);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selection = await TrySelectCatalogSoundForSchemaWithAutoSearchAsync(
                    story,
                    runId,
                    soundType: "fx",
                    prompt: description,
                    tags: tags,
                    source: "tts_schema.fxDescription",
                    preferredDurationSeconds: durationSeconds,
                    cancellationToken: cancellationToken);
                if (selection == null)
                {
                    missingCount++;
                    var err = $"Nessun FX trovato per '{description}'. Ricerca online fallita o senza risultati.";
                    _customLogger?.Append(runId, $"[{story.Id}] [WARN] {err}");
                    if (missingCount > maxMissingTolerated)
                    {
                        return (false, $"{err} (mancanti={missingCount}, tollerati={maxMissingTolerated})");
                    }
                    continue;
                }
                var localCopiedPath = EnsureStoryLocalCopyOfCatalogSound(folderPath, selection.SourcePath, story.Id, runId);
                var localFileName = Path.GetFileName(localCopiedPath);
                assignedCount++;
                _customLogger?.Append(runId, $"[{story.Id}] Salvato da sounds: {localFileName}");
                
                // Save prompt to .txt file with same name
                var promptFileName = Path.ChangeExtension(localFileName, ".txt");
                var promptFilePath = Path.Combine(folderPath, promptFileName);
                await File.WriteAllTextAsync(promptFilePath, description);
                _customLogger?.Append(runId, $"[{story.Id}] Salvato prompt: {promptFileName}");

                // Update tts_schema.json: add fxFile property
                entry["fxFile"] = localFileName;
                entry["fx_file"] = localFileName;
                entry["fx_source_path"] = localCopiedPath;
                entry["fxSourcePath"] = localCopiedPath;
                entry["fx_sound_id"] = selection.Sound.Id;
                entry["fxSoundId"] = selection.Sound.Id;
            }
            catch (OperationCanceledException)
            {
                var err = "Operazione annullata";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
            catch (Exception ex)
            {
                var err = $"Errore generazione FX {fxCounter}: {ex.Message}";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        // Save updated schema
        try
        {
            SanitizeTtsSchemaTextFields(rootNode);
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
            // Mark story as having generated FX (best-effort)
            try { _database.UpdateStoryGeneratedEffects(story.Id, true); } catch { }
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = $"Generazione effetti sonori completata ({fxCounter} effetti, assegnati={assignedCount}, mancanti={missingCount})";
        ReportCommandProgress(
            runId,
            fxEntries.Count,
            fxEntries.Count,
            $"FX: completato ({fxEntries.Count}/{fxEntries.Count})",
            emitBatchProgress);
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    private sealed record CatalogSoundMatch(Sound Sound, double Score, int MatchedTokens);
    private sealed record CatalogSoundSelection(Sound Sound, string SourcePath);

    private async Task<CatalogSoundSelection?> TrySelectCatalogSoundForSchemaWithAutoSearchAsync(
        StoryRecord story,
        string runId,
        string soundType,
        string prompt,
        string? tags,
        string source,
        int? preferredDurationSeconds,
        CancellationToken cancellationToken)
    {
        var normalizedType = NormalizeSoundType(soundType);
        _customLogger?.Append(runId, $"[{story.Id}] Ricerca in libreria sounds type={normalizedType} prompt='{prompt}' tags='{tags}'");

        var selection = TrySelectCatalogSoundForSchema(
            story,
            runId,
            soundType,
            prompt,
            tags,
            source,
            preferredDurationSeconds,
            out var missingSoundId);
        if (selection != null)
        {
            return selection;
        }

        if (normalizedType is not ("fx" or "amb" or "music"))
        {
            return null;
        }

        if (_soundSearchService == null)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] SoundSearchService non disponibile: impossibile cercare online il suono mancante.");
            return null;
        }

        if (!missingSoundId.HasValue || missingSoundId.Value <= 0)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] sounds_missing non registrato correttamente: impossibile lanciare ricerca online.");
            return null;
        }

        _customLogger?.Append(runId, $"[{story.Id}] Avvio ricerca online per sounds_missing #{missingSoundId.Value} (type={normalizedType})...");
        try
        {
            var searchResult = await _soundSearchService.ProcessOneMissingSoundAsync(
                missingSoundId.Value,
                cancellationToken,
                runId: runId).ConfigureAwait(false);

            _customLogger?.Append(runId,
                $"[{story.Id}] Ricerca online completata per sounds_missing #{missingSoundId.Value}: status={searchResult.Status}; candidati={searchResult.CandidatesSeen}; inseriti={searchResult.InsertedCount}; errori={searchResult.Errors.Count}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Ricerca online suono mancante fallita: {ex.Message}");
            return null;
        }

        _customLogger?.Append(runId, $"[{story.Id}] Nuovo tentativo ricerca in libreria sounds dopo download online...");
        return TrySelectCatalogSoundForSchema(
            story,
            runId,
            soundType,
            prompt,
            tags,
            source,
            preferredDurationSeconds,
            out _);
    }

    private async Task AssignCatalogLibraryReferencesInTtsSchemaAsync(
        string schemaPath,
        StoryRecord story,
        string folderPath,
        string runId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(schemaPath) || !File.Exists(schemaPath))
            return;

        var json = await File.ReadAllTextAsync(schemaPath, cancellationToken).ConfigureAwait(false);
        var rootNode = JsonNode.Parse(json) as JsonObject;
        if (rootNode == null) return;
        if (!(rootNode["timeline"] is JsonArray timelineArray)) return;

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0) return;

        var ambAssigned = 0;
        var fxAssigned = 0;
        var musicAssigned = 0;

        // Ambient: assegna il suono alla riga di definizione [RUMORI], poi lascia tag+file/path/id come riferimento principale.
        foreach (var entry in phraseEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ambientDesc = ReadAmbientSoundsDescription(entry);
            if (string.IsNullOrWhiteSpace(ambientDesc)) continue;

            var currentSourcePath =
                ReadString(entry, "ambient_sound_source_path") ??
                ReadString(entry, "ambientSoundSourcePath") ??
                ReadString(entry, "AmbientSoundSourcePath");
            if (!string.IsNullOrWhiteSpace(currentSourcePath) && File.Exists(currentSourcePath))
                continue;

            var ambientTags = ReadAmbientSoundsTags(entry);
            var selection = await TrySelectCatalogSoundForSchemaWithAutoSearchAsync(
                story,
                runId,
                soundType: "amb",
                prompt: ambientDesc,
                tags: ambientTags,
                source: "tts_schema.ambient_sound_description",
                preferredDurationSeconds: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (selection == null) continue;

            var localAmbientPath = EnsureStoryLocalCopyOfCatalogSound(folderPath, selection.SourcePath, story.Id, runId);
            var libraryFileName = Path.GetFileName(localAmbientPath);
            entry["ambient_sound_file"] = libraryFileName;
            entry["ambientSoundsFile"] = libraryFileName;
            entry["ambient_sound_source_path"] = localAmbientPath;
            entry["ambientSoundSourcePath"] = localAmbientPath;
            entry["ambient_sound_id"] = selection.Sound.Id;
            entry["ambientSoundId"] = selection.Sound.Id;
            entry["ambient_sound_description"] = null;
            entry["ambientSoundDescription"] = null;
            entry["AmbientSoundDescription"] = null;
            entry["ambientSounds"] = null;
            entry["AmbientSounds"] = null;
            entry["ambient_sounds"] = null;
            ambAssigned++;
        }

        // FX: se risolto da libreria, salva file/path/id e rimuovi la descrizione (restano i tag).
        foreach (var entry in phraseEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fxSourcePath = ReadString(entry, "fx_source_path") ?? ReadString(entry, "fxSourcePath") ?? ReadString(entry, "FxSourcePath");
            if (!string.IsNullOrWhiteSpace(fxSourcePath) && File.Exists(fxSourcePath))
                continue;

            var fxDesc = ReadString(entry, "fxDescription") ?? ReadString(entry, "FxDescription") ?? ReadString(entry, "fx_description");
            if (string.IsNullOrWhiteSpace(fxDesc))
                fxDesc = ReadString(entry, "fx_tags") ?? ReadString(entry, "fxTags") ?? ReadString(entry, "FxTags");
            if (string.IsNullOrWhiteSpace(fxDesc)) continue;

            int? fxDuration = null;
            if (TryReadNumber(entry, "fxDuration", out var d1) || TryReadNumber(entry, "FxDuration", out d1) || TryReadNumber(entry, "fx_duration", out d1))
            {
                fxDuration = (int)Math.Ceiling(d1);
            }

            var fxTags = ReadFxTags(entry);
            var selection = await TrySelectCatalogSoundForSchemaWithAutoSearchAsync(
                story,
                runId,
                soundType: "fx",
                prompt: fxDesc,
                tags: fxTags,
                source: "tts_schema.fx_description",
                preferredDurationSeconds: fxDuration,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (selection == null) continue;

            var localFxPath = EnsureStoryLocalCopyOfCatalogSound(folderPath, selection.SourcePath, story.Id, runId);
            var libraryFileName = Path.GetFileName(localFxPath);
            entry["fx_file"] = libraryFileName;
            entry["fxFile"] = libraryFileName;
            entry["fx_source_path"] = localFxPath;
            entry["fxSourcePath"] = localFxPath;
            entry["fx_sound_id"] = selection.Sound.Id;
            entry["fxSoundId"] = selection.Sound.Id;
            entry["fx_description"] = null;
            entry["fxDescription"] = null;
            entry["FxDescription"] = null;
            fxAssigned++;
        }

        // Music: solo catalogo locale (nessuna ricerca online); se risolta, salva file/path/id e rimuovi la descrizione.
        foreach (var entry in phraseEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var musicSourcePath = ReadString(entry, "music_source_path") ?? ReadString(entry, "musicSourcePath") ?? ReadString(entry, "MusicSourcePath");
            if (!string.IsNullOrWhiteSpace(musicSourcePath) && File.Exists(musicSourcePath))
                continue;

            var musicDesc = ReadString(entry, "music_description") ?? ReadString(entry, "musicDescription") ?? ReadString(entry, "MusicDescription");
            if (string.IsNullOrWhiteSpace(musicDesc))
                musicDesc = ReadString(entry, "music_tags") ?? ReadString(entry, "musicTags") ?? ReadString(entry, "MusicTags");
            if (string.IsNullOrWhiteSpace(musicDesc)) continue;

            var musicTags = ReadMusicTags(entry);
            var musicDuration = ReadMusicPreferredDurationSeconds(entry);
            var selection = TrySelectCatalogSoundForSchema(
                story,
                runId,
                soundType: "music",
                prompt: musicDesc,
                tags: musicTags,
                source: "tts_schema.music_description",
                preferredDurationSeconds: musicDuration,
                out _);

            if (selection == null) continue;

            var localMusicPath = EnsureStoryLocalCopyOfCatalogSound(folderPath, selection.SourcePath, story.Id, runId);
            var libraryFileName = Path.GetFileName(localMusicPath);
            entry["music_file"] = libraryFileName;
            entry["musicFile"] = libraryFileName;
            entry["music_source_path"] = localMusicPath;
            entry["musicSourcePath"] = localMusicPath;
            entry["music_sound_id"] = selection.Sound.Id;
            entry["musicSoundId"] = selection.Sound.Id;
            entry["music_description"] = null;
            entry["musicDescription"] = null;
            entry["MusicDescription"] = null;
            musicAssigned++;
        }

        SanitizeTtsSchemaTextFields(rootNode);
        await File.WriteAllTextAsync(schemaPath, rootNode.ToJsonString(SchemaJsonOptions), cancellationToken).ConfigureAwait(false);
        _customLogger?.Append(runId, $"[{story.Id}] tts_schema: riferimenti libreria assegnati (amb={ambAssigned}, fx={fxAssigned}, music={musicAssigned})");
    }

    private CatalogSoundSelection? TrySelectCatalogSoundForSchema(
        StoryRecord story,
        string runId,
        string soundType,
        string prompt,
        string? tags,
        string source,
        int? preferredDurationSeconds,
        out long? missingSoundId)
    {
        missingSoundId = null;
        var match = FindBestCatalogSoundMatch(soundType, prompt, tags, preferredDurationSeconds);
        if (match == null)
        {
            missingSoundId = RegisterMissingSoundBestEffort(story, soundType, prompt, tags, source, runId);
            return null;
        }

        var sourcePath = ResolveExistingSoundFilePath(match.Sound);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Suono #{match.Sound.Id} selezionato ma file non trovato: {match.Sound.SoundPath}");
            missingSoundId = RegisterMissingSoundBestEffort(story, soundType, prompt, tags, source, runId);
            return null;
        }

        try
        {
            _database.MarkSoundUsed(match.Sound.Id);
        }
        catch
        {
            // best-effort
        }

        _customLogger?.Append(runId, $"[{story.Id}] Match sounds {soundType}: soundId={match.Sound.Id}, score={match.Score:0.##}, matchedTokens={match.MatchedTokens}, file='{match.Sound.SoundName}'");
        return new CatalogSoundSelection(match.Sound, sourcePath);
    }

    private string EnsureStoryLocalCopyOfCatalogSound(string folderPath, string sourcePath, long storyId, string? runId)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("Cartella storia non valida", nameof(folderPath));
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("File sorgente sounds non trovato", sourcePath);

        Directory.CreateDirectory(folderPath);

        var fileName = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException($"Nome file non valido per '{sourcePath}'");

        var destPath = Path.Combine(folderPath, fileName);
        if (File.Exists(destPath))
        {
            // Gia' presente: usa la copia locale esistente.
            return destPath;
        }

        File.Copy(sourcePath, destPath, overwrite: false);
        if (!string.IsNullOrWhiteSpace(runId))
        {
            _customLogger?.Append(runId!, $"[{storyId}] Copiato suono da libreria in cartella storia: {fileName}");
        }
        return destPath;
    }

    private CatalogSoundMatch? FindBestCatalogSoundMatch(string soundType, string prompt, string? tags, int? preferredDurationSeconds)
    {
        List<Sound> candidates;
        try
        {
            candidates = _database.ListSounds(type: NormalizeSoundType(soundType));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Errore leggendo sounds (type={Type})", soundType);
            return null;
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var requestTagTokensOrdered = ParseNormalizedCsvTagsOrdered(tags);
        var requestTagTokens = new HashSet<string>(requestTagTokensOrdered, StringComparer.OrdinalIgnoreCase);
        var usePromptFallback = requestTagTokens.Count == 0;
        var requestTokens = usePromptFallback
            ? BuildSoundSearchTokens(prompt, null)
            : new HashSet<string>(requestTagTokens, StringComparer.OrdinalIgnoreCase);
        var normalizedRequestType = NormalizeSoundType(soundType);
        var primaryRequiredTag = !usePromptFallback && requestTagTokensOrdered.Count > 0
            ? requestTagTokensOrdered[0]
            : null;
        var secondaryRequiredTag = normalizedRequestType == "fx" && !usePromptFallback && requestTagTokensOrdered.Count > 1
            ? requestTagTokensOrdered[1]
            : null;
        if (requestTokens.Count == 0)
        {
            return null;
        }

        CatalogSoundMatch? best = null;

        foreach (var sound in candidates)
        {
            if (!sound.Enabled)
            {
                continue;
            }

            var path = ResolveExistingSoundFilePath(sound);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var tagTokens = ParseNormalizedCsvTags(sound.Tags);
            if (tagTokens.Count == 0 && usePromptFallback)
            {
                // Compatibilità con librerie sounds ancora non taggate.
                tagTokens = TokenizeSoundSearchText(sound.Description);
            }

            if (!usePromptFallback && !string.IsNullOrWhiteSpace(primaryRequiredTag))
            {
                if (normalizedRequestType == "fx")
                {
                    var matchesPrimaryOrSecondary =
                        tagTokens.Contains(primaryRequiredTag) ||
                        (!string.IsNullOrWhiteSpace(secondaryRequiredTag) && tagTokens.Contains(secondaryRequiredTag));
                    if (!matchesPrimaryOrSecondary)
                    {
                        // Regola FX: almeno uno dei primi due tag (sinonimi/varianti principali) deve matchare.
                        continue;
                    }
                }
                else if (!tagTokens.Contains(primaryRequiredTag))
                {
                    // Regola generale: il primo tag richiesto (piu' importante) deve matchare obbligatoriamente.
                    continue;
                }
            }

            var matched = 0;
            foreach (var token in requestTokens)
            {
                if (tagTokens.Contains(token))
                {
                    matched++;
                }
            }

            if (matched <= 0)
            {
                continue;
            }

            var primaryRankMetric = matched; // fallback legacy: numero tag matchati
            var score = (double)matched;

            if (!usePromptFallback)
            {
                var slotMatch = ComputeSlotMatchScore(normalizedRequestType, requestTagTokensOrdered, tagTokens, preferredDurationSeconds, sound.DurationSeconds);
                if (!slotMatch.IsValid)
                {
                    continue;
                }

                // Ranking intelligente: prima copertura slot, poi score pesato slot.
                primaryRankMetric = slotMatch.SlotsMatched;
                score = slotMatch.Score + (matched * 0.05d);

                // Per richieste strutturate richiediamo almeno il numero minimo di slot sensato.
                if (slotMatch.SlotsMatched < slotMatch.MinimumRequiredSlots)
                {
                    continue;
                }
            }
            else
            {
                var minRequiredMatches = GetMinimumRequiredTagMatches(requestTokens.Count);
                if (matched < minRequiredMatches)
                {
                    continue;
                }
            }

            var current = new CatalogSoundMatch(sound, score, primaryRankMetric);
            if (best == null || IsBetterCatalogSoundMatch(current, best))
            {
                best = current;
            }
        }

        return best;
    }

    private static int GetMinimumRequiredTagMatches(int requestedTagCount)
    {
        if (requestedTagCount <= 0) return 1;
        if (requestedTagCount >= 6) return 3;
        if (requestedTagCount >= 3) return 2;
        return 1;
    }

    private static bool IsBetterCatalogSoundMatch(CatalogSoundMatch candidate, CatalogSoundMatch current)
    {
        if (candidate.MatchedTokens != current.MatchedTokens) return candidate.MatchedTokens > current.MatchedTokens;
        if (candidate.Score != current.Score) return candidate.Score > current.Score;

        var candidateFinal = candidate.Sound.ScoreFinal ?? double.MinValue;
        var currentFinal = current.Sound.ScoreFinal ?? double.MinValue;
        if (candidateFinal != currentFinal) return candidateFinal > currentFinal;

        var candidateHuman = candidate.Sound.ScoreHuman ?? double.MinValue;
        var currentHuman = current.Sound.ScoreHuman ?? double.MinValue;
        if (candidateHuman != currentHuman) return candidateHuman > currentHuman;

        var candidateUsage = candidate.Sound.UsageCount;
        var currentUsage = current.Sound.UsageCount;
        if (candidateUsage != currentUsage) return candidateUsage < currentUsage;

        return candidate.Sound.Id < current.Sound.Id;
    }

    private static SlotMatchScore ComputeSlotMatchScore(
        string requestType,
        IReadOnlyList<string> orderedRequestTags,
        HashSet<string> candidateTagTokens,
        int? preferredDurationSeconds,
        double? candidateDurationSeconds)
    {
        if (orderedRequestTags == null || orderedRequestTags.Count == 0)
        {
            return new SlotMatchScore(false, 0, 0d, 1);
        }

        var slotWeights = new[] { 5d, 3d, 2d, 1d };
        var slotsAvailable = 0;
        var slotsMatched = 0;
        var score = 0d;

        for (var slotIndex = 0; slotIndex < 4; slotIndex++)
        {
            var i = slotIndex * 2;
            var a = i < orderedRequestTags.Count ? orderedRequestTags[i] : null;
            var b = (i + 1) < orderedRequestTags.Count ? orderedRequestTags[i + 1] : null;

            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
            {
                continue;
            }

            slotsAvailable++;
            var hit =
                (!string.IsNullOrWhiteSpace(a) && candidateTagTokens.Contains(a)) ||
                (!string.IsNullOrWhiteSpace(b) && candidateTagTokens.Contains(b));

            if (!hit)
            {
                if (slotIndex == 0)
                {
                    // Slot principale obbligatorio.
                    return new SlotMatchScore(false, 0, 0d, 1);
                }
                continue;
            }

            slotsMatched++;
            score += slotWeights[slotIndex];
        }

        if (slotsMatched <= 0)
        {
            return new SlotMatchScore(false, 0, 0d, 1);
        }

        // Regola meno rigida per la musica: basta lo slot principale, gli altri migliorano il ranking.
        // FX e ambience restano piu' restrittivi (main + almeno un altro slot se disponibili).
        var minRequiredSlots = string.Equals(requestType, "music", StringComparison.OrdinalIgnoreCase)
            ? 1
            : (slotsAvailable >= 2 ? 2 : 1);

        if (preferredDurationSeconds.HasValue && preferredDurationSeconds.Value > 0 && candidateDurationSeconds.HasValue && candidateDurationSeconds.Value > 0)
        {
            var delta = Math.Abs(candidateDurationSeconds.Value - preferredDurationSeconds.Value);
            if (delta <= 1.0d) score += 1.0d;
            else if (delta <= 3.0d) score += 0.5d;
        }

        return new SlotMatchScore(true, slotsMatched, score, minRequiredSlots);
    }

    private string? SelectOpeningMusicFromSoundsCatalog(long storyId)
    {
        try
        {
            var allMusic = _database.ListSounds(type: "music");
            if (allMusic == null || allMusic.Count == 0)
            {
                return null;
            }

            var candidates = new List<(Sound Sound, string Path, HashSet<string> Tags)>();
            foreach (var sound in allMusic)
            {
                if (!sound.Enabled) continue;
                var path = ResolveExistingSoundFilePath(sound);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                candidates.Add((sound, path, ParseNormalizedCsvTags(sound.Tags)));
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var preferred = candidates
                .Where(x => x.Tags.Contains("opening") || x.Tags.Contains("intro") || x.Tags.Contains("title"))
                .ToList();

            var pool = preferred.Count > 0 ? preferred : candidates;
            var ordered = pool
                .OrderByDescending(x => x.Sound.ScoreFinal ?? double.MinValue)
                .ThenByDescending(x => x.Sound.ScoreHuman ?? double.MinValue)
                .ThenBy(x => x.Sound.UsageCount)
                .ThenBy(x => x.Sound.Id)
                .ToList();

            var topCount = Math.Min(10, ordered.Count);
            var idx = topCount <= 1 ? 0 : (int)(Math.Abs(storyId) % topCount);
            return ordered[idx].Path;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Errore selezione opening music da sounds");
            return null;
        }
    }

    private sealed record SlotMatchScore(bool IsValid, int SlotsMatched, double Score, int MinimumRequiredSlots);

    private long? RegisterMissingSoundBestEffort(StoryRecord story, string soundType, string prompt, string? tags, string source, string? runId = null)
    {
        try
        {
            var effectiveTags = string.IsNullOrWhiteSpace(tags)
                ? string.Join(", ", BuildSoundSearchTokens(prompt, null).Take(20))
                : tags;

            var missingId = _database.UpsertMissingSound(
                type: NormalizeSoundType(soundType),
                prompt: prompt,
                tags: effectiveTags,
                storyId: story.Id,
                storyTitle: story.Title,
                source: source);
            if (!string.IsNullOrWhiteSpace(runId))
            {
                _customLogger?.Append(runId!, $"[{story.Id}] sounds_missing registrato/aggiornato: id={missingId}, type={NormalizeSoundType(soundType)}, source={source}");
            }
            return missingId;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Impossibile registrare sounds_missing per storyId={StoryId}, type={Type}", story.Id, soundType);
            if (!string.IsNullOrWhiteSpace(runId))
            {
                _customLogger?.Append(runId!, $"[{story.Id}] [WARN] Impossibile registrare sounds_missing ({NormalizeSoundType(soundType)}): {ex.Message}");
            }
        }
        return null;
    }

    private static string NormalizeSoundType(string? soundType)
    {
        var value = (soundType ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "amb" => "amb",
            "music" => "music",
            _ => "fx"
        };
    }

    private static string? ResolveExistingSoundFilePath(Sound sound)
    {
        if (sound == null || string.IsNullOrWhiteSpace(sound.SoundPath))
        {
            return null;
        }

        try
        {
            var direct = sound.SoundPath.Trim();
            if (File.Exists(direct)) return direct;

            var full = Path.GetFullPath(direct);
            if (File.Exists(full)) return full;
        }
        catch
        {
            // ignore invalid paths
        }

        return null;
    }

    private static string? ResolveSchemaAudioReferencePath(string folderPath, string? localFileName, string? sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(localFileName))
        {
            var localPath = Path.Combine(folderPath, localFileName);
            if (File.Exists(localPath))
            {
                return localPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
        {
            return sourcePath;
        }

        return null;
    }

    private static readonly HashSet<string> SoundSearchStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","ad","al","alla","alle","allo","ai","agli","all","con","da","dal","dalla","delle","degli",
        "del","dello","dei","di","e","ed","il","lo","la","le","gli","i","in","nel","nella","nelle","nei",
        "per","su","tra","fra","un","una","uno","the","and","for","with","of","to","background","sottofondo"
    };

    private static HashSet<string> BuildSoundSearchTokens(string? prompt, string? tags)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in TokenizeSoundSearchText(prompt))
        {
            all.Add(token);
        }
        foreach (var token in TokenizeSoundSearchText(tags))
        {
            all.Add(token);
        }
        return all;
    }

    private static HashSet<string> TokenizeSoundSearchText(string? text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (Match match in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9àèéìòù]{2,}"))
        {
            var token = match.Value.Trim();
            if (token.Length < 2) continue;
            if (SoundSearchStopWords.Contains(token)) continue;
            result.Add(token);
        }

        return result;
    }

    private static HashSet<string> ParseNormalizedCsvTags(string? csv)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv))
        {
            return result;
        }

        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim().ToLowerInvariant();
            token = Regex.Replace(token, @"\s+", "_");
            token = Regex.Replace(token, @"[^a-z0-9_]+", string.Empty);
            token = Regex.Replace(token, @"_+", "_").Trim('_');
            if (!string.IsNullOrWhiteSpace(token))
            {
                result.Add(token);
            }
        }

        return result;
    }

    private static List<string> ParseNormalizedCsvTagsOrdered(string? csv)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(csv))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim().ToLowerInvariant();
            token = Regex.Replace(token, @"\s+", "_");
            token = Regex.Replace(token, @"[^a-z0-9_]+", string.Empty);
            token = Regex.Replace(token, @"_+", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(token)) continue;
            if (seen.Add(token))
            {
                result.Add(token);
            }
        }

        return result;
    }

    private static string NormalizeAudioCraftFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var normalized = fileName.Trim().Trim('"', '\'');

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            normalized = uri.AbsolutePath;
        }

        normalized = normalized.Replace('\\', '/');

        var idx = normalized.LastIndexOf("/download/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            normalized = normalized.Substring(idx + "/download/".Length);
        }

        var qIndex = normalized.IndexOf('?');
        if (qIndex >= 0)
        {
            normalized = normalized.Substring(0, qIndex);
        }

        var hashIndex = normalized.IndexOf('#');
        if (hashIndex >= 0)
        {
            normalized = normalized.Substring(0, hashIndex);
        }

        normalized = normalized.Trim('/');
        normalized = Path.GetFileName(normalized);
        return normalized.Trim();
    }

    private async Task<(bool success, string? error)> TryDownloadAudioCraftFileAsync(
        HttpClient httpClient,
        string generatedFileName,
        string localFilePath,
        long storyId,
        string runId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(generatedFileName))
        {
            var err = "Nome file AudioCraft non valido";
            _customLogger?.Append(runId, $"[{storyId}] {err}", "error");
            return (false, err);
        }

        const int maxAttempts = 4;
        var lastError = string.Empty;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileToken = Uri.EscapeDataString(generatedFileName);
            var downloadUrl = $"http://localhost:8003/download/{fileToken}";

            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(45));

                using var downloadResponse = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                if (downloadResponse.IsSuccessStatusCode)
                {
                    var audioBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
                    if (audioBytes.Length == 0)
                    {
                        lastError = $"File vuoto per {generatedFileName}";
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(localFilePath, audioBytes, cancellationToken);
                        return (true, null);
                    }
                }
                else
                {
                    var status = downloadResponse.StatusCode;
                    var retryable = status == HttpStatusCode.NotFound ||
                                    status == HttpStatusCode.RequestTimeout ||
                                    status == HttpStatusCode.Conflict ||
                                    status == HttpStatusCode.TooManyRequests ||
                                    status == HttpStatusCode.InternalServerError ||
                                    status == HttpStatusCode.BadGateway ||
                                    status == HttpStatusCode.ServiceUnavailable ||
                                    status == HttpStatusCode.GatewayTimeout;

                    lastError = $"Impossibile scaricare {generatedFileName} (status {status})";
                    if (!retryable || attempt == maxAttempts)
                    {
                        _customLogger?.Append(runId, $"[{storyId}] {lastError}", "error");
                        return (false, lastError);
                    }
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = $"Timeout download {generatedFileName}: {ex.Message}";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                lastError = $"Errore rete download {generatedFileName}: {ex.Message}";
            }
            catch (Exception ex)
            {
                lastError = $"Errore download {generatedFileName}: {ex.Message}";
                _customLogger?.Append(runId, $"[{storyId}] {lastError}", "error");
                return (false, lastError);
            }

            if (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromSeconds(Math.Max(1, attempt * 2));
                _customLogger?.Append(runId, $"[{storyId}] Tentativo download {attempt}/{maxAttempts} fallito per {generatedFileName}. Retry in {delay.TotalSeconds:0}s...", "warn");
                await Task.Delay(delay, cancellationToken);
            }
        }

        var finalError = string.IsNullOrWhiteSpace(lastError)
            ? $"Impossibile scaricare {generatedFileName}"
            : lastError;
        _customLogger?.Append(runId, $"[{storyId}] {finalError}", "error");
        return (false, finalError);
    }

    /// <summary>
    /// Generates FX audio for a story using AudioCraft based on the 'fxDescription' fields in tts_schema.json.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> GenerateFxForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, null);
        var deleteCmd = new DeleteFxCommand(this);
        var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
        if (!cleanupOk)
        {
            return (false, cleanupMessage ?? "Impossibile cancellare gli effetti sonori esistenti");
        }
        
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            var (dispatcherSuccess, dispatcherMessage) = await GenerateFxAudioInternalAsync(context, dispatcherRunId);
            if (dispatcherSuccess)
            {
                // In batch-worker mode the status command wrapper may not run in-process:
                // advance status here as well to keep the chain coherent.
                TryChangeStatus(story.Id, "generate_fx_audio", dispatcherRunId);
            }
            return (dispatcherSuccess, dispatcherMessage);
        }
        var (success, message) = await StartFxAudioGenerationAsync(context);
        return (success, message);
    }

    // ==================== GENERATE MUSIC COMMAND ====================

    internal Task<(bool success, string? message)> StartMusicGenerationAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = $"music_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        _customLogger?.Append(runId, $"[{storyId}] Avvio generazione musica nella cartella {context.FolderPath}");

        _ = Task.Run(async () =>
        {
            try
            {
                var (success, message) = await GenerateMusicInternalAsync(context, runId);
                if (success)
                {
                    try
                    {
                        var story = GetStoryById(storyId);
                        if (story != null)
                        {
                            ApplyStatusTransitionWithCleanup(story, "music_generated", runId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Impossibile aggiornare lo stato della storia {Id}", storyId);
                        _customLogger?.Append(runId, $"[{storyId}] Aggiornamento stato fallito: {ex.Message}");
                    }
                }
                await (_customLogger?.MarkCompletedAsync(runId, message ?? (success ? "Generazione musica completata" : "Errore generazione musica"))
                    ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Errore non gestito durante la generazione musica per la storia {Id}", storyId);
                _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
                await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            }
        });

        return Task.FromResult<(bool success, string? message)>((true, $"Generazione musica avviata (run {runId}). Monitora i log per i dettagli."));
    }

    internal async Task<(bool success, string? message)> GenerateMusicInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");
        var maxMissingTolerated = Math.Max(0, _audioGenerationOptions?.CurrentValue?.Music?.MaxMissingSoundsTolerated ?? 2);

        // Step 1: Verify tts_schema.json exists
        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Get timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Step 2: Assign music files from sounds catalog (no generative fallback)
        var assigned = 0;
        var missing = 0;
        var musicRequests = new List<(JsonObject Entry, string Description)>();
        for (var i = 0; i < phraseEntries.Count; i++)
        {
            var entry = phraseEntries[i];
            var musicDesc = ReadString(entry, "music_description") ?? ReadString(entry, "musicDescription") ?? ReadString(entry, "MusicDescription");
            if (string.IsNullOrWhiteSpace(musicDesc))
                musicDesc = ReadString(entry, "music_tags") ?? ReadString(entry, "musicTags") ?? ReadString(entry, "MusicTags");
            if (string.IsNullOrWhiteSpace(musicDesc))
                continue;

            if (string.Equals(musicDesc.Trim(), "silence", StringComparison.OrdinalIgnoreCase))
            {
                entry["music_file"] = null;
                entry["musicFile"] = null;
                continue;
            }

            musicRequests.Add((entry, musicDesc));
        }

        var emitBatchProgress = IsBatchWorkerProcess();
        ReportCommandProgress(
            runId,
            0,
            musicRequests.Count,
            $"Music: preparazione ricerca suoni (chunk 0/{Math.Max(1, musicRequests.Count)})",
            emitBatchProgress);

        var requestCounter = 0;
        foreach (var (entry, musicDesc) in musicRequests)
        {
            requestCounter++;
            ReportCommandProgress(
                runId,
                requestCounter,
                musicRequests.Count,
                $"Music: ricerca/assegnazione suono chunk {requestCounter}/{musicRequests.Count}",
                emitBatchProgress);

            var currentMusicFile = ReadString(entry, "music_file") ?? ReadString(entry, "musicFile") ?? ReadString(entry, "MusicFile");
            var currentMusicSourcePath = ReadString(entry, "music_source_path") ?? ReadString(entry, "musicSourcePath") ?? ReadString(entry, "MusicSourcePath");
            if ((!string.IsNullOrWhiteSpace(currentMusicFile) && File.Exists(Path.Combine(folderPath, currentMusicFile))) ||
                (!string.IsNullOrWhiteSpace(currentMusicSourcePath) && File.Exists(currentMusicSourcePath)))
                continue; // already assigned and present
            var normalizedMusicPrompt = NormalizeMusicPromptForSearch(musicDesc);
            var musicTags = ReadMusicTags(entry);
            if (string.IsNullOrWhiteSpace(musicTags))
            {
                musicTags = ExtractMusicTagsFromDescription(musicDesc);
            }
            var musicDuration = ReadMusicPreferredDurationSeconds(entry);

            var selection = await TrySelectCatalogSoundForSchemaWithAutoSearchAsync(
                story,
                runId,
                soundType: "music",
                prompt: normalizedMusicPrompt,
                tags: musicTags,
                source: "tts_schema.music_description",
                preferredDurationSeconds: musicDuration,
                cancellationToken: context.CancellationToken).ConfigureAwait(false);

            if (selection == null)
            {
                missing++;
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Nessuna musica trovata per '{normalizedMusicPrompt}'. Continuo.");
                if (missing > maxMissingTolerated)
                {
                    return (false, $"Nessuna musica trovata per '{normalizedMusicPrompt}'. Superata soglia mancanti tollerati (mancanti={missing}, tollerati={maxMissingTolerated}).");
                }
                continue;
            }

            var localCopiedPath = EnsureStoryLocalCopyOfCatalogSound(folderPath, selection.SourcePath, story.Id, runId);
            var localFileName = Path.GetFileName(localCopiedPath);
            entry["music_file"] = localFileName;
            entry["musicFile"] = localFileName;
            entry["music_source_path"] = localCopiedPath;
            entry["musicSourcePath"] = localCopiedPath;
            entry["music_sound_id"] = selection.Sound.Id;
            entry["musicSoundId"] = selection.Sound.Id;
            assigned++;
        }

        // Step 3: Save updated schema
        try
        {
            SanitizeTtsSchemaTextFields(rootNode);
            var updated = rootNode.ToJsonString(SchemaJsonOptions);
            await File.WriteAllTextAsync(schemaPath, updated);
            // Mark story as having generated music (best-effort)
            try { _database.UpdateStoryGeneratedMusic(story.Id, true); } catch { }
        }
        catch (Exception ex)
        {
            var err = $"Impossibile aggiornare tts_schema.json: {ex.Message}";
            _logger?.LogError(ex, "Errore salvataggio schema TTS per la storia {Id}", story.Id);
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var successMsg = assigned > 0
            ? $"Musica assegnata da sounds ({assigned} voci in timeline, mancanti={missing})"
            : (missing > 0
                ? $"Nessuna musica assegnata da sounds (mancanti={missing})"
                : "Nessuna musica assegnata (nessuna richiesta)");
        ReportCommandProgress(
            runId,
            musicRequests.Count,
            musicRequests.Count,
            $"Music: completato ({musicRequests.Count}/{musicRequests.Count})",
            emitBatchProgress);
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    private string GetMusicLibraryFolderForStory(StoryRecord story)
    {
        if (story.SerieId.HasValue)
        {
            try
            {
                var serie = _database.GetSeriesById(story.SerieId.Value);
                var folder = (serie?.Folder ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    return Path.Combine(Directory.GetCurrentDirectory(), "series_folder", folder, "music");
                }
            }
            catch
            {
                // best-effort
            }

            // If story is in a series but folder is missing, still fall back to global folder.
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "data", "music_stories");
    }

    private static Dictionary<string, List<string>> BuildMusicLibraryIndex(List<string> files)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        map["any"] = new List<string>();

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file)) continue;
            map["any"].Add(file);

            var name = Path.GetFileNameWithoutExtension(file) ?? string.Empty;
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var sep = name.IndexOfAny(new[] { '_', '-' });
            var prefix = sep > 0 ? name.Substring(0, sep) : name;
            prefix = prefix.Trim();
            if (string.IsNullOrWhiteSpace(prefix)) continue;

            // Only index known music types that correspond to actual audio files; everything else goes into "any" only.
            if (!KnownMusicFileTypes.Contains(prefix))
                continue;

            if (!map.TryGetValue(prefix, out var list))
            {
                list = new List<string>();
                map[prefix] = list;
            }
            list.Add(file);
        }

        return map;
    }

    private static readonly HashSet<string> KnownMusicTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "opening",
        "ending",
        "transition",
        "mystery",
        "suspense",
        "tension",
        "love",
        "combat",
        "activity",
        "exploration",
        "victory",
        "defeat",
        "aftermath",
        "ambient",
        "silence"
    };

    private static readonly HashSet<string> KnownMusicFileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "opening",
        "ending",
        "transition",
        "mystery",
        "suspense",
        "tension",
        "love",
        "combat",
        "activity",
        "exploration",
        "victory",
        "defeat",
        "aftermath",
        "ambient"
    };

    private static string InferMusicTypeFromDescription(string description, IEnumerable<string> knownTypes)
    {
        if (string.IsNullOrWhiteSpace(description)) return "any";

        // If the description explicitly includes a known type, use it even if the library currently has no files for that type.
        foreach (var t in KnownMusicTypes)
        {
            if (Regex.IsMatch(description, $@"\b{Regex.Escape(t)}\b", RegexOptions.IgnoreCase))
                return t;
        }

        foreach (var t in knownTypes)
        {
            if (string.Equals(t, "any", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (Regex.IsMatch(description, $@"\b{Regex.Escape(t)}\b", RegexOptions.IgnoreCase))
                return t;
        }

        return "any";
    }

    
    private async Task<(bool success, List<string> files, string? error)> TryGenerateSeriesMusicLibraryAsync(
        StoryRecord story,
        Series serie,
        List<JsonObject> phraseEntries,
        string runId)
    {
        if (story == null || serie == null) return (false, new List<string>(), "Serie non disponibile");
        if (phraseEntries == null || phraseEntries.Count == 0) return (false, new List<string>(), "Nessuna richiesta musica in timeline");

        var sentiments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in phraseEntries)
        {
            var musicDesc = ReadString(entry, "music_description") ?? ReadString(entry, "musicDescription") ?? ReadString(entry, "MusicDescription");
            if (string.IsNullOrWhiteSpace(musicDesc)) continue;
            sentiments.Add(musicDesc.Trim());
        }

        if (sentiments.Count == 0)
        {
            return (false, new List<string>(), "Nessun sentimento musica trovato in timeline");
        }

        var folderBase = (serie.Folder ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(folderBase))
        {
            return (false, new List<string>(), "Folder serie non impostato");
        }

        var musicFolder = Path.Combine(Directory.GetCurrentDirectory(), "series_folder", folderBase, "music");
        Directory.CreateDirectory(musicFolder);

        var genre = (serie.Genere ?? string.Empty).Trim();
        var sub = (serie.Sottogenere ?? string.Empty).Trim();
        var genreCombo = string.Join("/", new[] { genre, sub }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(genreCombo))
        {
            genreCombo = "non specificata";
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var audioCraft = new AudioCraftTool(httpClient, forceCpu: false, logger: _customLogger);

        var generatedFiles = new List<string>();
        var sentimentIndex = 0;

        foreach (var sentiment in sentiments.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            sentimentIndex++;
            var prompt = $"una musica di sottofondo non invasiva per una serie {genreCombo} che esprima il sentimento {sentiment}";
            var durationSeconds = 30;

            _customLogger?.Append(runId, $"[{story.Id}] Generazione musica serie: '{sentiment}' ({durationSeconds}s)");

            var request = new
            {
                operation = "generate_music",
                prompt = prompt,
                duration = durationSeconds,
                model = "facebook/musicgen-small"
            };
            var requestJson = JsonSerializer.Serialize(request);
            string? resultJson = null;

            try
            {
                try
                {
                    resultJson = await audioCraft.ExecuteAsync(requestJson);
                }
                catch (Exception executeEx) when (executeEx.Message.Contains("Connection") || executeEx.Message.Contains("No connection") || executeEx is HttpRequestException)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Errore connessione AudioCraft: {executeEx.Message}. Tentativo riavvio...");
                    var restarted = await StartupTasks.TryRestartAudioCraftAsync(_healthMonitor, _logger);
                    if (!restarted)
                    {
                        return (false, new List<string>(), "Servizio AudioCraft non disponibile e impossibile riavviare");
                    }

                    _customLogger?.Append(runId, $"[{story.Id}] AudioCraft riavviato. Retry musica '{sentiment}'...");
                    resultJson = await audioCraft.ExecuteAsync(requestJson);
                }

                _customLogger?.Append(runId, $"[{story.Id}] AudioCraft music response: {resultJson?.Substring(0, Math.Min(500, resultJson?.Length ?? 0))}");

                string? generatedFileName = null;
                if (!string.IsNullOrWhiteSpace(audioCraft.LastGeneratedMusicFile))
                {
                    generatedFileName = audioCraft.LastGeneratedMusicFile;
                }
                if (string.IsNullOrWhiteSpace(generatedFileName) && !string.IsNullOrWhiteSpace(resultJson))
                {
                    try
                    {
                        var resultNode = JsonNode.Parse(resultJson) as JsonObject;
                        var resultField = resultNode?["result"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(resultField))
                        {
                            var inner = JsonNode.Parse(resultField) as JsonObject;
                            generatedFileName = inner?["file"]?.ToString()
                                ?? inner?["filename"]?.ToString()
                                ?? inner?["output"]?.ToString();
                        }
                    }
                    catch
                    {
                        // ignore parse errors
                    }
                }

                if (string.IsNullOrWhiteSpace(generatedFileName))
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Avviso: file musica non determinato per '{sentiment}'");
                    continue;
                }

                var safeSentiment = SlugifyFilePart(sentiment);
                var localFileName = $"music_{safeSentiment}_{sentimentIndex:D3}.wav";
                var localFilePath = Path.Combine(musicFolder, localFileName);

                try
                {
                    var downloadResponse = await httpClient.GetAsync($"http://localhost:8003/download/{generatedFileName}");
                    if (!downloadResponse.IsSuccessStatusCode)
                    {
                        _customLogger?.Append(runId, $"[{story.Id}] Avviso: download musica fallito per {generatedFileName}");
                        continue;
                    }

                    var audioBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(localFilePath, audioBytes);
                    generatedFiles.Add(localFilePath);

                    var promptFileName = Path.ChangeExtension(localFileName, ".txt");
                    var promptFilePath = Path.Combine(musicFolder, promptFileName);
                    await File.WriteAllTextAsync(promptFilePath, prompt);
                }
                catch (Exception ex)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Avviso: errore download musica {generatedFileName}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId, $"[{story.Id}] Errore generazione musica '{sentiment}': {ex.Message}");
            }
        }

        if (generatedFiles.Count == 0)
        {
            return (false, new List<string>(), "Generazione musica serie fallita");
        }

        return (true, generatedFiles, null);
    }

    private static string SlugifyFilePart(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "music";
        var cleaned = Regex.Replace(input.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_");
        cleaned = cleaned.Trim('_');
        return string.IsNullOrWhiteSpace(cleaned) ? "music" : cleaned;
    }

private static string? SelectMusicFileDeterministic(
        Dictionary<string, List<string>> index,
        string requestedType,
        HashSet<string> usedDestFileNames,
        long seedA,
        int seedB)
    {
        if (index == null || index.Count == 0) return null;

        List<string> candidates;
        if (!string.IsNullOrWhiteSpace(requestedType) && index.TryGetValue(requestedType, out var typed) && typed.Count > 0)
            candidates = typed;
        else if (index.TryGetValue("any", out var any) && any.Count > 0)
            candidates = any;
        else
            candidates = index.Values.FirstOrDefault(v => v.Count > 0) ?? new List<string>();

        if (candidates.Count == 0) return null;

        // Prefer unused within the episode/run.
        var unused = candidates
            .Where(f => !usedDestFileNames.Contains(Path.GetFileName(f) ?? string.Empty))
            .ToList();
        var pool = unused.Count > 0 ? unused : candidates;

        var seed = unchecked((int)(seedA ^ (seedA >> 32) ^ seedB));
        var rnd = new Random(seed);
        return pool[rnd.Next(pool.Count)];
    }

    /// <summary>
    /// Generates music for a story using AudioCraft MusicGen based on the 'musicDescription' fields in tts_schema.json.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> GenerateMusicForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, null);

        var deleteCmd = new DeleteMusicCommand(this);
        var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
        if (!cleanupOk)
        {
            return (false, cleanupMessage ?? "Impossibile cancellare la musica esistente");
        }
        
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            return await GenerateMusicInternalAsync(context, dispatcherRunId);
        }
        var (success, message) = await StartMusicGenerationAsync(context);
        return (success, message);
    }

    // ==================== MIX FINAL AUDIO COMMAND ====================

    internal async Task<(bool success, string? message)> StartMixFinalAudioAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = $"mixaudio_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        _customLogger?.Start(runId);
        _customLogger?.Append(runId, $"[{storyId}] Avvio mixaggio audio finale nella cartella {context.FolderPath}");

        try
        {
            var (success, message) = await MixFinalAudioInternalAsync(context, runId);

            if (success && context.TargetStatus?.Id > 0)
            {
                try
                {
                    _database.UpdateStoryById(storyId, statusId: context.TargetStatus.Id, updateStatus: true);
                    _customLogger?.Append(runId, $"[{storyId}] Stato aggiornato a {context.TargetStatus.Code ?? context.TargetStatus.Id.ToString()}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Impossibile aggiornare lo stato della storia {Id}", storyId);
                    _customLogger?.Append(runId, $"[{storyId}] Aggiornamento stato fallito: {ex.Message}");
                }
            }

            var completionMessage = message ?? (success ? "Mixaggio audio completato" : "Errore mixaggio audio");
            await (_customLogger?.MarkCompletedAsync(runId, completionMessage) ?? Task.CompletedTask);
            return (success, message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore non gestito durante il mixaggio audio per la storia {Id}", storyId);
            _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
            await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            return (false, $"Errore: {ex.Message}");
        }
    }

    private async Task<(bool success, string? message)> MixFinalAudioInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");

        // Step 1: Verify tts_schema.json exists
        if (!File.Exists(schemaPath))
        {
            var err = "File tts_schema.json non trovato: genera prima lo schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        JsonObject? rootNode;
        try
        {
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            var err = $"Impossibile leggere tts_schema.json: {ex.Message}";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        if (rootNode == null)
        {
            var err = "Formato tts_schema.json non valido";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Get timeline
        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            var err = "Timeline mancante nello schema TTS";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            var err = "La timeline non contiene frasi";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        _customLogger?.Append(runId, $"[{story.Id}] Trovate {phraseEntries.Count} frasi nella timeline");

        // Step 2: Check for missing TTS audio files
        var missingTts = new List<int>();
        var ttsFiles = new List<(int Index, string FileName, int StartMs, int DurationMs)>();
        
        for (int i = 0; i < phraseEntries.Count; i++)
        {
            var entry = phraseEntries[i];
            var fileName = ReadString(entry, "fileName") ?? ReadString(entry, "FileName") ?? ReadString(entry, "file_name");
            
            if (string.IsNullOrWhiteSpace(fileName))
            {
                missingTts.Add(i + 1);
                continue;
            }

            var filePath = Path.Combine(folderPath, fileName);
            if (!File.Exists(filePath))
            {
                missingTts.Add(i + 1);
                continue;
            }

            int startMs = 0;
            int durationMs = 0;
            if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                startMs = (int)s;
            if (TryReadNumber(entry, "durationMs", out var d) || TryReadNumber(entry, "DurationMs", out d) || TryReadNumber(entry, "duration_ms", out d))
                durationMs = (int)d;

            ttsFiles.Add((i, fileName, startMs, durationMs));
        }

        if (missingTts.Count > 0)
        {
            var err = $"File TTS mancanti per le frasi: {string.Join(", ", missingTts)}. Il mix richiede i file TTS esistenti per mantenere la sincronizzazione; genera prima i TTS.";
            _customLogger?.Append(runId, $"[{story.Id}] {err}");
            return (false, err);
        }

        // Step 3: Check for missing ambient sound files only when ambience is required.
        var ambientSoundsNeeded = phraseEntries.Any(HasAmbientSoundRequest);

        if (IsAmbienceRequiredForNextStatus() && ambientSoundsNeeded)
        {
            var missingAmbientSounds = phraseEntries.Where(e =>
            {
                var ambientSounds = ReadAmbientSoundsDescription(e);
                if (string.IsNullOrWhiteSpace(ambientSounds)) return false;

                var ambientSoundFile =
                    ReadString(e, "ambient_sound_file") ??
                    ReadString(e, "AmbientSoundFile") ??
                    ReadString(e, "ambientSoundFile") ??
                    ReadString(e, "ambientSoundsFile") ??
                    ReadString(e, "AmbientSoundsFile") ??
                    ReadString(e, "ambient_sounds_file");
                var ambientSourcePath =
                    ReadString(e, "ambient_sound_source_path") ??
                    ReadString(e, "ambientSoundSourcePath") ??
                    ReadString(e, "AmbientSoundSourcePath");
                if (string.IsNullOrWhiteSpace(ambientSoundFile))
                {
                    if (!string.IsNullOrWhiteSpace(ambientSourcePath) && File.Exists(ambientSourcePath))
                    {
                        return false;
                    }
                    // Keep mix check aligned with ambient generation:
                    // entries with ambient description but zero computed duration do not generate a wav.
                    if (TryReadNumber(e, "ambientSoundsDuration", out var ambientDur) ||
                        TryReadNumber(e, "AmbientSoundsDuration", out ambientDur) ||
                        TryReadNumber(e, "ambient_sounds_duration", out ambientDur))
                    {
                        return ambientDur > 0;
                    }
                    return true;
                }
                if (File.Exists(Path.Combine(folderPath, ambientSoundFile))) return false;
                if (!string.IsNullOrWhiteSpace(ambientSourcePath) && File.Exists(ambientSourcePath)) return false;
                return true;
            }).ToList();

            if (missingAmbientSounds.Count > 0)
            {
                var warn = $"[WARN] File ambient sound mancanti per alcune frasi (count: {missingAmbientSounds.Count}). Il mix prosegue con i soli suoni disponibili.";
                _customLogger?.Append(runId, $"[{story.Id}] {warn}");
            }
        }

        // Step 4: Check for missing FX audio files
        var fxNeeded = phraseEntries.Any(HasFxRequest);
        
        if (fxNeeded)
        {
            var missingFx = phraseEntries.Where(e =>
            {
                if (!HasFxRequest(e)) return false;
                var fxFile = ReadString(e, "fxFile") ?? ReadString(e, "FxFile");
                var fxSourcePath = ReadString(e, "fx_source_path") ?? ReadString(e, "fxSourcePath") ?? ReadString(e, "FxSourcePath");
                if (!string.IsNullOrWhiteSpace(fxFile) && File.Exists(Path.Combine(folderPath, fxFile))) return false;
                if (!string.IsNullOrWhiteSpace(fxSourcePath) && File.Exists(fxSourcePath)) return false;
                return true;
            }).ToList();

            if (missingFx.Count > 0)
            {
                var warn = $"[WARN] File FX mancanti per alcune frasi (count: {missingFx.Count}). Il mix prosegue con gli FX disponibili.";
                _customLogger?.Append(runId, $"[{story.Id}] {warn}");
            }
        }

        // Step 4.5: Check for missing music files and generate if needed (music can be regenerated)
        var musicNeeded = phraseEntries.Any(HasMusicRequest);
        if (musicNeeded)
        {
            var missingMusic = phraseEntries.Where(e =>
            {
                if (!HasMusicRequest(e)) return false;
                var musicFile = ReadString(e, "music_file");
                var musicSourcePath = ReadString(e, "music_source_path") ?? ReadString(e, "musicSourcePath") ?? ReadString(e, "MusicSourcePath");
                if (!string.IsNullOrWhiteSpace(musicFile) && File.Exists(Path.Combine(folderPath, musicFile))) return false;
                if (!string.IsNullOrWhiteSpace(musicSourcePath) && File.Exists(musicSourcePath)) return false;
                return true;
            }).ToList();

            if (missingMusic.Count > 0)
            {
                var warn = $"[WARN] File music mancanti per alcune frasi (count: {missingMusic.Count}). Il mix prosegue con la musica disponibile.";
                _customLogger?.Append(runId, $"[{story.Id}] {warn}");
            }
        }

        _customLogger?.Append(runId, $"[{story.Id}] Verifica audio completata. Avvio mixaggio con gli asset disponibili...");


        var ttsTrackFiles = new List<(string FilePath, int StartMs)>();
        var ambienceTrackFiles = new List<(string FilePath, int StartMs, int DurationMs)>();
        var fxTrackFiles = new List<(string FilePath, int StartMs)>();
        var musicTrackFiles = new List<(string FilePath, int StartMs, int DurationMs)>();

        // === OPENING INTRO (handled ONLY in mix, not in tts_schema.json) ===
        // 15s opening music + narrator announcement at +5s. Then shift the rest of the story by +15s.
        var introShiftMs = 0;
        try
        {
            var selectedOpening = SelectOpeningMusicFromSoundsCatalog(story.Id);
            if (!string.IsNullOrWhiteSpace(selectedOpening) && File.Exists(selectedOpening))
            {
                musicTrackFiles.Add((selectedOpening, 0, 15000));
                introShiftMs = 15000;

                // Narrator announcement
                var narratorVoice = (ReadString(rootNode, "narrator_voice_id")
                    ?? ReadString(rootNode, "narratorVoiceId")
                    ?? ReadString(rootNode, "narrator_voice")
                    ?? ReadString(rootNode, "narratorVoice")
                    ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(narratorVoice))
                {
                    narratorVoice = GetNarratorDefaultVoiceId() ?? string.Empty;
                }

                string? seriesTitle = null;
                if (story.SerieId.HasValue)
                {
                    try
                    {
                        seriesTitle = _database.GetSeriesById(story.SerieId.Value)?.Titolo;
                    }
                    catch
                    {
                        seriesTitle = null;
                    }
                }
                var episodeNumber = story.SerieEpisode;
                var storyTitle = (story.Title ?? string.Empty).Trim();
                string? episodeTitle = null;
                if (story.SerieId.HasValue && episodeNumber.HasValue)
                {
                    try
                    {
                        episodeTitle = _database.GetSeriesEpisodeBySerieAndNumber(story.SerieId.Value, episodeNumber.Value)?.Title;
                    }
                    catch
                    {
                        episodeTitle = null;
                    }
                }
                var titleAlreadyInTimeline = !string.IsNullOrWhiteSpace(storyTitle)
                    && TimelineContainsTitle(phraseEntries, storyTitle);
                var seriesAlreadyInTimeline = !string.IsNullOrWhiteSpace(seriesTitle)
                    && TimelineContainsTitle(phraseEntries, seriesTitle);
                var episodeAlreadyInTimeline = episodeNumber.HasValue && (
                    TimelineContainsTitle(phraseEntries, $"episodio {episodeNumber.Value}") ||
                    TimelineContainsTitle(phraseEntries, $"ep {episodeNumber.Value}") ||
                    TimelineContainsTitle(phraseEntries, $"ep{episodeNumber.Value}")
                );
                var openingAlreadyInTimeline = titleAlreadyInTimeline || seriesAlreadyInTimeline || episodeAlreadyInTimeline;

                if (!string.IsNullOrWhiteSpace(storyTitle) && _ttsService != null && !string.IsNullOrWhiteSpace(narratorVoice) && !openingAlreadyInTimeline)
                {
                    var announcement = BuildOpeningAnnouncement(seriesTitle, episodeNumber, storyTitle, episodeTitle);

                    var openingTtsFile = Path.Combine(folderPath, "tts_opening.wav");
                    var openingAnnouncementFile = Path.Combine(folderPath, "tts_opening.txt");
                    var mustRegenerateOpening = true;
                    if (File.Exists(openingTtsFile) && File.Exists(openingAnnouncementFile))
                    {
                        try
                        {
                            var savedAnnouncement = (await File.ReadAllTextAsync(openingAnnouncementFile)).Trim();
                            mustRegenerateOpening = !string.Equals(savedAnnouncement, announcement, StringComparison.Ordinal);
                        }
                        catch
                        {
                            mustRegenerateOpening = true;
                        }
                    }

                    if (mustRegenerateOpening)
                    {
                        var ttsSafe = ProsodyNormalizer.NormalizeForTTS(announcement);
                        var narratorProvider = ResolveVoiceProvider(narratorVoice);
                        var ttsResult = await _ttsService.SynthesizeAsync(narratorVoice, ttsSafe, provider: narratorProvider);
                        if (ttsResult != null && !string.IsNullOrWhiteSpace(ttsResult.AudioBase64))
                        {
                            var audioBytes = Convert.FromBase64String(ttsResult.AudioBase64);
                            await File.WriteAllBytesAsync(openingTtsFile, audioBytes);
                            await File.WriteAllTextAsync(openingAnnouncementFile, announcement);
                        }
                    }

                    if (File.Exists(openingTtsFile))
                    {
                        ttsTrackFiles.Add((openingTtsFile, 5000));
                    }
                }
            }
            else
            {
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Nessuna opening music trovata in sounds (type=music). Intro senza musica.");
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Opening intro fallito: {ex.Message}");
        }

        var mixGapOptions = _audioMixOptions?.CurrentValue ?? new AudioMixOptions();
        var maxSilenceGapMs = (int)Math.Round(Math.Max(0d, mixGapOptions.MaxSilenceGapSeconds) * 1000d);
        var defaultPhraseGapMs = Math.Max(0, mixGapOptions.DefaultPhraseGapMs);
        var commaAttributionGapMs = Math.Clamp(mixGapOptions.CommaAttributionGapMs, 0, Math.Max(0, defaultPhraseGapMs));
        if (maxSilenceGapMs > 0)
        {
            defaultPhraseGapMs = Math.Min(defaultPhraseGapMs, maxSilenceGapMs);
            commaAttributionGapMs = Math.Min(commaAttributionGapMs, maxSilenceGapMs);
        }

        // Accoda il resto della storia in sequenza reale:
        // start di ogni frase = fine frase precedente + gap dinamico.
        int currentTimeMs = introShiftMs;
        int wavDurationUsedCount = 0;
        var subtitleTimings = new List<FinalMixSubtitleTiming>();
        for (int entryIndex = 0; entryIndex < phraseEntries.Count; entryIndex++)
        {
            var entry = phraseEntries[entryIndex];
            var hasScheduledPhraseTiming = false;
            var scheduledPhraseStartMs = 0;
            var scheduledPhraseDurationMs = 0;
            // TTS file
            var ttsFileName = ReadString(entry, "fileName") ?? ReadString(entry, "FileName") ?? ReadString(entry, "file_name");
            if (!string.IsNullOrWhiteSpace(ttsFileName))
            {
                var ttsFilePath = Path.Combine(folderPath, ttsFileName);
                if (File.Exists(ttsFilePath))
                {
                    int startMs = currentTimeMs;
                    int durationMs = 0;
                    var wavDuration = TryGetWavDurationFromFile(ttsFilePath);
                    if (wavDuration.HasValue && wavDuration.Value > 0)
                    {
                        durationMs = wavDuration.Value;
                        wavDurationUsedCount++;
                    }
                    else if (TryReadNumber(entry, "durationMs", out var d) ||
                             TryReadNumber(entry, "DurationMs", out d) ||
                             TryReadNumber(entry, "duration_ms", out d))
                    {
                        durationMs = (int)d;
                    }

                    if (durationMs <= 0)
                    {
                        durationMs = TryGetAudioDurationMsFromFile(ttsFilePath) ?? 0;
                        if (durationMs > 0)
                        {
                            wavDurationUsedCount++;
                        }
                    }
                    if (durationMs <= 0)
                        durationMs = 2000; // fallback gap if duration is unavailable

                    ttsTrackFiles.Add((ttsFilePath, startMs));
                    var phraseText = (ReadString(entry, "text") ?? ReadString(entry, "Text") ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(phraseText))
                    {
                        subtitleTimings.Add(new FinalMixSubtitleTiming
                        {
                            Index = entryIndex,
                            StartMs = Math.Max(0, startMs),
                            EndMs = Math.Max(startMs, startMs + durationMs),
                            Text = phraseText
                        });
                    }
                    hasScheduledPhraseTiming = true;
                    scheduledPhraseStartMs = Math.Max(0, startMs);
                    scheduledPhraseDurationMs = Math.Max(0, durationMs);
                    var nextEntry = entryIndex + 1 < phraseEntries.Count ? phraseEntries[entryIndex + 1] : null;
                    var gapAfterMs = ComputePhraseGapMs(entry, nextEntry, defaultPhraseGapMs, commaAttributionGapMs);
                    currentTimeMs = startMs + durationMs + gapAfterMs;
                }
            }

            // Ambient sounds are handled as continuous segments (from one [RUMORI] tag until the next)

            // FX file - starts at middle of phrase duration
            var fxFile = ReadString(entry, "fxFile") ?? ReadString(entry, "fx_file") ?? ReadString(entry, "FxFile");
            var fxSourcePath = ReadString(entry, "fx_source_path") ?? ReadString(entry, "fxSourcePath") ?? ReadString(entry, "FxSourcePath");
            var fxFilePath = ResolveSchemaAudioReferencePath(folderPath, fxFile, fxSourcePath);
            if (!string.IsNullOrWhiteSpace(fxFilePath))
            {
                if (File.Exists(fxFilePath))
                {
                    int startMs;
                    int phraseDurationMs;
                    if (hasScheduledPhraseTiming)
                    {
                        startMs = scheduledPhraseStartMs;
                        phraseDurationMs = scheduledPhraseDurationMs;
                    }
                    else
                    {
                        startMs = introShiftMs;
                        phraseDurationMs = 0;
                        if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                            startMs = (int)s + introShiftMs;
                        if (TryReadNumber(entry, "durationMs", out var d) || TryReadNumber(entry, "DurationMs", out d) || TryReadNumber(entry, "duration_ms", out d))
                            phraseDurationMs = (int)d;
                    }
                    int fxStartMs = startMs + (phraseDurationMs / 2);
                    fxTrackFiles.Add((fxFilePath, fxStartMs));
                }
            }

            // Music file - starts 2 seconds after phrase start
            var musicFile = ReadString(entry, "musicFile") ?? ReadString(entry, "music_file") ?? ReadString(entry, "MusicFile");
            var musicSourcePath = ReadString(entry, "music_source_path") ?? ReadString(entry, "musicSourcePath") ?? ReadString(entry, "MusicSourcePath");
            var musicFilePath = ResolveSchemaAudioReferencePath(folderPath, musicFile, musicSourcePath);
            if (!string.IsNullOrWhiteSpace(musicFilePath))
            {
                if (File.Exists(musicFilePath))
                {
                    int startMs;
                    if (hasScheduledPhraseTiming)
                    {
                        startMs = scheduledPhraseStartMs;
                    }
                    else
                    {
                        startMs = introShiftMs;
                        if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                            startMs = (int)s + introShiftMs;
                    }
                    int durationMs = 20000; // default 20s for music
                    if (TryReadNumber(entry, "musicDuration", out var md) || TryReadNumber(entry, "MusicDuration", out md) || TryReadNumber(entry, "music_duration", out md))
                        durationMs = (int)md * 1000;
                    int musicStartMs = startMs + 2000;
                    if (TryReadNumber(entry, "musicStartMs", out var msOverride))
                    {
                        musicStartMs = (int)msOverride + introShiftMs;
                    }
                    musicTrackFiles.Add((musicFilePath, musicStartMs, durationMs));
                }
            }
        }

        if (wavDurationUsedCount > 0)
        {
            _customLogger?.Append(
                runId,
                $"[{story.Id}] Timeline TTS sequenziale applicata: durate reali file={wavDurationUsedCount}, gapDefault={defaultPhraseGapMs}ms, gapCommaAttribution={commaAttributionGapMs}ms, maxSilenceGap={maxSilenceGapMs}ms");
        }
        var subtitleTimingByIndex = subtitleTimings
            .GroupBy(t => t.Index)
            .ToDictionary(g => g.Key, g => g.First());

        // Build ambience segments based on ambient_sound_file definitions and ambientSoundsDuration
        try
        {
            for (int i = 0; i < phraseEntries.Count; i++)
            {
                var e = phraseEntries[i];
                var ambientFile =
                    ReadString(e, "ambient_sound_file") ??
                    ReadString(e, "AmbientSoundFile") ??
                    ReadString(e, "ambientSoundFile") ??
                    ReadString(e, "ambientSoundsFile") ??
                    ReadString(e, "AmbientSoundsFile") ??
                    ReadString(e, "ambient_sounds_file");
                var ambientSourcePath =
                    ReadString(e, "ambient_sound_source_path") ??
                    ReadString(e, "ambientSoundSourcePath") ??
                    ReadString(e, "AmbientSoundSourcePath");

                if (string.IsNullOrWhiteSpace(ambientFile)) continue;

                int startMs = 0;
                if (subtitleTimingByIndex.TryGetValue(i, out var timing))
                {
                    startMs = timing.StartMs;
                }
                else if (TryReadNumber(e, "startMs", out var s) || TryReadNumber(e, "StartMs", out s) || TryReadNumber(e, "start_ms", out s))
                {
                    startMs = (int)s + introShiftMs;
                }
                else
                {
                    startMs = introShiftMs;
                }
                var durationMs = ReadAmbientSoundsDurationMs(e);
                if (durationMs <= 0) continue;

                var fullPath = ResolveSchemaAudioReferencePath(folderPath, ambientFile, ambientSourcePath) ?? string.Empty;
                if (!File.Exists(fullPath))
                {
                    _customLogger?.Append(runId, $"[{story.Id}] [WARN] File ambient sound NON TROVATO: {fullPath}");
                    continue;
                }

                ambienceTrackFiles.Add((fullPath, startMs, durationMs));
                _customLogger?.Append(runId, $"[{story.Id}] Ambient segment: {(string.IsNullOrWhiteSpace(ambientFile) ? Path.GetFileName(fullPath) : ambientFile)} @ {startMs}ms for {durationMs}ms");
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Errore costruendo segmenti ambience: {ex.Message}");
        }

        // === ENDING OUTRO (handled ONLY in mix, not in tts_schema.json) ===
        // Append 20s ending music at the end of the story.
        try
        {
            var musicLibraryFolder = GetMusicLibraryFolderForStory(story);
            if (!string.IsNullOrWhiteSpace(musicLibraryFolder) && Directory.Exists(musicLibraryFolder))
            {
                var libraryFiles = Directory
                    .GetFiles(musicLibraryFolder)
                    .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var index = BuildMusicLibraryIndex(libraryFiles);
                var selectedEnding = SelectMusicFileDeterministic(index, requestedType: "ending", usedDestFileNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase), seedA: story.Id, seedB: -200);
                if (!string.IsNullOrWhiteSpace(selectedEnding) && File.Exists(selectedEnding))
                {
                    musicTrackFiles.Add((selectedEnding, currentTimeMs, 20000));
                    currentTimeMs += 20000;
                }
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Ending outro fallito: {ex.Message}");
        }

        _customLogger?.Append(runId, $"[{story.Id}] [DEBUG] Riepilogo file raccolti: {ttsTrackFiles.Count} TTS, {ambienceTrackFiles.Count} ambience, {fxTrackFiles.Count} FX, {musicTrackFiles.Count} music");

        // Step 6: Generate ffmpeg command for mixing
        var outputFile = Path.Combine(folderPath, "final_mix.wav");
        var (ffmpegSuccess, ffmpegError) = await MixAudioWithFfmpegAsync(
            folderPath, 
            ttsTrackFiles, 
            ambienceTrackFiles, 
            fxTrackFiles, 
            musicTrackFiles,
            outputFile, 
            runId,
            story.Id);

        if (!ffmpegSuccess)
        {
            return (false, ffmpegError);
        }

        // Post-process ONLY on final mixed file: trim overlong silences without touching intermediate track sync.
        var finalSilenceOptions = _audioMixOptions?.CurrentValue ?? new AudioMixOptions();
        if (finalSilenceOptions.EnableFinalSilenceTrim)
        {
            var trimResult = await ApplyFinalSilenceTrimAsync(outputFile, runId, story.Id, finalSilenceOptions);
            if (!trimResult.success)
            {
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Trim silenzi finali non applicato: {trimResult.error}");
            }
        }

        if (subtitleTimings.Count > 0 && finalSilenceOptions.EnableFinalSilenceTrim)
        {
            var maxGapMs = (int)Math.Round(Math.Max(0d, finalSilenceOptions.FinalSilenceTrimMaxGapSeconds) * 1000d);
            var keepMs = (int)Math.Round(Math.Clamp(finalSilenceOptions.FinalSilenceTrimKeepSeconds, 0d, Math.Max(0d, finalSilenceOptions.FinalSilenceTrimMaxGapSeconds)) * 1000d);
            if (maxGapMs > 0)
            {
                var (mapTimeMs, cuts, removedTotalMs) = BuildSilenceTrimMapFromSubtitleTimings(subtitleTimings, maxGapMs, keepMs);
                if (cuts > 0)
                {
                    foreach (var timing in subtitleTimings)
                    {
                        var mappedStart = mapTimeMs(timing.StartMs);
                        var mappedEnd = mapTimeMs(timing.EndMs);
                        timing.StartMs = Math.Max(0, mappedStart);
                        timing.EndMs = Math.Max(timing.StartMs, mappedEnd);
                    }
                    _customLogger?.Append(runId, $"[{story.Id}] Timeline sottotitoli mix compensata: {cuts} tagli, {removedTotalMs}ms rimossi.");
                }
            }
        }

        if (subtitleTimings.Count > 0)
        {
            try
            {
                var subtitleTimelinePath = Path.Combine(folderPath, "final_mix_subtitle_timeline.json");
                var payload = new
                {
                    generated_at_utc = DateTime.UtcNow,
                    story_id = story.Id,
                    intro_shift_ms = introShiftMs,
                    entries = subtitleTimings
                        .OrderBy(t => t.Index)
                        .Select(t => new
                        {
                            index = t.Index,
                            start_ms = t.StartMs,
                            end_ms = t.EndMs,
                            duration_ms = Math.Max(0, t.EndMs - t.StartMs),
                            text = t.Text
                        })
                };
                await File.WriteAllTextAsync(
                    subtitleTimelinePath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
                _customLogger?.Append(runId, $"[{story.Id}] Timeline sottotitoli salvata: {Path.GetFileName(subtitleTimelinePath)} ({subtitleTimings.Count} frasi)");
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Impossibile salvare final_mix_subtitle_timeline.json: {ex.Message}");
            }
        }

        // Also create mp3 version
        var mp3OutputFile = Path.Combine(folderPath, "final_mix.mp3");
        try
        {
            var mp3Process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -i \"{outputFile}\" -codec:a libmp3lame -q:a 0 -b:a 320k \"{mp3OutputFile}\"",  // q:a 0 = massima qualit� VBR, -b:a 320k = bitrate massimo
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            mp3Process.Start();
            
            // Read streams asynchronously and wait with timeout
            var mp3StderrTask = mp3Process.StandardError.ReadToEndAsync();
            var mp3StdoutTask = mp3Process.StandardOutput.ReadToEndAsync();
            
            using var mp3Cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await mp3Process.WaitForExitAsync(mp3Cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { mp3Process.Kill(true); } catch { }
                _customLogger?.Append(runId, $"[{story.Id}] Timeout durante creazione MP3");
            }
            
            await mp3StderrTask;
            await mp3StdoutTask;
            
            if (mp3Process.ExitCode == 0)
            {
                _customLogger?.Append(runId, $"[{story.Id}] File MP3 creato: final_mix.mp3");
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] Avviso: impossibile creare MP3: {ex.Message}");
        }

        var successMsg = $"Mixaggio audio completato: final_mix.wav ({ttsTrackFiles.Count} voci, {ambienceTrackFiles.Count} ambience, {fxTrackFiles.Count} FX)";
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        // Aggiorna lo stato della storia a `audio_master_generated` quando il mix finale e stato creato
        try
        {
            var audioMasterStatus = GetStoryStatusByCode("audio_master_generated");
            if (audioMasterStatus != null)
            {
                var updated = _database.UpdateStoryById(story.Id, statusId: audioMasterStatus.Id, updateStatus: true);
                if (updated)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Stato aggiornato a {audioMasterStatus.Code}");
                }
                else
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Aggiornamento stato fallito: UpdateStoryById returned false");
                }
            }
            else
            {
                _customLogger?.Append(runId, $"[{story.Id}] Avviso: status code 'audio_master_generated' non trovato nel DB");
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] Errore aggiornamento stato audio master: {ex.Message}");
        }
        try { _database.UpdateStoryGeneratedMixedAudio(story.Id, true); } catch { }
        return (true, successMsg);
    }

    private async Task<(bool success, string? error)> MixAudioWithFfmpegAsync(
        string folderPath,
        List<(string FilePath, int StartMs)> ttsFiles,
        List<(string FilePath, int StartMs, int DurationMs)> ambienceFiles,
        List<(string FilePath, int StartMs)> fxFiles,
        List<(string FilePath, int StartMs, int DurationMs)> musicFiles,
        string outputFile,
        string runId,
        long storyId)
    {
        var mixOptions = _audioMixOptions?.CurrentValue ?? new AudioMixOptions();
        var voiceScale = NormalizeMixScale(mixOptions.VoiceVolume, defaultValue: 7);
        var ambienceScale = NormalizeMixScale(mixOptions.BackgroundSoundsVolume, defaultValue: 3);
        var fxScale = NormalizeMixScale(mixOptions.FxSourdsVolume, defaultValue: 6);
        var musicScale = NormalizeMixScale(mixOptions.MusicVolume, defaultValue: 3);

        if (voiceScale <= 0)
        {
            ttsFiles = new List<(string FilePath, int StartMs)>();
        }
        if (ambienceScale <= 0)
        {
            ambienceFiles = new List<(string FilePath, int StartMs, int DurationMs)>();
        }
        if (fxScale <= 0)
        {
            fxFiles = new List<(string FilePath, int StartMs)>();
        }
        if (musicScale <= 0)
        {
            musicFiles = new List<(string FilePath, int StartMs, int DurationMs)>();
        }

        // NEW STRATEGY: Create 3 separate tracks, then mix them
        // Track 1: TTS voices (may require batching if >50 files)
        // Track 2: Ambience + FX (usually few files, no batching needed)
        // Track 3: Music (usually few files, no batching needed)
        // Final: Mix the 3 tracks together
        
        _customLogger?.Append(runId, $"[{storyId}] Riepilogo: {ttsFiles.Count} TTS, {ambienceFiles.Count} ambience, {fxFiles.Count} FX, {musicFiles.Count} music");
        
        try
        {
        var trackFiles = new List<string>();
        
        // ===== TRACK 1: TTS VOICES =====
        var ttsTrackFile = Path.Combine(folderPath, $"track_tts_{storyId}.wav");
        if (ttsFiles.Count > 0 && voiceScale > 0)
        {
            _customLogger?.Append(runId, $"[{storyId}] Generazione traccia TTS...");
            var ttsResult = await CreateTtsTrackAsync(folderPath, ttsFiles, ttsTrackFile, runId, storyId, voiceScale);
            if (!ttsResult.success)
            {
                return ttsResult;
            }
            trackFiles.Add(ttsTrackFile);
        }
        
        // ===== TRACK 2: AMBIENCE + FX =====
        var ambienceFxTrackFile = Path.Combine(folderPath, $"track_ambience_fx_{storyId}.wav");
        if ((ambienceFiles.Count > 0 || fxFiles.Count > 0) && (ambienceScale > 0 || fxScale > 0))
        {
            _customLogger?.Append(runId, $"[{storyId}] Generazione traccia ambience+FX...");
            var ambienceFxResult = await CreateAmbienceFxTrackAsync(
                folderPath,
                ambienceFiles,
                fxFiles,
                ambienceFxTrackFile,
                runId,
                storyId,
                lowerAmbienceBecauseMusic: musicFiles.Count > 0,
                ambienceScale,
                fxScale);
            if (!ambienceFxResult.success)
            {
                TryDeleteFile(ttsTrackFile);
                return ambienceFxResult;
            }
            trackFiles.Add(ambienceFxTrackFile);
        }
        
        // ===== TRACK 3: MUSIC =====
        var musicTrackFile = Path.Combine(folderPath, $"track_music_{storyId}.wav");
        if (musicFiles.Count > 0 && musicScale > 0)
        {
            _customLogger?.Append(runId, $"[{storyId}] Generazione traccia music...");
            var musicResult = await CreateMusicTrackAsync(folderPath, musicFiles, musicTrackFile, runId, storyId, musicScale);
            if (!musicResult.success)
            {
                TryDeleteFile(ttsTrackFile);
                TryDeleteFile(ambienceFxTrackFile);
                return musicResult;
                }
                trackFiles.Add(musicTrackFile);
            }
            
            // ===== FINAL MIX: Combine the 3 tracks =====
            if (trackFiles.Count == 0)
            {
                return (false, "Nessuna traccia audio da mixare");
            }
            
            if (trackFiles.Count == 1)
            {
                // Solo una traccia, copia diretta
                File.Copy(trackFiles[0], outputFile, overwrite: true);
                TryDeleteFile(trackFiles[0]);
            }
            else
            {
                _customLogger?.Append(runId, $"[{storyId}] Mix finale di {trackFiles.Count} tracce...");
                var finalResult = await MixFinalTracksAsync(
                    trackFiles,
                    outputFile,
                    runId,
                    storyId,
                    mixOptions.VoiceVolume,
                    mixOptions.BackgroundSoundsVolume,
                    mixOptions.FxSourdsVolume,
                    mixOptions.MusicVolume);
                
                // Cleanup track files
                foreach (var track in trackFiles)
                {
                    TryDeleteFile(track);
                }
                
                if (!finalResult.success)
                {
                    return finalResult;
                }
            }
            
            if (!File.Exists(outputFile))
            {
                return (false, "ffmpeg non ha creato il file di output");
            }

            var fileInfo = new FileInfo(outputFile);
            _customLogger?.Append(runId, $"[{storyId}] File finale creato: {Path.GetFileName(outputFile)} ({fileInfo.Length / 1024} KB)");
            return (true, null);
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{storyId}] Eccezione durante il mix: {ex.Message}");
            return (false, $"Eccezione durante il mix: {ex.Message}");
        }
    }

    private static double NormalizeMixScale(double value, double defaultValue)
    {
        if (defaultValue <= 0)
        {
            return 0;
        }

        if (value <= 0)
        {
            return 0;
        }

        return value / defaultValue;
    }

    private async Task<(bool success, string? error)> ApplyFinalSilenceTrimAsync(
        string finalMixFilePath,
        string runId,
        long storyId,
        AudioMixOptions options)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(finalMixFilePath) || !File.Exists(finalMixFilePath))
            {
                return (false, "File final_mix.wav non trovato");
            }

            var maxGapSeconds = Math.Max(0d, options.FinalSilenceTrimMaxGapSeconds);
            if (maxGapSeconds <= 0d)
            {
                return (true, null);
            }

            var keepSeconds = Math.Clamp(options.FinalSilenceTrimKeepSeconds, 0d, maxGapSeconds);
            var thresholdDb = options.FinalSilenceTrimThresholdDb;
            if (!double.IsFinite(thresholdDb))
            {
                thresholdDb = -42d;
            }
            if (thresholdDb > 0d)
            {
                thresholdDb = -thresholdDb;
            }

            var tempOutput = Path.Combine(
                Path.GetDirectoryName(finalMixFilePath) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(finalMixFilePath)}_trimmed{Path.GetExtension(finalMixFilePath)}");

            var filter = string.Format(
                CultureInfo.InvariantCulture,
                "silenceremove=stop_periods=-1:stop_duration={0:0.###}:stop_threshold={1:0.###}dB:stop_silence={2:0.###}",
                maxGapSeconds,
                thresholdDb,
                keepSeconds);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -i \"{finalMixFilePath}\" -af \"{filter}\" \"{tempOutput}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                TryDeleteFile(tempOutput);
                return (false, "Timeout ffmpeg durante trim silenzi finali");
            }

            var stderr = await stderrTask;
            await stdoutTask;

            if (process.ExitCode != 0)
            {
                TryDeleteFile(tempOutput);
                var err = string.IsNullOrWhiteSpace(stderr) ? $"ffmpeg exit code {process.ExitCode}" : stderr;
                return (false, err);
            }

            if (!File.Exists(tempOutput))
            {
                return (false, "ffmpeg non ha creato il file trimmed");
            }

            File.Copy(tempOutput, finalMixFilePath, overwrite: true);
            TryDeleteFile(tempOutput);

            _customLogger?.Append(
                runId,
                $"[{storyId}] Trim silenzi finali applicato (maxGap={maxGapSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s, keep={keepSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s, threshold={thresholdDb.ToString("0.###", CultureInfo.InvariantCulture)}dB)");

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
    
    private async Task<(bool success, string? error)> CreateTtsTrackAsync(
        string folderPath,
        List<(string FilePath, int StartMs)> ttsFiles,
        string outputFile,
        string runId,
        long storyId,
        double volumeScale)
    {
        const int MaxInputsPerBatch = 50;
        
        if (ttsFiles.Count <= MaxInputsPerBatch)
        {
            // Direct mix - all TTS files fit in one command
            var inputArgs = new StringBuilder();
            var filterArgs = new StringBuilder();
            var labels = new List<string>();
            
            for (int i = 0; i < ttsFiles.Count; i++)
            {
                var (filePath, startMs) = ttsFiles[i];
                inputArgs.Append($" -i \"{filePath}\"");
                var label = $"tts{i}";
                filterArgs.Append($"[{i}]adelay={startMs}|{startMs},volume={volumeScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}[{label}];");
                labels.Add($"[{label}]");
            }
            
            foreach (var lbl in labels) filterArgs.Append(lbl);
            filterArgs.Append($"amix=inputs={labels.Count}:duration=longest:normalize=0[out]");
            
            var filterFile = Path.Combine(folderPath, $"filter_tts_{storyId}.txt");
            await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
            
            var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
            TryDeleteFile(filterFile);
            return result;
        }
        else
        {
            // Batch processing for many TTS files
            _customLogger?.Append(runId, $"[{storyId}] TTS: batching {ttsFiles.Count} files...");
            
            var batchFiles = new List<string>();
            for (int i = 0; i < ttsFiles.Count; i += MaxInputsPerBatch)
            {
                var batch = ttsFiles.Skip(i).Take(MaxInputsPerBatch).ToList();
                var batchFile = Path.Combine(folderPath, $"batch_tts_{storyId}_{i / MaxInputsPerBatch}.wav");
                
                var batchResult = await CreateTtsBatchAsync(
                    folderPath,
                    batch,
                    batchFile,
                    runId,
                    storyId,
                    i / MaxInputsPerBatch,
                    volumeScale);
                if (!batchResult.success)
                {
                    foreach (var f in batchFiles) TryDeleteFile(f);
                    return batchResult;
                }
                batchFiles.Add(batchFile);
            }
            
            // Merge all batches
            var mergeResult = await MergeAudioFilesAsync(batchFiles, outputFile, runId, storyId);
            foreach (var f in batchFiles) TryDeleteFile(f);
            return mergeResult;
        }
    }
    
    private async Task<(bool success, string? error)> CreateTtsBatchAsync(
        string folderPath,
        List<(string FilePath, int StartMs)> files,
        string outputFile,
        string runId,
        long storyId,
        int batchIndex,
        double volumeScale)
    {
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        var labels = new List<string>();
        
        _customLogger?.Append(runId, $"[{storyId}] TTS batch {batchIndex}: {files.Count} files");
        
        for (int i = 0; i < files.Count; i++)
        {
            var (filePath, startMs) = files[i];
            inputArgs.Append($" -i \"{filePath}\"");
            var label = $"t{i}";
            filterArgs.Append($"[{i}]adelay={startMs}|{startMs},volume={volumeScale.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}[{label}];");
            labels.Add($"[{label}]");
        }
        
        foreach (var lbl in labels) filterArgs.Append(lbl);
        filterArgs.Append($"amix=inputs={labels.Count}:duration=longest:normalize=0[out]");
        
        var filterFile = Path.Combine(folderPath, $"filter_tts_batch_{storyId}_{batchIndex}.txt");
        await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
        TryDeleteFile(filterFile);
        return result;
    }
    
    private async Task<(bool success, string? error)> CreateAmbienceFxTrackAsync(
        string folderPath,
        List<(string FilePath, int StartMs, int DurationMs)> ambienceFiles,
        List<(string FilePath, int StartMs)> fxFiles,
        string outputFile,
        string runId,
        long storyId,
        bool lowerAmbienceBecauseMusic,
        double ambienceScale,
        double fxScale)
    {
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        var labels = new List<string>();
        int idx = 0;
        var tempLoopedFiles = new List<string>();

        // If there's music in the final mix, keep ambience (rumori) lower so it doesn't fight the music bed.
        var ambienceBase = lowerAmbienceBecauseMusic ? 0.18 : 0.30;
        var ambienceVolume = ambienceBase * ambienceScale;
        
        // Add ambience with timing and volume. If the file is shorter than needed, loop it and trim.
        foreach (var (filePath, startMs, durationMs) in ambienceFiles)
        {
            if (ambienceScale <= 0)
            {
                break;
            }
            string inputFile = filePath;
            if (durationMs > 0)
            {
                try
                {
                    var wavBytes = await File.ReadAllBytesAsync(filePath);
                    var srcDurationMs = TryGetWavDuration(wavBytes) ?? 0;
                    if (srcDurationMs > 0 && durationMs > srcDurationMs)
                    {
                        var loopedFile = Path.Combine(folderPath, $"ambience_loop_{storyId}_{idx:D3}.wav");
                        var loopResult = await CreateLoopedAmbienceFileAsync(filePath, loopedFile, durationMs, runId, storyId);
                        if (loopResult.success && File.Exists(loopedFile))
                        {
                            inputFile = loopedFile;
                            tempLoopedFiles.Add(loopedFile);
                        }
                    }
                }
                catch
                {
                    // best-effort: if duration check fails, fall back to stream_loop
                }
            }

            if (string.Equals(inputFile, filePath, StringComparison.OrdinalIgnoreCase))
            {
                // Loop this input indefinitely, then atrim to requested duration.
                inputArgs.Append($" -stream_loop -1 -i \"{inputFile}\"");
            }
            else
            {
                inputArgs.Append($" -i \"{inputFile}\"");
            }
            var label = $"amb{idx}";
            var endSeconds = (durationMs / 1000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            filterArgs.Append($"[{idx}]atrim=start=0:end={endSeconds},adelay={startMs}|{startMs},volume={ambienceVolume.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}[{label}];");
            labels.Add($"[{label}]");
            idx++;
        }
        
        // Add FX with timing and volume
        foreach (var (filePath, startMs) in fxFiles)
        {
            if (fxScale <= 0)
            {
                break;
            }
            inputArgs.Append($" -i \"{filePath}\"");
            var label = $"fx{idx}";
            var fxVolume = 0.90 * fxScale;
            filterArgs.Append($"[{idx}]adelay={startMs}|{startMs},volume={fxVolume.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}[{label}];");
            labels.Add($"[{label}]");
            idx++;
        }
        
        if (labels.Count == 0)
        {
            return (false, "Nessun file ambience/FX");
        }
        
        foreach (var lbl in labels) filterArgs.Append(lbl);
        filterArgs.Append($"amix=inputs={labels.Count}:duration=longest:normalize=0[out]");
        
        var filterFile = Path.Combine(folderPath, $"filter_ambience_fx_{storyId}.txt");
        await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
        TryDeleteFile(filterFile);
        foreach (var tmp in tempLoopedFiles) TryDeleteFile(tmp);
        return result;
    }

    private async Task<(bool success, string? error)> CreateLoopedAmbienceFileAsync(
        string inputFile,
        string outputFile,
        int durationMs,
        string runId,
        long storyId)
    {
        try
        {
            var durationSeconds = (durationMs / 1000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            var process = new Process();
            process.StartInfo.FileName = "ffmpeg";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add("-stream_loop");
            process.StartInfo.ArgumentList.Add("-1");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(inputFile);
            process.StartInfo.ArgumentList.Add("-t");
            process.StartInfo.ArgumentList.Add(durationSeconds);
            process.StartInfo.ArgumentList.Add("-ac");
            process.StartInfo.ArgumentList.Add("2");
            process.StartInfo.ArgumentList.Add("-ar");
            process.StartInfo.ArgumentList.Add("44100");
            process.StartInfo.ArgumentList.Add(outputFile);

            process.Start();
            var stdErr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _customLogger?.Append(runId, $"[{storyId}] [WARN] ffmpeg loop ambience failed: {stdErr}");
                return (false, stdErr);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{storyId}] [WARN] ffmpeg loop ambience exception: {ex.Message}");
            return (false, ex.Message);
        }
    }
    
    private async Task<(bool success, string? error)> CreateMusicTrackAsync(
        string folderPath,
        List<(string FilePath, int StartMs, int DurationMs)> musicFiles,
        string outputFile,
        string runId,
        long storyId,
        double volumeScale)
    {
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        var labels = new List<string>();
        
        for (int i = 0; i < musicFiles.Count; i++)
        {
            var (filePath, startMs, durationMs) = musicFiles[i];
            inputArgs.Append($" -i \"{filePath}\"");
            var label = $"m{i}";
            var endSeconds = (durationMs / 1000.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            var durationSeconds = durationMs / 1000.0;
            var fadeDurSeconds = Math.Min(2.0, Math.Max(0.0, durationSeconds));
            var fadeStartSeconds = Math.Max(0.0, durationSeconds - fadeDurSeconds);
            var fadeStart = fadeStartSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            var fadeDur = fadeDurSeconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            var musicVolume = 0.70 * volumeScale;
            filterArgs.Append($"[{i}]atrim=start=0:end={endSeconds},afade=t=out:st={fadeStart}:d={fadeDur},adelay={startMs}|{startMs},volume={musicVolume.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}[{label}];");
            labels.Add($"[{label}]");
        }
        
        if (labels.Count == 0)
        {
            return (false, "Nessun file music");
        }
        
        foreach (var lbl in labels) filterArgs.Append(lbl);
        filterArgs.Append($"amix=inputs={labels.Count}:duration=longest:normalize=0[out]");
        
        var filterFile = Path.Combine(folderPath, $"filter_music_{storyId}.txt");
        await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
        TryDeleteFile(filterFile);
        return result;
    }
    
    private async Task<(bool success, string? error)> MixFinalTracksAsync(
        List<string> trackFiles,
        string outputFile,
        string runId,
        long storyId,
        double voiceVolumeSetting,
        double ambienceVolumeSetting,
        double fxVolumeSetting,
        double musicVolumeSetting)
    {
        // Mix the 3 tracks: TTS (full volume), Ambience+FX (already has volume), Music (already has volume)
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        var labels = new List<string>();

        var safeVoiceSetting = Math.Max(0d, voiceVolumeSetting);
        var safeAmbienceFxSetting = Math.Max(0d, Math.Max(ambienceVolumeSetting, fxVolumeSetting));
        var safeMusicSetting = Math.Max(0d, musicVolumeSetting);
        var maxSetting = Math.Max(1d, Math.Max(safeVoiceSetting, Math.Max(safeAmbienceFxSetting, safeMusicSetting)));
         
        for (int i = 0; i < trackFiles.Count; i++)
        {
            inputArgs.Append($" -i \"{trackFiles[i]}\"");
            var fileName = Path.GetFileName(trackFiles[i]) ?? string.Empty;
            var label = $"mx{i}";
            double trackGain;
            if (fileName.StartsWith("track_tts_", StringComparison.OrdinalIgnoreCase))
            {
                trackGain = safeVoiceSetting / maxSetting;
            }
            else if (fileName.StartsWith("track_music_", StringComparison.OrdinalIgnoreCase))
            {
                trackGain = safeMusicSetting / maxSetting;
            }
            else
            {
                trackGain = safeAmbienceFxSetting / maxSetting;
            }

            // Keep a floor so tracks are never fully muted unless setting is 0.
            trackGain = Math.Clamp(trackGain, 0d, 1.5d);
            filterArgs.Append($"[{i}]volume={trackGain.ToString("0.###", CultureInfo.InvariantCulture)}[{label}];");
            labels.Add($"[{label}]");
        }
         
        var mixOptions = _audioMixOptions?.CurrentValue ?? new AudioMixOptions();
        var limiterLevel = Math.Clamp(mixOptions.FinalLimiterLevel, 0.5, 0.999);
        foreach (var lbl in labels) filterArgs.Append(lbl);

        // Final mastering:
        // - optional dynamic normalization (can color/degrade voices)
        // - otherwise apply only a light limiter to avoid clipping while preserving timbre
        if (mixOptions.EnableFinalDynamicNormalization)
        {
            filterArgs.Append($"amix=inputs={trackFiles.Count}:duration=longest:dropout_transition=2:normalize=0[mixed];[mixed]dynaudnorm=p=0.95:s=3,alimiter=limit={limiterLevel.ToString("0.###", CultureInfo.InvariantCulture)}[out]");
            _customLogger?.Append(runId, $"[{storyId}] Final mix mastering: dynaudnorm=ON, limiter={limiterLevel.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
        else
        {
            filterArgs.Append($"amix=inputs={trackFiles.Count}:duration=longest:dropout_transition=2:normalize=0[mixed];[mixed]alimiter=limit={limiterLevel.ToString("0.###", CultureInfo.InvariantCulture)}[out]");
            _customLogger?.Append(
                runId,
                $"[{storyId}] Final mix mastering: dynaudnorm=OFF, limiter={limiterLevel.ToString("0.###", CultureInfo.InvariantCulture)}, gains voice={((safeVoiceSetting / maxSetting)).ToString("0.###", CultureInfo.InvariantCulture)}, ambience_fx={((safeAmbienceFxSetting / maxSetting)).ToString("0.###", CultureInfo.InvariantCulture)}, music={((safeMusicSetting / maxSetting)).ToString("0.###", CultureInfo.InvariantCulture)}");
        }
        
        var filterFile = Path.Combine(Path.GetDirectoryName(outputFile)!, $"filter_final_{storyId}.txt");
        await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
        TryDeleteFile(filterFile);
        return result;
    }
    
    private async Task<(bool success, string? error)> MergeAudioFilesAsync(
        List<string> audioFiles,
        string outputFile,
        string runId,
        long storyId)
    {
        // Simple concatenation of audio files (no timing, already positioned)
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        
        for (int i = 0; i < audioFiles.Count; i++)
        {
            inputArgs.Append($" -i \"{audioFiles[i]}\"");
            filterArgs.Append($"[{i}]");
        }
        
        filterArgs.Append($"amix=inputs={audioFiles.Count}:duration=longest:normalize=0[out]");
        
        var filterFile = Path.Combine(Path.GetDirectoryName(outputFile)!, $"filter_merge_{storyId}.txt");
        await File.WriteAllTextAsync(filterFile, filterArgs.ToString());
        
        var result = await RunFfmpegProcessAsync(inputArgs.ToString(), filterFile, outputFile, runId, storyId);
        TryDeleteFile(filterFile);
        return result;
    }
    
    private async Task<(bool success, string? error)> RunFfmpegProcessAsync(
        string inputArgs, 
        string filterScriptFile, 
        string outputFile, 
        string runId, 
        long storyId)
    {
        try
        {
            // Extract input file paths from inputArgs string (format: " -i "path1" -i "path2" ...")
            var inputFiles = new List<string>();
            var regex = new Regex(@"-i\s+""([^""]+)""");
            foreach (Match match in regex.Matches(inputArgs))
            {
                inputFiles.Add(match.Groups[1].Value);
            }
            
            _customLogger?.Append(runId, $"[{storyId}] ffmpeg con {inputFiles.Count} input files");
            
            // Build argument list for ffmpeg using ArgumentList (avoids command line length issues)
            var process = new Process();
            process.StartInfo.FileName = "ffmpeg";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            
            // Add arguments one by one using ArgumentList
            process.StartInfo.ArgumentList.Add("-y");
            
            foreach (var inputFile in inputFiles)
            {
                process.StartInfo.ArgumentList.Add("-i");
                process.StartInfo.ArgumentList.Add(inputFile);
            }
            
            process.StartInfo.ArgumentList.Add("-filter_complex_script");
            process.StartInfo.ArgumentList.Add(filterScriptFile);
            process.StartInfo.ArgumentList.Add("-map");
            process.StartInfo.ArgumentList.Add("[out]");
            process.StartInfo.ArgumentList.Add("-ac");
            process.StartInfo.ArgumentList.Add("2");
            process.StartInfo.ArgumentList.Add("-ar");
            process.StartInfo.ArgumentList.Add("48000");  // Mantenere qualit� audio 48kHz (stesso del TTS)
            process.StartInfo.ArgumentList.Add("-b:a");
            process.StartInfo.ArgumentList.Add("320k");   // Bitrate alto per qualit� master
            process.StartInfo.ArgumentList.Add(outputFile);
            
            process.Start();
            
            // Read stderr asynchronously to avoid deadlock when buffer fills up
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            
            // Wait for process with timeout to avoid hanging indefinitely
            var processExited = true;
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                processExited = false;
            }
            
            var stderr = await stderrTask;
            await stdoutTask; // Read stdout to prevent buffer issues

            if (!processExited)
            {
                _customLogger?.Append(runId, $"[{storyId}] Timeout ffmpeg (30 minuti)");
                return (false, "Timeout ffmpeg: il processo ha impiegato pi� di 30 minuti");
            }

            if (process.ExitCode != 0)
            {
                _customLogger?.Append(runId, $"[{storyId}] Errore ffmpeg: {stderr}");
                return (false, $"Errore ffmpeg (exit code {process.ExitCode}): {stderr.Substring(0, Math.Min(500, stderr.Length))}");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{storyId}] Eccezione ffmpeg: {ex.Message}");
            return (false, $"Eccezione durante l'esecuzione di ffmpeg: {ex.Message}");
        }
    }
    
    
    private void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    /// <summary>
    /// Mix final audio for a story by combining TTS, ambience, and FX tracks.
    /// If dispatcherRunId is provided, progress will be reported to the CommandDispatcher.
    /// </summary>
    public async Task<(bool success, string? error)> MixFinalAudioForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, null);
        var deleteCmd = new DeleteFinalMixCommand(this);
        var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
        if (!cleanupOk)
        {
            return (false, cleanupMessage ?? "Impossibile cancellare il mix finale esistente");
        }
        
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            return await MixFinalAudioInternalAsync(context, dispatcherRunId);
        }
        var (success, message) = await StartMixFinalAudioAsync(context);
        return (success, message);
    }

    internal async Task<(bool success, string? message)> StartGenerateStoryVideoAsync(StoryCommandContext context)
    {
        var storyId = context.Story.Id;
        var runId = string.IsNullOrWhiteSpace(CurrentDispatcherRunId)
            ? $"video_{storyId}_{DateTime.UtcNow:yyyyMMddHHmmss}"
            : CurrentDispatcherRunId!;

        if (string.IsNullOrWhiteSpace(CurrentDispatcherRunId))
        {
            _customLogger?.Start(runId);
        }
        _customLogger?.Append(runId, $"[{storyId}] Avvio generazione video finale nella cartella {context.FolderPath}");

        try
        {
            var (success, message) = await GenerateStoryVideoInternalAsync(context, runId);

            if (success && context.TargetStatus?.Id > 0)
            {
                try
                {
                    _database.UpdateStoryById(storyId, statusId: context.TargetStatus.Id, updateStatus: true);
                    _customLogger?.Append(runId, $"[{storyId}] Stato aggiornato a {context.TargetStatus.Code ?? context.TargetStatus.Id.ToString()}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Impossibile aggiornare lo stato video per la storia {Id}", storyId);
                    _customLogger?.Append(runId, $"[{storyId}] Aggiornamento stato video fallito: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(CurrentDispatcherRunId))
            {
                var completionMessage = message ?? (success ? "Generazione video completata" : "Errore generazione video");
                await (_customLogger?.MarkCompletedAsync(runId, completionMessage) ?? Task.CompletedTask);
            }
            return (success, message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Errore non gestito durante la generazione video per la storia {Id}", storyId);
            _customLogger?.Append(runId, $"[{storyId}] Errore inatteso: {ex.Message}");
            if (string.IsNullOrWhiteSpace(CurrentDispatcherRunId))
            {
                await (_customLogger?.MarkCompletedAsync(runId, $"Errore: {ex.Message}") ?? Task.CompletedTask);
            }
            return (false, $"Errore: {ex.Message}");
        }
    }

    public async Task<(bool success, string? error)> GenerateStoryVideoForStoryAsync(long storyId, string folderName, string? dispatcherRunId = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return (false, "Folder name is required");

        var story = GetStoryById(storyId);
        if (story == null)
            return (false, "Story not found");

        var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", folderName);
        Directory.CreateDirectory(folderPath);

        var context = new StoryCommandContext(story, folderPath, null);
        if (!string.IsNullOrWhiteSpace(dispatcherRunId))
        {
            return await GenerateStoryVideoInternalAsync(context, dispatcherRunId);
        }

        return await StartGenerateStoryVideoAsync(context);
    }

    private async Task<(bool success, string? message)> GenerateStoryVideoInternalAsync(StoryCommandContext context, string runId)
    {
        var story = context.Story;
        var folderPath = context.FolderPath;
        var emitBatchProgress = IsBatchWorkerProcess();
        void ReportVideoProgress(int current, int max, string description)
            => ReportCommandProgress(runId, current, max, description, emitBatchProgress);

        ReportVideoProgress(0, 100, $"[{story.Id}] Video: preparazione");
        var schemaPath = Path.Combine(folderPath, "tts_schema.json");
        if (!File.Exists(schemaPath))
        {
            return (false, "File tts_schema.json non trovato: genera prima lo schema TTS");
        }

        var mixAudioPath = Path.Combine(folderPath, "final_mix.mp3");
        if (!File.Exists(mixAudioPath))
        {
            mixAudioPath = Path.Combine(folderPath, "final_mix.wav");
        }
        if (!File.Exists(mixAudioPath))
        {
            return (false, "File final_mix non trovato: genera prima il mix finale audio");
        }

        JsonObject? rootNode;
        try
        {
            var json = await File.ReadAllTextAsync(schemaPath);
            rootNode = JsonNode.Parse(json) as JsonObject;
        }
        catch (Exception ex)
        {
            return (false, $"Impossibile leggere tts_schema.json: {ex.Message}");
        }

        if (rootNode == null)
        {
            return (false, "Formato tts_schema.json non valido");
        }

        if (!rootNode.TryGetPropertyValue("timeline", out var timelineNode))
            rootNode.TryGetPropertyValue("Timeline", out timelineNode);
        if (timelineNode is not JsonArray timelineArray)
        {
            return (false, "Timeline mancante nello schema TTS");
        }

        var phraseEntries = timelineArray.OfType<JsonObject>().Where(IsPhraseEntry).ToList();
        if (phraseEntries.Count == 0)
        {
            return (false, "La timeline non contiene frasi per i sottotitoli");
        }
        var phrasesTotal = phraseEntries.Count;
        var progressMax = Math.Max(phrasesTotal + 5, 10);
        ReportVideoProgress(1, progressMax, $"[{story.Id}] Video: timeline pronta ({phrasesTotal} frasi)");

        var introShiftMs = 0;
        try
        {
            var selectedOpening = SelectOpeningMusicFromSoundsCatalog(story.Id);
            if (!string.IsNullOrWhiteSpace(selectedOpening) && File.Exists(selectedOpening))
            {
                introShiftMs = 15000;
            }
        }
        catch
        {
            introShiftMs = 0;
        }

        var mixGapOptions = _audioMixOptions?.CurrentValue ?? new AudioMixOptions();
        var maxSilenceGapMs = (int)Math.Round(Math.Max(0d, mixGapOptions.MaxSilenceGapSeconds) * 1000d);
        var defaultPhraseGapMs = Math.Max(0, mixGapOptions.DefaultPhraseGapMs);
        var commaAttributionGapMs = Math.Clamp(mixGapOptions.CommaAttributionGapMs, 0, Math.Max(0, defaultPhraseGapMs));
        if (maxSilenceGapMs > 0)
        {
            defaultPhraseGapMs = Math.Min(defaultPhraseGapMs, maxSilenceGapMs);
            commaAttributionGapMs = Math.Min(commaAttributionGapMs, maxSilenceGapMs);
        }

        var finalTrimMaxGapMs = (int)Math.Round(Math.Max(0d, mixGapOptions.FinalSilenceTrimMaxGapSeconds) * 1000d);
        var finalTrimKeepMs = (int)Math.Round(Math.Clamp(mixGapOptions.FinalSilenceTrimKeepSeconds, 0d, Math.Max(0d, mixGapOptions.FinalSilenceTrimMaxGapSeconds)) * 1000d);
        if (mixGapOptions.EnableFinalSilenceTrim && finalTrimMaxGapMs > 0)
        {
            var (timelineCuts, mapTimeMs) = BuildTimelineSilenceTrimMap(phraseEntries, finalTrimMaxGapMs, finalTrimKeepMs);
            if (timelineCuts.Count > 0)
            {
                var updatedTimelineCount = ApplyMappedTimelineTimes(phraseEntries, mapTimeMs);
                if (updatedTimelineCount > 0)
                {
                    try
                    {
                        var pretty = rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                        await File.WriteAllTextAsync(schemaPath, pretty, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        _customLogger?.Append(runId, $"[{story.Id}] [WARN] Impossibile riscrivere tts_schema.json con tempi compensati: {ex.Message}");
                    }
                }

                try
                {
                    var cutsPath = Path.Combine(folderPath, "final_mix_silence_cuts.json");
                    var cutsPayload = timelineCuts.Select((c, idx) => new
                    {
                        index = idx + 1,
                        original_gap_start_ms = c.GapStartMs,
                        original_gap_end_ms = c.GapEndMs,
                        original_gap_ms = c.GapEndMs - c.GapStartMs,
                        kept_ms = c.KeepMs,
                        removed_ms = c.RemovedMs,
                        mapped_gap_end_ms = c.GapEndMs - c.CumulativeRemovedMs
                    }).ToList();
                    await File.WriteAllTextAsync(cutsPath, JsonSerializer.Serialize(cutsPayload, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                }
                catch
                {
                    // best effort
                }

                var removedTotalMs = timelineCuts.Sum(c => c.RemovedMs);
                _customLogger?.Append(
                    runId,
                    $"[{story.Id}] Compensazione timeline per trim silenzi: {timelineCuts.Count} tagli, {removedTotalMs}ms rimossi. tts_schema.json aggiornato.");
            }
        }

        IReadOnlyDictionary<int, (int StartMs, int EndMs)>? fixedSubtitleTimings = null;
        try
        {
            var subtitleTimelinePath = Path.Combine(folderPath, "final_mix_subtitle_timeline.json");
            if (File.Exists(subtitleTimelinePath))
            {
                var subtitleTimelineJson = await File.ReadAllTextAsync(subtitleTimelinePath, Encoding.UTF8);
                var subtitleTimelineNode = JsonNode.Parse(subtitleTimelineJson) as JsonObject;
                if (subtitleTimelineNode?["entries"] is JsonArray subtitleEntriesNode)
                {
                    var mapped = new Dictionary<int, (int StartMs, int EndMs)>();
                    foreach (var item in subtitleEntriesNode.OfType<JsonObject>())
                    {
                        if (!TryReadNumber(item, "index", out var idxRaw))
                        {
                            continue;
                        }
                        var idx = Math.Max(0, (int)Math.Round(idxRaw, MidpointRounding.AwayFromZero));
                        var hasStart = TryReadNumber(item, "start_ms", out var startRaw) || TryReadNumber(item, "startMs", out startRaw);
                        var hasEnd = TryReadNumber(item, "end_ms", out var endRaw) || TryReadNumber(item, "endMs", out endRaw);
                        if (!hasStart || !hasEnd)
                        {
                            continue;
                        }
                        var start = Math.Max(0, (int)Math.Round(startRaw, MidpointRounding.AwayFromZero));
                        var end = Math.Max(start, (int)Math.Round(endRaw, MidpointRounding.AwayFromZero));
                        mapped[idx] = (start, end);
                    }

                    if (mapped.Count > 0)
                    {
                        fixedSubtitleTimings = mapped;
                        _customLogger?.Append(runId, $"[{story.Id}] Timeline sottotitoli da mix caricata: {mapped.Count} frasi.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Impossibile leggere final_mix_subtitle_timeline.json, fallback su tts_schema: {ex.Message}");
        }

        var subtitleEntries = BuildSubtitleEntries(
            phraseEntries,
            folderPath,
            introShiftMs,
            defaultPhraseGapMs,
            commaAttributionGapMs,
            fixedSubtitleTimings,
            (current, max, description) =>
            {
                var mappedCurrent = 1 + Math.Clamp(current, 0, Math.Max(1, max));
                ReportVideoProgress(mappedCurrent, progressMax, $"[{story.Id}] {description}");
            });
        if (subtitleEntries.Count == 0)
        {
            return (false, "Impossibile costruire sottotitoli: nessuna riga valida nella timeline");
        }

        var srtPath = Path.Combine(folderPath, "final_mix_subtitles.srt");
        await File.WriteAllTextAsync(srtPath, BuildSrt(subtitleEntries), Encoding.UTF8);
        _customLogger?.Append(runId, $"[{story.Id}] Sottotitoli generati: {Path.GetFileName(srtPath)} ({subtitleEntries.Count} righe)");
        ReportVideoProgress(phrasesTotal + 2, progressMax, $"[{story.Id}] Video: sottotitoli pronti ({subtitleEntries.Count} righe)");

        var outputVideoPath = Path.Combine(folderPath, "final_video.mp4");
        var subtitleFilterPath = EscapeForFfmpegSubtitlesPath(srtPath);
        var subtitleFilter = $"subtitles='{subtitleFilterPath}':force_style='Alignment=2,FontName=Arial,FontSize=22,PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000,BorderStyle=1,Outline=2,Shadow=0,MarginV=34'";
        var audioDurationMs = TryGetAudioDurationMsFromFile(mixAudioPath) ?? subtitleEntries.Max(s => s.EndMs);
        var ambientKeywords = ExtractAmbientKeywordsForOpenImages(phraseEntries).ToList();
        var imagesFolder = Path.Combine(Directory.GetCurrentDirectory(), "immagini");
        var backgroundImages = await ResolveStoryBackgroundImagesAsync(ambientKeywords, imagesFolder, maxImages: 6, runId, story.Id);
        if (backgroundImages.Count == 0)
        {
            var fallbackImagePath = EnsureFallbackBackgroundImage(imagesFolder, runId, story.Id);
            if (!string.IsNullOrWhiteSpace(fallbackImagePath) && File.Exists(fallbackImagePath))
            {
                backgroundImages.Add(fallbackImagePath);
            }
        }
        var concatPath = backgroundImages.Count > 0
            ? BuildSlideshowConcatFile(backgroundImages, folderPath, audioDurationMs, segmentSeconds: 20)
            : null;
        if (backgroundImages.Count > 0)
        {
            _customLogger?.Append(runId, $"[{story.Id}] Sfondo video: {backgroundImages.Count} immagini (catalogo + OpenImages), rotazione ogni 20s.");
        }
        else
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Nessuna immagine disponibile in catalogo/OpenImages, uso sfondo grafico di fallback.");
        }
        ReportVideoProgress(phrasesTotal + 3, progressMax, $"[{story.Id}] Video: sfondo pronto ({backgroundImages.Count} immagini)");

        async Task<(bool ok, string? error)> RunFfmpegAsync(bool useConcatInput, string? loopImagePath = null)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add("-hide_banner");
            if (useConcatInput)
            {
                process.StartInfo.ArgumentList.Add("-f");
                process.StartInfo.ArgumentList.Add("concat");
                process.StartInfo.ArgumentList.Add("-safe");
                process.StartInfo.ArgumentList.Add("0");
                process.StartInfo.ArgumentList.Add("-i");
                process.StartInfo.ArgumentList.Add(concatPath!);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(loopImagePath) || !File.Exists(loopImagePath))
                {
                    return (false, "Impossibile preparare un'immagine di sfondo valida per il video.");
                }
                process.StartInfo.ArgumentList.Add("-loop");
                process.StartInfo.ArgumentList.Add("1");
                process.StartInfo.ArgumentList.Add("-i");
                process.StartInfo.ArgumentList.Add(loopImagePath);
            }

            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(mixAudioPath);
            process.StartInfo.ArgumentList.Add("-vf");
            process.StartInfo.ArgumentList.Add($"scale=1280:720:force_original_aspect_ratio=increase,crop=1280:720,{subtitleFilter}");
            process.StartInfo.ArgumentList.Add("-c:v");
            process.StartInfo.ArgumentList.Add("libx264");
            process.StartInfo.ArgumentList.Add("-preset");
            process.StartInfo.ArgumentList.Add("veryfast");
            process.StartInfo.ArgumentList.Add("-crf");
            process.StartInfo.ArgumentList.Add("22");
            process.StartInfo.ArgumentList.Add("-pix_fmt");
            process.StartInfo.ArgumentList.Add("yuv420p");
            process.StartInfo.ArgumentList.Add("-r");
            process.StartInfo.ArgumentList.Add("30");
            process.StartInfo.ArgumentList.Add("-c:a");
            process.StartInfo.ArgumentList.Add("aac");
            process.StartInfo.ArgumentList.Add("-b:a");
            process.StartInfo.ArgumentList.Add("192k");
            process.StartInfo.ArgumentList.Add("-shortest");
            process.StartInfo.ArgumentList.Add("-movflags");
            process.StartInfo.ArgumentList.Add("+faststart");
            process.StartInfo.ArgumentList.Add(outputVideoPath);
            _customLogger?.Append(runId, $"[{story.Id}] ffmpeg video args: {string.Join(" ", process.StartInfo.ArgumentList)}");

            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return (false, "Timeout ffmpeg durante la generazione video (30 minuti)");
            }

            var stderr = await stderrTask;
            await stdoutTask;
            if (process.ExitCode == 0)
            {
                return (true, null);
            }

            var compactErr = (stderr ?? string.Empty).Trim();
            // ffmpeg prints banner/config first; the actionable error is usually at the end of stderr.
            var shortErr = compactErr.Length <= 1200
                ? compactErr
                : compactErr.Substring(Math.Max(0, compactErr.Length - 1200));
            return (false, $"Errore ffmpeg video (exit code {process.ExitCode}): {shortErr}");
        }

        try
        {
            ReportVideoProgress(phrasesTotal + 4, progressMax, $"[{story.Id}] Video: rendering ffmpeg in corso");
            var hasConcatInput = !string.IsNullOrWhiteSpace(concatPath) && File.Exists(concatPath);
            var fallbackImagePath = backgroundImages.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                                    ?? EnsureFallbackBackgroundImage(imagesFolder, runId, story.Id);

            var (ok, err) = await RunFfmpegAsync(hasConcatInput, hasConcatInput ? null : fallbackImagePath);
            if (!ok && hasConcatInput)
            {
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Rendering slideshow fallito, retry con immagine singola. Dettaglio: {err}");
                (ok, err) = await RunFfmpegAsync(useConcatInput: false, loopImagePath: fallbackImagePath);
            }

            if (!ok)
            {
                return (false, err);
            }
        }
        catch (Exception ex)
        {
            return (false, $"Eccezione durante la generazione video: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(concatPath))
            {
                TryDeleteFile(concatPath);
            }
        }

        if (!File.Exists(outputVideoPath))
        {
            return (false, "ffmpeg non ha creato final_video.mp4");
        }

        try
        {
            var targetStatus = GetStoryStatusByCode("video_generated_1");
            if (targetStatus != null)
            {
                _database.UpdateStoryById(story.Id, statusId: targetStatus.Id, updateStatus: true);
                _customLogger?.Append(runId, $"[{story.Id}] Stato aggiornato a video_generated_1");
            }
            else
            {
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Stato 'video_generated_1' non trovato nel DB; video creato senza cambio stato.");
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Aggiornamento stato video fallito: {ex.Message}");
        }

        var msg = $"Video generato: {Path.GetFileName(outputVideoPath)}";
        _customLogger?.Append(runId, $"[{story.Id}] {msg}");
        ReportVideoProgress(progressMax, progressMax, $"[{story.Id}] Video completato");
        return (true, msg);
    }

    private readonly record struct TimelineSilenceCut(
        int GapStartMs,
        int GapEndMs,
        int KeepMs,
        int RemovedMs,
        int CumulativeRemovedMs);

    private sealed class FinalMixSubtitleTiming
    {
        public int Index { get; set; }
        public int StartMs { get; set; }
        public int EndMs { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    private static (List<TimelineSilenceCut> Cuts, Func<int, int> MapTimeMs) BuildTimelineSilenceTrimMap(
        List<JsonObject> phraseEntries,
        int maxGapMs,
        int keepMs)
    {
        var cuts = new List<TimelineSilenceCut>();
        if (phraseEntries == null || phraseEntries.Count < 2 || maxGapMs <= 0)
        {
            return (cuts, t => Math.Max(0, t));
        }

        var ranges = phraseEntries
            .Select(e =>
            {
                var s = ReadEntryStartMs(e);
                var eMs = ReadEntryEndMs(e);
                if (eMs < s) eMs = s;
                return (Start: Math.Max(0, s), End: Math.Max(0, eMs));
            })
            .OrderBy(r => r.Start)
            .ToList();

        var safeKeepMs = Math.Clamp(keepMs, 0, maxGapMs);
        var cumulativeRemoved = 0;
        for (var i = 0; i < ranges.Count - 1; i++)
        {
            var gapStart = ranges[i].End;
            var gapEnd = ranges[i + 1].Start;
            if (gapEnd <= gapStart)
            {
                continue;
            }

            var gap = gapEnd - gapStart;
            if (gap <= maxGapMs)
            {
                continue;
            }

            var removed = gap - safeKeepMs;
            if (removed <= 0)
            {
                continue;
            }

            cumulativeRemoved += removed;
            cuts.Add(new TimelineSilenceCut(
                GapStartMs: gapStart,
                GapEndMs: gapEnd,
                KeepMs: safeKeepMs,
                RemovedMs: removed,
                CumulativeRemovedMs: cumulativeRemoved));
        }

        if (cuts.Count == 0)
        {
            return (cuts, t => Math.Max(0, t));
        }

        int MapTime(int originalMs)
        {
            var t = Math.Max(0, originalMs);
            var removedBefore = 0;
            foreach (var cut in cuts)
            {
                var keptUntil = cut.GapStartMs + cut.KeepMs;
                if (t <= keptUntil)
                {
                    break;
                }

                if (t < cut.GapEndMs)
                {
                    return Math.Max(0, keptUntil - removedBefore);
                }

                removedBefore += cut.RemovedMs;
            }

            var mapped = t - removedBefore;
            return mapped < 0 ? 0 : mapped;
        }

        return (cuts, MapTime);
    }

    private static int ApplyMappedTimelineTimes(List<JsonObject> phraseEntries, Func<int, int> mapTimeMs)
    {
        if (phraseEntries == null || phraseEntries.Count == 0 || mapTimeMs == null)
        {
            return 0;
        }

        var updated = 0;
        foreach (var entry in phraseEntries)
        {
            var start = ReadEntryStartMs(entry);
            var end = ReadEntryEndMs(entry);
            if (end < start)
            {
                end = start;
            }

            var mappedStart = mapTimeMs(start);
            var mappedEnd = mapTimeMs(end);
            if (mappedEnd < mappedStart)
            {
                mappedEnd = mappedStart;
            }

            var changed = mappedStart != start || mappedEnd != end;
            entry["startMs"] = mappedStart;
            entry["endMs"] = mappedEnd;
            entry["durationMs"] = Math.Max(0, mappedEnd - mappedStart);
            if (changed)
            {
                updated++;
            }
        }

        return updated;
    }

    private static (Func<int, int> MapTimeMs, int Cuts, int RemovedTotalMs) BuildSilenceTrimMapFromSubtitleTimings(
        List<FinalMixSubtitleTiming> timings,
        int maxGapMs,
        int keepMs)
    {
        if (timings == null || timings.Count < 2 || maxGapMs <= 0)
        {
            return (t => Math.Max(0, t), 0, 0);
        }

        var ordered = timings
            .Select(t => (Start: Math.Max(0, t.StartMs), End: Math.Max(Math.Max(0, t.StartMs), t.EndMs)))
            .OrderBy(r => r.Start)
            .ToList();

        var safeKeepMs = Math.Clamp(keepMs, 0, maxGapMs);
        var cuts = new List<TimelineSilenceCut>();
        var cumulativeRemoved = 0;
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var gapStart = ordered[i].End;
            var gapEnd = ordered[i + 1].Start;
            if (gapEnd <= gapStart)
            {
                continue;
            }

            var gap = gapEnd - gapStart;
            if (gap <= maxGapMs)
            {
                continue;
            }

            var removed = gap - safeKeepMs;
            if (removed <= 0)
            {
                continue;
            }

            cumulativeRemoved += removed;
            cuts.Add(new TimelineSilenceCut(
                GapStartMs: gapStart,
                GapEndMs: gapEnd,
                KeepMs: safeKeepMs,
                RemovedMs: removed,
                CumulativeRemovedMs: cumulativeRemoved));
        }

        if (cuts.Count == 0)
        {
            return (t => Math.Max(0, t), 0, 0);
        }

        int MapTime(int originalMs)
        {
            var t = Math.Max(0, originalMs);
            var removedBefore = 0;
            foreach (var cut in cuts)
            {
                var keptUntil = cut.GapStartMs + cut.KeepMs;
                if (t <= keptUntil)
                {
                    break;
                }

                if (t < cut.GapEndMs)
                {
                    return Math.Max(0, keptUntil - removedBefore);
                }

                removedBefore += cut.RemovedMs;
            }

            var mapped = t - removedBefore;
            return mapped < 0 ? 0 : mapped;
        }

        return (MapTime, cuts.Count, cuts.Sum(c => c.RemovedMs));
    }

    private List<(int StartMs, int EndMs, string Text)> BuildSubtitleEntries(
        List<JsonObject> phraseEntries,
        string folderPath,
        int introShiftMs,
        int defaultPhraseGapMs,
        int commaAttributionGapMs,
        IReadOnlyDictionary<int, (int StartMs, int EndMs)>? fixedTimingsByIndex = null,
        Action<int, int, string>? progress = null)
    {
        var result = new List<(int StartMs, int EndMs, string Text)>();
        var currentMs = Math.Max(0, introShiftMs);

        for (int i = 0; i < phraseEntries.Count; i++)
        {
            var phraseCurrent = i + 1;
            var phraseTotal = Math.Max(1, phraseEntries.Count);
            progress?.Invoke(phraseCurrent, phraseTotal, $"Video: elaborazione frase {phraseCurrent}/{phraseTotal}");

            var entry = phraseEntries[i];
            var text = (ReadString(entry, "text") ?? ReadString(entry, "Text") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var durationMs = 0;
            var ttsFileName = ReadString(entry, "fileName") ?? ReadString(entry, "FileName") ?? ReadString(entry, "file_name");
            if (!string.IsNullOrWhiteSpace(ttsFileName))
            {
                var ttsFilePath = Path.Combine(folderPath, ttsFileName);
                if (File.Exists(ttsFilePath))
                {
                    durationMs = TryGetWavDurationFromFile(ttsFilePath) ?? 0;
                    if (durationMs <= 0)
                    {
                        durationMs = TryGetAudioDurationMsFromFile(ttsFilePath) ?? 0;
                    }
                }
            }

            if (durationMs <= 0 &&
                (TryReadNumber(entry, "durationMs", out var d) ||
                 TryReadNumber(entry, "DurationMs", out d) ||
                 TryReadNumber(entry, "duration_ms", out d)))
            {
                durationMs = (int)d;
            }
            if (durationMs <= 0)
            {
                durationMs = 1800;
            }

            var chunks = SplitSubtitleTextIntoChunks(text)
                .Select(c => WrapSubtitleChunk(c, 44, 2))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToList();
            if (chunks.Count == 0)
            {
                continue;
            }

            var hasTimelineStart = TryReadNumber(entry, "startMs", out var startRaw) ||
                                   TryReadNumber(entry, "StartMs", out startRaw) ||
                                   TryReadNumber(entry, "start_ms", out startRaw);
            var hasTimelineEnd = TryReadNumber(entry, "endMs", out var endRaw) ||
                                 TryReadNumber(entry, "EndMs", out endRaw) ||
                                 TryReadNumber(entry, "end_ms", out endRaw);

            var startFromTimelineMs = hasTimelineStart ? Math.Max(0, (int)Math.Round(startRaw, MidpointRounding.AwayFromZero)) : -1;
            var endFromTimelineMs = hasTimelineEnd ? Math.Max(0, (int)Math.Round(endRaw, MidpointRounding.AwayFromZero)) : -1;
            var fixedTiming = (StartMs: 0, EndMs: 0);
            var hasFixedTiming = fixedTimingsByIndex != null && fixedTimingsByIndex.TryGetValue(i, out fixedTiming);

            var startMs = hasFixedTiming
                ? Math.Max(0, fixedTiming.StartMs)
                : hasTimelineStart
                ? Math.Max(0, introShiftMs + startFromTimelineMs)
                : Math.Max(0, currentMs);

            var effectivePhraseDurationMs = durationMs;
            if (hasFixedTiming)
            {
                effectivePhraseDurationMs = Math.Max(300, fixedTiming.EndMs - fixedTiming.StartMs);
            }
            else if (hasTimelineStart && hasTimelineEnd && endFromTimelineMs > startFromTimelineMs)
            {
                effectivePhraseDurationMs = Math.Max(300, endFromTimelineMs - startFromTimelineMs);
            }

            var chunkDurationMs = Math.Max(240, effectivePhraseDurationMs / Math.Max(1, chunks.Count));
            var cursorMs = startMs;
            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                var chunkText = chunks[chunkIndex];
                var chunkStart = cursorMs;
                var chunkEnd = Math.Max(chunkStart + 200, chunkStart + chunkDurationMs);
                result.Add((chunkStart, chunkEnd, chunkText));
                cursorMs = chunkEnd;
            }

            var endMs = cursorMs;

            var nextEntry = i + 1 < phraseEntries.Count ? phraseEntries[i + 1] : null;
            if (hasFixedTiming)
            {
                if (fixedTimingsByIndex != null && fixedTimingsByIndex.TryGetValue(i + 1, out var nextFixedTiming))
                {
                    currentMs = Math.Max(endMs, nextFixedTiming.StartMs);
                }
                else
                {
                    currentMs = endMs + Math.Max(0, ComputePhraseGapMs(entry, nextEntry, defaultPhraseGapMs, commaAttributionGapMs));
                }
            }
            else if (hasTimelineStart && hasTimelineEnd && endFromTimelineMs >= startFromTimelineMs)
            {
                var nextStartMs = -1;
                if (nextEntry != null &&
                    (TryReadNumber(nextEntry, "startMs", out var nextStartRaw) ||
                     TryReadNumber(nextEntry, "StartMs", out nextStartRaw) ||
                     TryReadNumber(nextEntry, "start_ms", out nextStartRaw)))
                {
                    nextStartMs = Math.Max(0, (int)Math.Round(nextStartRaw, MidpointRounding.AwayFromZero));
                }

                if (nextStartMs >= 0)
                {
                    currentMs = Math.Max(endMs, introShiftMs + nextStartMs);
                }
                else
                {
                    currentMs = endMs + Math.Max(0, ComputePhraseGapMs(entry, nextEntry, defaultPhraseGapMs, commaAttributionGapMs));
                }
            }
            else
            {
                var gapAfterMs = ComputePhraseGapMs(entry, nextEntry, defaultPhraseGapMs, commaAttributionGapMs);
                currentMs = endMs + Math.Max(0, gapAfterMs);
            }
        }

        return result;
    }

    private static string BuildSrt(List<(int StartMs, int EndMs, string Text)> entries)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            sb.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{FormatSrtTime(e.StartMs)} --> {FormatSrtTime(e.EndMs)}");
            sb.AppendLine(EscapeSrtText(e.Text));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string EscapeSrtText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\n", lines).Replace("-->", "->");
    }

    private static List<string> SplitSubtitleTextIntoChunks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new List<string>();
        }

        var chunks = Regex
            .Split(normalized, @"(?<=[\.,])\s+")
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return chunks.Count == 0 ? new List<string> { normalized } : chunks;
    }

    private static string WrapSubtitleChunk(string text, int maxLineLength, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var words = Regex.Split(text.Trim(), @"\s+")
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToList();
        if (words.Count == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        var current = new StringBuilder();

        foreach (var word in words)
        {
            if (current.Length == 0)
            {
                current.Append(word);
                continue;
            }

            if (current.Length + 1 + word.Length <= maxLineLength || lines.Count >= maxLines - 1)
            {
                current.Append(' ').Append(word);
                continue;
            }

            lines.Add(current.ToString());
            current.Clear();
            current.Append(word);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        if (lines.Count > maxLines)
        {
            var head = lines.Take(maxLines - 1).ToList();
            head.Add(string.Join(" ", lines.Skip(maxLines - 1)));
            lines = head;
        }

        return string.Join("\n", lines.Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    private static string FormatSrtTime(int ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00},{3:000}",
            (int)t.TotalHours,
            t.Minutes,
            t.Seconds,
            t.Milliseconds);
    }

    private static string EscapeForFfmpegSubtitlesPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace("\\", "/");
        if (normalized.Length >= 3 && normalized[1] == ':' && normalized[2] == '/')
        {
            normalized = normalized.Insert(1, "\\");
        }

        normalized = normalized.Replace("'", "\\'");
        normalized = normalized.Replace(",", "\\,");
        return normalized;
    }

    private static readonly HttpClient OpenImagesHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static readonly string[] OpenImagesClassDescriptionUrls =
    {
        "https://storage.googleapis.com/openimages/v5/class-descriptions-boxable.csv"
    };

    private static readonly string[] OpenImagesValidationLabelsUrls =
    {
        "https://storage.googleapis.com/openimages/v5/validation-annotations-human-imagelabels-boxable.csv"
    };

    private static readonly string[] OpenImagesImageUrlTemplates =
    {
        "https://open-images-dataset.s3.amazonaws.com/validation/{0}.jpg",
        "https://storage.googleapis.com/openimages/v6/validation/{0}.jpg",
        "https://storage.googleapis.com/openimages/v5/validation/{0}.jpg",
        "https://storage.googleapis.com/openimages/2018_04/validation/{0}.jpg"
    };

    private static readonly string[] SciFiSeedKeywords =
    {
        "space",
        "planet",
        "moon",
        "star",
        "rocket",
        "spacecraft",
        "satellite",
        "astronaut",
        "spaceship"
    };

    private static readonly string[] SpaceOnlyClassHints =
    {
        "space",
        "astronaut",
        "rocket",
        "spacecraft",
        "satellite",
        "planet",
        "moon",
        "sky",
        "star"
    };

    private static readonly Dictionary<string, string[]> AmbientKeywordSciFiMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["astronave"] = new[] { "spaceship", "spacecraft", "rocket", "space" },
        ["navicella"] = new[] { "spacecraft", "spaceship", "space" },
        ["spazio"] = new[] { "space", "planet", "galaxy", "star", "moon", "satellite" },
        ["galassia"] = new[] { "galaxy", "space", "star" },
        ["cosmo"] = new[] { "space", "galaxy", "star" },
        ["orbita"] = new[] { "satellite", "spacecraft", "space" },
        ["pianeta"] = new[] { "planet", "space", "moon" },
        ["luna"] = new[] { "moon", "space", "night" },
        ["marte"] = new[] { "planet", "desert", "space" },
        ["stazione"] = new[] { "spacecraft", "satellite", "station" },
        ["stazione spaziale"] = new[] { "spacecraft", "satellite", "space" },
        ["alieno"] = new[] { "space", "planet", "creature" },
        ["alieni"] = new[] { "space", "planet", "creature" }
    };

    private static IEnumerable<string> ExtractAmbientKeywordsForOpenImages(IEnumerable<JsonObject> phraseEntries)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in phraseEntries)
        {
            var pieces = new[]
            {
                ReadString(entry, "ambient_sound_tags"),
                ReadString(entry, "ambientSoundTags"),
                ReadString(entry, "AmbientSoundTags"),
                ReadString(entry, "ambient_sound_description"),
                ReadString(entry, "ambientSoundDescription"),
                ReadString(entry, "AmbientSoundDescription"),
                ReadString(entry, "ambientSounds"),
                ReadString(entry, "ambient_sounds")
            };

            foreach (var piece in pieces)
            {
                if (string.IsNullOrWhiteSpace(piece))
                    continue;

                foreach (var token in Regex.Split(piece, @"[,;|/\n\r]+"))
                {
                    var t = token.Trim().ToLowerInvariant();
                    if (t.Length < 3) continue;
                    if (t.StartsWith("suono ") || t.StartsWith("rumore "))
                    {
                        t = t.Split(' ', 2).LastOrDefault()?.Trim() ?? t;
                    }
                    if (t.Length >= 3)
                    {
                        keywords.Add(t);
                    }
                }
            }
        }

        // Add normalized/expanded terms for better OpenImages matching, with sci-fi preference.
        foreach (var k in keywords.ToList())
        {
            if (AmbientKeywordSciFiMap.TryGetValue(k, out var expansions))
            {
                foreach (var ex in expansions)
                {
                    if (!string.IsNullOrWhiteSpace(ex))
                    {
                        keywords.Add(ex.Trim().ToLowerInvariant());
                    }
                }
            }
        }

        if (ShouldPreferSciFi(keywords))
        {
            foreach (var seed in SciFiSeedKeywords)
            {
                keywords.Add(seed);
            }
        }

        if (keywords.Count == 0)
        {
            foreach (var seed in SciFiSeedKeywords.Take(8))
            {
                keywords.Add(seed);
            }
        }

        return keywords.Take(24);
    }

    private static bool ShouldPreferSciFi(IEnumerable<string> ambientKeywords)
    {
        foreach (var raw in ambientKeywords)
        {
            var k = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (k.Length == 0) continue;
            if (k.Contains("space", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("galaxy", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("planet", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("astronaut", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("rocket", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("spaceship", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("spacecraft", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("satellite", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("futur", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("cyber", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("astronav", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("spazio", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("galassi", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("alien", StringComparison.OrdinalIgnoreCase) ||
                k.Contains("robot", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSpaceOrPlanetText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var value = raw.Trim().ToLowerInvariant();
        return value.Contains("space", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("planet", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("moon", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("rocket", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("spacecraft", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("spaceship", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("satellite", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("astronaut", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("star", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("sky", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("spazio", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("pianeta", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("luna", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("astronav", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSpaceOrPlanetImageAsset(ImageAsset image)
    {
        if (image == null) return false;
        return IsSpaceOrPlanetText(image.Tags) ||
               IsSpaceOrPlanetText(image.Description) ||
               IsSpaceOrPlanetText(image.ImageName) ||
               IsSpaceOrPlanetText(image.Provenance) ||
               IsSpaceOrPlanetText(image.ImagePath);
    }

    private static int ScoreSpaceText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        var value = raw.Trim().ToLowerInvariant();
        var score = 0;
        if (value.Contains("planet", StringComparison.OrdinalIgnoreCase) || value.Contains("pianeta", StringComparison.OrdinalIgnoreCase)) score += 5;
        if (value.Contains("moon", StringComparison.OrdinalIgnoreCase) || value.Contains("luna", StringComparison.OrdinalIgnoreCase)) score += 5;
        if (value.Contains("space", StringComparison.OrdinalIgnoreCase) || value.Contains("spazio", StringComparison.OrdinalIgnoreCase)) score += 4;
        if (value.Contains("astronaut", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (value.Contains("satellite", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (value.Contains("rocket", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (value.Contains("spacecraft", StringComparison.OrdinalIgnoreCase) || value.Contains("spaceship", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (value.Contains("star", StringComparison.OrdinalIgnoreCase) || value.Contains("sky", StringComparison.OrdinalIgnoreCase)) score += 1;
        return score;
    }

    private static IReadOnlyList<string> BuildNasaQueries(IReadOnlyCollection<string> ambientKeywords)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in ambientKeywords)
        {
            var k = (keyword ?? string.Empty).Trim().ToLowerInvariant();
            if (k.Length >= 3 && IsSpaceOrPlanetText(k))
            {
                terms.Add(k);
            }
        }

        foreach (var seed in SciFiSeedKeywords)
        {
            terms.Add(seed);
        }

        var topTerms = terms
            .OrderByDescending(ScoreSpaceText)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var queries = new List<string>();
        if (topTerms.Count > 0)
        {
            queries.Add(string.Join(" ", topTerms));
        }
        queries.Add("planet moon space");
        queries.Add("astronaut satellite rocket spacecraft");
        return queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<string>> DownloadNasaSpaceBackgroundsAsync(
        IReadOnlyCollection<string> ambientKeywords,
        string destinationFolder,
        int maxImages,
        string runId,
        long storyId)
    {
        var downloaded = new List<string>();
        try
        {
            Directory.CreateDirectory(destinationFolder);

            var queries = BuildNasaQueries(ambientKeywords);
            var candidates = new List<(string Url, string MetaText, string FileStem)>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var query in queries)
            {
                if (candidates.Count >= Math.Max(maxImages * 20, 40))
                {
                    break;
                }

                var searchUrl = $"https://images-api.nasa.gov/search?media_type=image&q={Uri.EscapeDataString(query)}";
                using var response = await OpenImagesHttpClient.GetAsync(searchUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                if (!doc.RootElement.TryGetProperty("collection", out var collection) ||
                    !collection.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("data", out var dataArray) ||
                        dataArray.ValueKind != JsonValueKind.Array ||
                        dataArray.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var data = dataArray[0];
                    var mediaType = data.TryGetProperty("media_type", out var mediaNode) ? (mediaNode.GetString() ?? string.Empty) : string.Empty;
                    if (!string.Equals(mediaType, "image", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var title = data.TryGetProperty("title", out var titleNode) ? (titleNode.GetString() ?? string.Empty) : string.Empty;
                    var description = data.TryGetProperty("description", out var descNode) ? (descNode.GetString() ?? string.Empty) : string.Empty;
                    var nasaId = data.TryGetProperty("nasa_id", out var idNode) ? (idNode.GetString() ?? string.Empty) : string.Empty;
                    var keywordsText = string.Empty;
                    if (data.TryGetProperty("keywords", out var keywordsNode) && keywordsNode.ValueKind == JsonValueKind.Array)
                    {
                        keywordsText = string.Join(" ", keywordsNode.EnumerateArray()
                            .Where(k => k.ValueKind == JsonValueKind.String)
                            .Select(k => k.GetString())
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                    }

                    var metaText = $"{title} {description} {keywordsText}";
                    if (!IsSpaceOrPlanetText(metaText))
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("links", out var linksNode) || linksNode.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    string? imageUrl = null;
                    foreach (var link in linksNode.EnumerateArray())
                    {
                        if (!link.TryGetProperty("href", out var hrefNode)) continue;
                        var href = hrefNode.GetString();
                        if (string.IsNullOrWhiteSpace(href)) continue;
                        if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                        imageUrl = href;
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(imageUrl) || !seenUrls.Add(imageUrl))
                    {
                        continue;
                    }

                    var fileStem = string.IsNullOrWhiteSpace(nasaId) ? Guid.NewGuid().ToString("N") : SanitizeForFile(nasaId);
                    candidates.Add((imageUrl, metaText, fileStem));
                    if (candidates.Count >= Math.Max(maxImages * 20, 40))
                    {
                        break;
                    }
                }
            }

            var rnd = new Random(unchecked((int)(storyId ^ (storyId >> 32))));
            var ordered = candidates
                .OrderByDescending(c => ScoreSpaceText(c.MetaText))
                .ThenBy(_ => rnd.Next())
                .ToList();

            foreach (var candidate in ordered)
            {
                if (downloaded.Count >= maxImages) break;

                var extension = ".jpg";
                try
                {
                    var uri = new Uri(candidate.Url);
                    var ext = Path.GetExtension(uri.AbsolutePath);
                    if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 5)
                    {
                        extension = ext;
                    }
                }
                catch
                {
                    // keep default extension
                }

                var filePath = Path.Combine(destinationFolder, $"{candidate.FileStem}{extension}");
                if (File.Exists(filePath))
                {
                    var fi = new FileInfo(filePath);
                    if (fi.Length >= 40 * 1024)
                    {
                        downloaded.Add(filePath);
                    }
                    continue;
                }

                if (await TryDownloadImageAsync(candidate.Url, filePath, minBytes: 40 * 1024))
                {
                    downloaded.Add(filePath);
                }
            }

            _customLogger?.Append(runId, $"[{storyId}] NASA immagini: query={queries.Count}, candidate={candidates.Count}, scaricate={downloaded.Count}.");
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{storyId}] [WARN] Download immagini NASA fallito: {ex.Message}");
        }

        return downloaded;
    }

    private async Task<List<string>> ResolveStoryBackgroundImagesAsync(
        IReadOnlyCollection<string> ambientKeywords,
        string imagesFolder,
        int maxImages,
        string runId,
        long storyId)
    {
        var result = new List<string>();
        try
        {
            Directory.CreateDirectory(imagesFolder);
            var fromCatalog = _database.ListActiveImagesByTags(ambientKeywords, Math.Max(maxImages * 3, 12))
                .Where(IsSpaceOrPlanetImageAsset)
                .Where(i => !string.IsNullOrWhiteSpace(i.ImagePath) && File.Exists(i.ImagePath))
                .Take(maxImages)
                .ToList();

            foreach (var item in fromCatalog)
            {
                result.Add(item.ImagePath);
                _database.MarkImageUsed(item.Id);
            }

            if (result.Count >= maxImages)
            {
                _customLogger?.Append(runId, $"[{storyId}] Riuso catalogo immagini: {result.Count} elementi trovati per i tag.");
                return result;
            }

            var toDownload = maxImages - result.Count;
            var downloaded = await DownloadNasaSpaceBackgroundsAsync(ambientKeywords, imagesFolder, toDownload, runId, storyId);
            var tagsSerialized = string.Join(",", ambientKeywords
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(30));

            foreach (var path in downloaded)
            {
                if (!File.Exists(path)) continue;
                var fileName = Path.GetFileName(path);
                var description = string.IsNullOrWhiteSpace(tagsSerialized)
                    ? "Immagine sfondo video"
                    : $"Immagine sfondo video tags: {tagsSerialized}";

                try
                {
                    var id = _database.InsertImage(new ImageAsset
                    {
                        ImageName = fileName,
                        Description = description,
                        Provenance = "nasa/images-api.nasa.gov",
                        Tags = tagsSerialized,
                        ImagePath = path,
                        UsageCount = 1,
                        IsActive = true,
                        IsDeleted = false,
                        SortOrder = 0,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    });
                    if (id > 0)
                    {
                        result.Add(path);
                    }
                }
                catch
                {
                    // Duplicate image_path or other insert errors: still usable for video.
                    result.Add(path);
                }
            }

            if (result.Count == 0)
            {
                // Fallback soft: usa qualsiasi immagine attiva già catalogata.
                var generic = _database.ListActiveImagesByTags(SciFiSeedKeywords, Math.Max(maxImages * 2, 8))
                    .Where(IsSpaceOrPlanetImageAsset)
                    .Where(i => !string.IsNullOrWhiteSpace(i.ImagePath) && File.Exists(i.ImagePath))
                    .Take(maxImages)
                    .ToList();
                foreach (var item in generic)
                {
                    result.Add(item.ImagePath);
                    _database.MarkImageUsed(item.Id);
                }
            }

            if (result.Count == 0)
            {
                var fallbackImagePath = EnsureFallbackBackgroundImage(imagesFolder, runId, storyId);
                if (!string.IsNullOrWhiteSpace(fallbackImagePath) && File.Exists(fallbackImagePath))
                {
                    result.Add(fallbackImagePath);
                }
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxImages).ToList();
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{storyId}] [WARN] Risoluzione immagini background fallita: {ex.Message}");
            if (result.Count == 0)
            {
                try
                {
                    var fallbackImagePath = EnsureFallbackBackgroundImage(imagesFolder, runId, storyId);
                    if (!string.IsNullOrWhiteSpace(fallbackImagePath) && File.Exists(fallbackImagePath))
                    {
                        result.Add(fallbackImagePath);
                    }
                }
                catch
                {
                    // best effort
                }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Max(1, maxImages)).ToList();
        }
    }

    private string EnsureFallbackBackgroundImage(string imagesFolder, string runId, long storyId)
    {
        Directory.CreateDirectory(imagesFolder);
        var fallbackPath = Path.Combine(imagesFolder, "fallback_background.ppm");
        if (File.Exists(fallbackPath))
        {
            return fallbackPath;
        }

        // PPM ASCII semplice: sempre leggibile da ffmpeg e senza dipendenze esterne.
        const int width = 64;
        const int height = 36;
        var sb = new StringBuilder();
        sb.AppendLine("P3");
        sb.AppendLine($"{width} {height}");
        sb.AppendLine("255");
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = 20 + (x * 80 / Math.Max(1, width - 1));
                var g = 30 + (y * 90 / Math.Max(1, height - 1));
                var b = 55 + ((x + y) * 100 / Math.Max(1, width + height - 2));
                sb.Append(r).Append(' ').Append(g).Append(' ').Append(b).Append(' ');
            }
            sb.AppendLine();
        }

        File.WriteAllText(fallbackPath, sb.ToString(), Encoding.ASCII);
        _customLogger?.Append(runId, $"[{storyId}] Creata immagine fallback locale: {Path.GetFileName(fallbackPath)}");
        return fallbackPath;
    }

    private async Task<List<string>> DownloadOpenImagesBackgroundsAsync(
        IReadOnlyCollection<string> ambientKeywords,
        string destinationFolder,
        int maxImages,
        string runId,
        long storyId)
    {
        var downloaded = new List<string>();
        try
        {
            Directory.CreateDirectory(destinationFolder);

            var cacheRoot = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", ".openimages_cache");
            Directory.CreateDirectory(cacheRoot);
            var classPath = Path.Combine(cacheRoot, "class-descriptions-boxable.csv");
            var labelsPath = Path.Combine(cacheRoot, "validation-annotations-human-imagelabels-boxable.csv");

            if (!await EnsureDownloadedFromAnyUrlAsync(OpenImagesClassDescriptionUrls, classPath))
            {
                _customLogger?.Append(runId, $"[{storyId}] [WARN] OpenImages class-descriptions non scaricabile.");
                return downloaded;
            }

            if (!await EnsureDownloadedFromAnyUrlAsync(OpenImagesValidationLabelsUrls, labelsPath))
            {
                _customLogger?.Append(runId, $"[{storyId}] [WARN] OpenImages validation labels non scaricabile.");
                return downloaded;
            }

            var classById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in await File.ReadAllLinesAsync(classPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var idx = line.IndexOf(',');
                if (idx <= 0 || idx >= line.Length - 1) continue;
                var classId = line.Substring(0, idx).Trim();
                var className = line[(idx + 1)..].Trim().ToLowerInvariant();
                if (classId.Length > 0 && className.Length > 0 && !classById.ContainsKey(classId))
                {
                    classById[classId] = className;
                }
            }

            var preferSciFi = ShouldPreferSciFi(ambientKeywords);
            var normalizedKeywords = ambientKeywords
                .Select(k => (k ?? string.Empty).Trim().ToLowerInvariant())
                .Where(k => k.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var classPriorityById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var matchedClassIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in classById)
            {
                var classId = kvp.Key;
                var className = kvp.Value;
                var priority = 0;
                var isSpaceClass = SpaceOnlyClassHints.Any(h => className.Contains(h, StringComparison.OrdinalIgnoreCase));
                if (!isSpaceClass)
                {
                    continue;
                }

                if (normalizedKeywords.Any(k => className.Contains(k, StringComparison.OrdinalIgnoreCase) || k.Contains(className, StringComparison.OrdinalIgnoreCase)))
                {
                    priority = Math.Max(priority, 5);
                }

                priority = Math.Max(priority, 10);

                if (priority > 0)
                {
                    matchedClassIds.Add(classId);
                    classPriorityById[classId] = priority;
                }
            }

            if (matchedClassIds.Count == 0)
            {
                _customLogger?.Append(runId, $"[{storyId}] Nessuna classe OpenImages a tema spazio/pianeti trovata.");
                return downloaded;
            }

            var imageScores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var imageMatches = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var reader = new StreamReader(labelsPath))
            {
                _ = await reader.ReadLineAsync(); // header
                var scanned = 0;
                var maxScanLines = Math.Max(200_000, maxImages * 120_000);
                var targetCandidates = Math.Max(maxImages * 120, 120);
                while (!reader.EndOfStream)
                {
                    scanned++;
                    if (scanned > maxScanLines && imageScores.Count >= Math.Max(maxImages * 20, 20))
                    {
                        break;
                    }

                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length < 4) continue;
                    var imageId = parts[0].Trim();
                    var labelId = parts[2].Trim();
                    var confidence = parts[3].Trim();
                    if (!matchedClassIds.Contains(labelId)) continue;
                    if (!string.Equals(confidence, "1", StringComparison.OrdinalIgnoreCase)) continue;
                    if (imageId.Length == 0) continue;

                    var score = classPriorityById.TryGetValue(labelId, out var w) ? w : 1;
                    if (!imageScores.ContainsKey(imageId))
                    {
                        imageScores[imageId] = 0;
                        imageMatches[imageId] = 0;
                    }
                    imageScores[imageId] += score;
                    imageMatches[imageId]++;

                    if (imageScores.Count >= targetCandidates && scanned > maxScanLines / 4)
                    {
                        break;
                    }
                }
            }

            // deterministic order: first best thematic score, then pseudo-random to vary results.
            var rnd = new Random(unchecked((int)(storyId ^ (storyId >> 32))));
            var orderedCandidates = imageScores
                .OrderByDescending(kvp => kvp.Value)
                .ThenByDescending(kvp => imageMatches.TryGetValue(kvp.Key, out var m) ? m : 0)
                .ThenBy(_ => rnd.Next())
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var imageId in orderedCandidates)
            {
                if (downloaded.Count >= maxImages) break;
                var filePath = Path.Combine(destinationFolder, $"{imageId}.jpg");
                if (File.Exists(filePath))
                {
                    var existing = new FileInfo(filePath);
                    if (existing.Length >= 40 * 1024)
                    {
                        downloaded.Add(filePath);
                    }
                    continue;
                }

                foreach (var template in OpenImagesImageUrlTemplates)
                {
                    var url = string.Format(CultureInfo.InvariantCulture, template, imageId);
                    if (await TryDownloadImageAsync(url, filePath, minBytes: 40 * 1024))
                    {
                        downloaded.Add(filePath);
                        break;
                    }
                }
            }

            _customLogger?.Append(runId, $"[{storyId}] OpenImages: preferSciFi={preferSciFi}, classi={matchedClassIds.Count}, candidati={imageScores.Count}, scaricate={downloaded.Count}.");
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{storyId}] [WARN] Download immagini OpenImages fallito: {ex.Message}");
        }

        return downloaded;
    }

    private static async Task<bool> EnsureDownloadedFromAnyUrlAsync(IEnumerable<string> urls, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            var fi = new FileInfo(destinationPath);
            if (fi.Length > 64) return true;
        }

        foreach (var url in urls)
        {
            try
            {
                using var response = await OpenImagesHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    continue;

                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(destinationPath);
                await input.CopyToAsync(output);
                var fi = new FileInfo(destinationPath);
                if (fi.Length > 64) return true;
            }
            catch
            {
                // try next URL
            }
        }

        return false;
    }

    private static async Task<bool> TryDownloadImageAsync(string url, string destinationPath, int minBytes = 5120)
    {
        try
        {
            using var response = await OpenImagesHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(destinationPath);
            await input.CopyToAsync(output);
            return new FileInfo(destinationPath).Length >= Math.Max(1024, minBytes);
        }
        catch
        {
            return false;
        }
    }

    private static string? BuildSlideshowConcatFile(IReadOnlyList<string> images, string folderPath, int targetDurationMs, int segmentSeconds)
    {
        if (images == null || images.Count == 0 || segmentSeconds <= 0)
            return null;

        var totalSeconds = Math.Max(1, (int)Math.Ceiling(targetDurationMs / 1000.0));
        var blocks = Math.Max(1, (int)Math.Ceiling(totalSeconds / (double)segmentSeconds));
        var concatPath = Path.Combine(folderPath, "video_backgrounds_concat.txt");
        var sb = new StringBuilder();
        for (int i = 0; i < blocks; i++)
        {
            var img = images[i % images.Count];
            sb.AppendLine($"file '{img.Replace("'", "''")}'");
            sb.AppendLine($"duration {segmentSeconds.ToString(CultureInfo.InvariantCulture)}");
        }

        // ffmpeg concat demuxer wants the final file repeated without duration
        var lastImg = images[(blocks - 1) % images.Count];
        sb.AppendLine($"file '{lastImg.Replace("'", "''")}'");

        File.WriteAllText(concatPath, sb.ToString(), Encoding.UTF8);
        return concatPath;
    }

    private static Dictionary<string, CharacterVoiceInfo> BuildCharacterMap(JsonArray charactersArray)
    {
        var map = new Dictionary<string, CharacterVoiceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in charactersArray.OfType<JsonObject>())
        {
            var name = ReadString(node, "Name") ?? ReadString(node, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var voiceId = ReadString(node, "VoiceId") ?? ReadString(node, "voiceId") ?? ReadString(node, "voice_id");
            var gender = ReadString(node, "Gender") ?? ReadString(node, "gender");

            map[name] = new CharacterVoiceInfo(name, voiceId, gender);
        }

        return map;
    }

    private static bool IsPhraseEntry(JsonObject entry)
    {
        // Try both casings: "character" and "Character"
        if (entry.TryGetPropertyValue("character", out var charNode) && charNode != null)
            return true;
        return entry.TryGetPropertyValue("Character", out charNode) && charNode != null;
    }

    private int GetPhraseGapMs(string text)
    {
        var seconds = _ttsSchemaOptions?.CurrentValue?.PhraseGapSeconds ?? (DefaultPhraseGapMs / 1000.0);
        var gapMs = (int)Math.Max(0, Math.Round(seconds * 1000.0));
        if (gapMs <= 0) return 0;
        return EndsWithSentence(text) ? gapMs : 0;
    }

    private static bool EndsWithSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.TrimEnd();
        while (t.Length > 0)
        {
            var last = t[^1];
            if (last == '"' || last == '\'' || last == '”' || last == '»')
            {
                t = t.Substring(0, t.Length - 1).TrimEnd();
                continue;
            }
            break;
        }

        if (t.EndsWith("...")) return true;
        var end = t.Length > 0 ? t[^1] : '\0';
        return end == '.' || end == '!' || end == '?' || end == '…';
    }

    private static bool IsPauseEntry(JsonObject entry, out int pauseMs)
    {
        pauseMs = 0;
        if (TryReadNumber(entry, "Seconds", out var seconds) || TryReadNumber(entry, "seconds", out seconds))
        {
            pauseMs = (int)Math.Max(0, Math.Round(seconds * 1000.0));
            return pauseMs > 0;
        }

        return false;
    }

    private static bool TryReadPhrase(JsonObject entry, out string character, out string text, out string emotion)
    {
        character = text = emotion = string.Empty;
        var charName = ReadString(entry, "Character") ?? ReadString(entry, "character");
        if (string.IsNullOrWhiteSpace(charName))
            return false;

        character = charName.Trim();
        text = ReadString(entry, "Text") ?? ReadString(entry, "text") ?? string.Empty;
        emotion = ReadString(entry, "Emotion") ?? ReadString(entry, "emotion") ?? "neutral";
        if (string.IsNullOrWhiteSpace(emotion))
            emotion = "neutral";
        emotion = emotion.Trim();
        return true;
    }

    private static string BuildAudioFileName(int index, string characterName, HashSet<string> usedFiles)
    {
        var sanitized = SanitizeForFile(characterName);
        var candidate = $"{index:000}_{sanitized}.wav";
        int suffix = 1;
        while (!usedFiles.Add(candidate))
        {
            candidate = $"{index:000}_{sanitized}_{suffix++}.wav";
        }
        return candidate;
    }

    private static string SanitizeForFile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "phrase";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (invalid.Contains(c) || char.IsWhiteSpace(c))
            {
                builder.Append('_');
            }
            else if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append('_');
            }
        }

        var result = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "phrase" : result;
    }

    // TTS API supported emotions
    private static readonly HashSet<string> ValidTtsEmotions = new(StringComparer.OrdinalIgnoreCase)
    {
        "neutral", "happy", "sad", "angry", "fearful", "disgusted", "surprised"
    };

    private async Task<(byte[] audioBytes, int? durationMs)> GenerateAudioBytesAsync(string voiceId, string text, string emotion)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Il testo della frase e vuoto");

        // Normalize emotion to valid TTS value or fallback to neutral
        var normalizedEmotion = NormalizeEmotionForTts(emotion);
        
        // Normalize text for TTS before sending to service
        var ttsSafeText = ProsodyNormalizer.NormalizeForTTS(text);
        var voiceProvider = ResolveVoiceProvider(voiceId);
        SynthesisResult? synthesis = null;

        try
        {
            synthesis = await _ttsService.SynthesizeAsync(voiceId, ttsSafeText, "it", normalizedEmotion, voiceProvider);
        }
        catch (Exception ex)
        {
            var recovered = await TryEnsureTtsServiceReadyAsync();
            if (!recovered)
            {
                throw;
            }

            _logger?.LogWarning(ex, "TTS non disponibile al primo tentativo. Riprovo dopo restart del servizio.");
            synthesis = await _ttsService.SynthesizeAsync(voiceId, ttsSafeText, "it", normalizedEmotion, voiceProvider);
        }

        if (synthesis == null)
        {
            var recovered = await TryEnsureTtsServiceReadyAsync();
            if (recovered)
            {
                synthesis = await _ttsService.SynthesizeAsync(voiceId, ttsSafeText, "it", normalizedEmotion, voiceProvider);
            }
        }

        if (synthesis == null)
            throw new InvalidOperationException("Il servizio TTS non ha restituito alcun risultato");

        byte[] audioBytes;
        if (!string.IsNullOrWhiteSpace(synthesis.AudioBase64))
        {
            audioBytes = Convert.FromBase64String(synthesis.AudioBase64);
        }
        else if (!string.IsNullOrWhiteSpace(synthesis.AudioUrl))
        {
            audioBytes = await _ttsService.DownloadAudioAsync(synthesis.AudioUrl);
        }
        else
        {
            throw new InvalidOperationException("Il servizio TTS non ha restituito dati audio");
        }

        int? durationMs = null;
        if (synthesis.DurationSeconds.HasValue)
        {
            durationMs = (int)Math.Max(0, Math.Round(synthesis.DurationSeconds.Value * 1000.0));
        }

        return (audioBytes, durationMs);
    }

    private async Task<bool> TryEnsureTtsServiceReadyAsync()
    {
        var isHealthy = _healthMonitor != null
            ? await _healthMonitor.CheckTtsHealthAsync()
            : await StartupTasks.CheckTtsHealthAsync(_logger);
        if (isHealthy)
        {
            return true;
        }

        _logger?.LogWarning("Servizio TTS non raggiungibile: avvio tentativo di riattivazione automatica.");
        return await StartupTasks.TryRestartTtsAsync(_healthMonitor, _logger);
    }

    private async Task<(bool success, string? message)> EnsureTtsReadyBeforeGenerationAsync(long storyId, string runId)
    {
        async Task<bool> CheckHealthWithLogAsync(string label)
        {
            _customLogger?.Append(runId, $"[{storyId}] {label}: GET /health...");
            try
            {
                var ok = _healthMonitor != null
                    ? await _healthMonitor.CheckTtsHealthAsync()
                    : await StartupTasks.CheckTtsHealthAsync(_logger);
                _customLogger?.Append(runId, $"[{storyId}] {label}: /health => {(ok ? "OK" : "KO")}");
                return ok;
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId, $"[{storyId}] {label}: errore su /health: {ex.Message}");
                _logger?.LogWarning(ex, "Errore health-check TTS durante precheck per storia {StoryId}", storyId);
                return false;
            }
        }

        var isHealthy = await CheckHealthWithLogAsync("Precheck iniziale");
        if (isHealthy)
        {
            _customLogger?.Append(runId, $"[{storyId}] Precheck TTS OK (/health).");
            return (true, null);
        }

        const int maxAttempts = 3;
        const int retryDelayMs = 5000;

        _customLogger?.Append(runId, $"[{storyId}] Precheck TTS fallito (/health non raggiungibile). Avvio riattivazione automatica...");
        _logger?.LogWarning("Precheck TTS fallito per storia {StoryId}: /health non raggiungibile. Avvio fino a {MaxAttempts} tentativi di riattivazione.", storyId, maxAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            _customLogger?.Append(runId, $"[{storyId}] Tentativo riattivazione TTS {attempt}/{maxAttempts}...");
            var restarted = false;
            try
            {
                restarted = await StartupTasks.TryRestartTtsAsync(_healthMonitor, _logger);
                _customLogger?.Append(runId, $"[{storyId}] Tentativo {attempt}/{maxAttempts}: avvio server TTS => {(restarted ? "OK" : "KO")}");
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId, $"[{storyId}] Tentativo {attempt}/{maxAttempts}: errore avvio server TTS: {ex.Message}");
                _logger?.LogWarning(ex, "Errore avvio TTS al tentativo {Attempt}/{MaxAttempts} per storia {StoryId}", attempt, maxAttempts, storyId);
            }

            if (!restarted)
            {
                _customLogger?.Append(runId, $"[{storyId}] Tentativo {attempt}/{maxAttempts}: avvio servizio non confermato, attendo comunque {retryDelayMs / 1000}s e riprovo health.");
            }

            await Task.Delay(retryDelayMs);

            isHealthy = await CheckHealthWithLogAsync($"Tentativo {attempt}/{maxAttempts}");
            if (isHealthy)
            {
                _customLogger?.Append(runId, $"[{storyId}] Riattivazione TTS completata al tentativo {attempt}/{maxAttempts}.");
                return (true, null);
            }

            _customLogger?.Append(runId, $"[{storyId}] Tentativo {attempt}/{maxAttempts}: /health ancora non raggiungibile.");
        }

        var err = $"Server TTS non raggiungibile: /health fallito dopo {maxAttempts} tentativi (attesa {retryDelayMs / 1000}s tra i tentativi).";
        _customLogger?.Append(runId, $"[{storyId}] {err}");
        return (false, err);
    }

    private string ResolveVoiceProvider(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return "localtts";
        }

        try
        {
            var voice = _database.GetTtsVoiceByVoiceId(voiceId);
            if (!string.IsNullOrWhiteSpace(voice?.Provider))
            {
                return voice.Provider!.Trim();
            }
        }
        catch
        {
            // Fallback to localtts
        }

        return "localtts";
    }

    /// <summary>
    /// Normalizes emotion string to a valid TTS API value.
    /// Falls back to "neutral" if not recognized.
    /// </summary>
    private static string NormalizeEmotionForTts(string? emotion)
    {
        if (string.IsNullOrWhiteSpace(emotion))
            return "neutral";

        var trimmed = emotion.Trim().ToLowerInvariant();

        // Already valid
        if (ValidTtsEmotions.Contains(trimmed))
            return trimmed;

        // Common Italian -> English mappings
        return trimmed switch
        {
            "felice" or "gioioso" or "allegro" or "entusiasta" or "euforico" => "happy",
            "triste" or "malinconico" or "afflitto" or "depresso" => "sad",
            "arrabbiato" or "furioso" or "irato" or "adirato" or "irritato" => "angry",
            "spaventato" or "impaurito" or "terrorizzato" or "ansioso" or "preoccupato" => "fearful",
            "disgustato" or "nauseato" or "schifato" => "disgusted",
            "sorpreso" or "stupito" or "meravigliato" or "sbalordito" or "incredulo" => "surprised",
            "neutrale" or "calmo" or "sereno" or "tranquillo" or "riflessivo" or "pensieroso" 
                or "solenne" or "grave" or "serio" or "determinato" or "deciso" => "neutral",
            _ => "neutral" // Fallback for unrecognized emotions
        };
    }

    private static int? TryGetWavDuration(byte[] audioBytes)
    {
        try
        {
            using var stream = new MemoryStream(audioBytes, writable: false);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != 0x46464952) // "RIFF"
                return null;

            reader.ReadUInt32(); // chunk size
            if (reader.ReadUInt32() != 0x45564157) // "WAVE"
                return null;

            int byteRate = 0;
            int dataSize = 0;

            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                var chunkId = reader.ReadUInt32();
                var chunkSize = reader.ReadInt32();

                if (chunkSize < 0 || reader.BaseStream.Position + chunkSize > reader.BaseStream.Length)
                    return null;

                if (chunkId == 0x20746d66) // "fmt "
                {
                    var fmtData = reader.ReadBytes(chunkSize);
                    if (chunkSize >= 12)
                    {
                        byteRate = BitConverter.ToInt32(fmtData, 8);
                    }
                }
                else if (chunkId == 0x61746164) // "data"
                {
                    dataSize = chunkSize;
                    reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                    break;
                }
                else
                {
                    reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
                }

                if ((chunkSize & 1) == 1)
                {
                    reader.BaseStream.Seek(1, SeekOrigin.Current);
                }
            }

            if (byteRate > 0 && dataSize > 0)
            {
                var seconds = (double)dataSize / byteRate;
                return (int)Math.Max(0, Math.Round(seconds * 1000.0));
            }
        }
        catch
        {
            // ignore and fall back to duration from API
        }

        return null;
    }

    private static bool TryReadNumber(JsonObject entry, string propertyName, out double value)
    {
        value = 0;
        // Try both casings: as-is and lowercase
        if (!entry.TryGetPropertyValue(propertyName, out var node) || node == null)
        {
            var lowerName = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
            if (!entry.TryGetPropertyValue(lowerName, out node) || node == null)
                return false;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<double>(out value))
                return true;

            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                value = intValue;
                return true;
            }
        }

        return double.TryParse(node.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string? ReadString(JsonObject entry, string propertyName)
    {
        // Try both casings: as-is and lowercase
        if (!entry.TryGetPropertyValue(propertyName, out var node) || node == null)
        {
            var lowerName = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
            if (!entry.TryGetPropertyValue(lowerName, out node) || node == null)
                return null;
        }

        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var value))
                return value;
        }

        return node.ToString();
    }

    private static int ComputePhraseGapMs(
        JsonObject currentEntry,
        JsonObject? nextEntry,
        int defaultPhraseGapMs,
        int commaAttributionGapMs)
    {
        if (nextEntry == null)
        {
            return defaultPhraseGapMs;
        }

        if (commaAttributionGapMs < defaultPhraseGapMs &&
            LooksLikeCommaAttributionTransition(currentEntry, nextEntry))
        {
            return commaAttributionGapMs;
        }

        return defaultPhraseGapMs;
    }

    private static bool LooksLikeCommaAttributionTransition(JsonObject currentEntry, JsonObject nextEntry)
    {
        var currentText = (ReadString(currentEntry, "text") ?? ReadString(currentEntry, "Text") ?? string.Empty).TrimEnd();
        if (string.IsNullOrWhiteSpace(currentText) || !currentText.EndsWith(",", StringComparison.Ordinal))
        {
            return false;
        }

        var nextTextRaw = (ReadString(nextEntry, "text") ?? ReadString(nextEntry, "Text") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(nextTextRaw))
        {
            return false;
        }

        var nextText = Regex.Replace(nextTextRaw, @"^\p{P}+", string.Empty).TrimStart().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(nextText))
        {
            return false;
        }

        var isAttributionStart =
            nextText.StartsWith("disse ", StringComparison.Ordinal) ||
            nextText.StartsWith("chiese ", StringComparison.Ordinal) ||
            nextText.StartsWith("rispose ", StringComparison.Ordinal) ||
            nextText.StartsWith("replicò ", StringComparison.Ordinal) ||
            nextText.StartsWith("mormorò ", StringComparison.Ordinal) ||
            nextText.StartsWith("sussurrò ", StringComparison.Ordinal) ||
            nextText.StartsWith("urlò ", StringComparison.Ordinal) ||
            nextText.StartsWith("ordinò ", StringComparison.Ordinal) ||
            nextText.StartsWith("aggiunse ", StringComparison.Ordinal) ||
            nextText.StartsWith("commentò ", StringComparison.Ordinal) ||
            nextText.StartsWith("proseguì ", StringComparison.Ordinal);

        if (!isAttributionStart)
        {
            return false;
        }

        var nextCharacter = (ReadString(nextEntry, "character") ?? ReadString(nextEntry, "Character") ?? string.Empty).Trim();
        var isNarrator = string.Equals(nextCharacter, "Narratore", StringComparison.OrdinalIgnoreCase);
        return isNarrator || nextText.Length <= 120;
    }

    private sealed record CharacterVoiceInfo(string Name, string? VoiceId, string? Gender);

    // TODO: Implement auto-advancement feature with idle detection
    public bool IsAutoAdvancementEnabled()
    {
        try
        {
            var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            if (File.Exists(appSettingsPath))
            {
                var json = File.ReadAllText(appSettingsPath);
                var root = JsonNode.Parse(json) as JsonObject;
                var enabledFromFile = root?["AutomaticOperations"]?["Enabled"]?.GetValue<bool?>();
                if (enabledFromFile.HasValue)
                {
                    return enabledFromFile.Value;
                }
            }

            return _idleAutoOptions?.CurrentValue?.Enabled ?? false;
        }
        catch
        {
            return false;
        }
    }
    
    public void SetAutoAdvancementEnabled(bool enabled)
    {
        try
        {
            var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            if (!File.Exists(appSettingsPath))
            {
                return;
            }

            var json = File.ReadAllText(appSettingsPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null)
            {
                return;
            }

            var autoNode = root["AutomaticOperations"] as JsonObject ?? new JsonObject();
            autoNode["Enabled"] = enabled;
            root["AutomaticOperations"] = autoNode;

            var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(appSettingsPath, updated);
        }
        catch
        {
            // best-effort
        }
    }

    public string GetAutoAdvancementMode()
    {
        try
        {
            var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            if (File.Exists(appSettingsPath))
            {
                var json = File.ReadAllText(appSettingsPath);
                var root = JsonNode.Parse(json) as JsonObject;
                var modeFromFile = root?["AutomaticOperations"]?["AutoAdvancementMode"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(modeFromFile))
                {
                    return NormalizeAutoAdvancementMode(modeFromFile);
                }
            }

            var mode = _idleAutoOptions?.CurrentValue?.AutoAdvancementMode;
            return NormalizeAutoAdvancementMode(mode);
        }
        catch
        {
            return "series";
        }
    }

    public bool IsMonomodelModeEnabled()
    {
        try
        {
            return _monomodelOptions?.CurrentValue?.Enabled ?? false;
        }
        catch
        {
            return false;
        }
    }

    public string GetMonomodelModeModelDescription()
    {
        try
        {
            return (_monomodelOptions?.CurrentValue?.ModelDescription ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    public int GetCommandDispatcherMaxParallelCommands()
    {
        try
        {
            if (_commandDispatcher is CommandDispatcher concreteDispatcher)
            {
                return concreteDispatcher.GetConfiguredQueueParallelism();
            }
        }
        catch
        {
            // best-effort
        }

        try
        {
            var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            if (File.Exists(appSettingsPath))
            {
                var json = File.ReadAllText(appSettingsPath);
                var root = JsonNode.Parse(json) as JsonObject;
                var configured = root?["CommandDispatcher"]?["MaxParallelCommands"]?.GetValue<int?>();
                if (configured.HasValue && configured.Value > 0)
                {
                    return Math.Clamp(configured.Value, 1, 64);
                }
            }
        }
        catch
        {
            // best-effort
        }

        return 1;
    }

    public bool SetCommandDispatcherMaxParallelCommands(int maxParallelCommands, out int appliedValue)
    {
        appliedValue = Math.Clamp(maxParallelCommands, 1, 64);

        try
        {
            var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            if (!File.Exists(appSettingsPath))
            {
                return false;
            }

            var json = File.ReadAllText(appSettingsPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null)
            {
                return false;
            }

            var dispatcherNode = root["CommandDispatcher"] as JsonObject ?? new JsonObject();
            dispatcherNode["MaxParallelCommands"] = appliedValue;
            root["CommandDispatcher"] = dispatcherNode;

            var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(appSettingsPath, updated);

            if (_commandDispatcher is CommandDispatcher concreteDispatcher)
            {
                appliedValue = concreteDispatcher.SetConfiguredQueueParallelism(appliedValue);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SetMonomodelMode(bool enabled, string? modelDescription)
    {
        try
        {
            var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            if (!File.Exists(appSettingsPath))
            {
                return;
            }

            var json = File.ReadAllText(appSettingsPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null)
            {
                return;
            }

            var monoNode = root["MonomodelMode"] as JsonObject ?? new JsonObject();
            monoNode["Enabled"] = enabled;
            monoNode["ModelDescription"] = (modelDescription ?? string.Empty).Trim();
            root["MonomodelMode"] = monoNode;

            var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(appSettingsPath, updated);
        }
        catch
        {
            // best-effort
        }
    }

    public void SetAutoAdvancementMode(string? mode)
    {
        try
        {
            var appSettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
            if (!File.Exists(appSettingsPath))
            {
                return;
            }

            var json = File.ReadAllText(appSettingsPath);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null)
            {
                return;
            }

            var autoNode = root["AutomaticOperations"] as JsonObject ?? new JsonObject();
            autoNode["AutoAdvancementMode"] = NormalizeAutoAdvancementMode(mode);
            root["AutomaticOperations"] = autoNode;

            var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(appSettingsPath, updated);
        }
        catch
        {
            // best-effort
        }
    }

    private static string NormalizeAutoAdvancementMode(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized == "nre")
        {
            return "nre";
        }

        if (normalized == "nre_manual")
        {
            return "nre_manual";
        }

        if (normalized == "complete_existing_first")
        {
            return "complete_existing_first";
        }

        if (normalized == "vatican_horror")
        {
            return "vatican_horror";
        }

        if (normalized == "complete_existing_then_vatican")
        {
            return "complete_existing_then_vatican";
        }

        return "series";
    }

    internal static class StoriesServiceDefaults
    {
        public const string EvaluatorTextProtocolInstructions =
@"Sei un valutatore narrativo tecnico, rigoroso e non compiacente.

    Devi restituire la valutazione completa nel formato seguente, senza aggiungere nulla prima o dopo.

    Formato OBBLIGATORIO:

    Coerenza narrativa
    <numero da 1 a 5>
    <breve spiegazione tecnica (1-2 frasi)>

    Originalita
    <numero da 1 a 5>
    <breve spiegazione tecnica (1-2 frasi)>

    Impatto emotivo
    <numero da 1 a 5>
    <breve spiegazione tecnica (1-2 frasi)>

    Azione
    <numero da 1 a 5>
    <breve spiegazione tecnica (1-2 frasi)>

    ========================
    VINCOLI ASSOLUTI
    ========================

    - NON riassumere la storia
    - NON suggerire miglioramenti
    - NON usare markup o elenchi
    - NON usare linguaggio morale o emotivo
    - NON aggiungere spiegazioni fuori formato";
        public static string GetDefaultTtsStructuredInstructions() => @"Leggi attentamente il testo del chunk e trascrivilo integralmente nel formato seguente, senza riassumere o saltare frasi, senza aggiungere note o testo extra.

Usa SOLO queste sezioni ripetute nell'ordine del testo:

[NARRATORE]
Testo narrativo cos� come appare nel chunk

[PERSONAGGIO: NomePersonaggio | EMOZIONE: emotion]
Battuta di dialogo cos� come appare nel chunk

Regole:
- NON cambiare lingua, NON abbreviare, NON riassumere.
- Se non e chiaramente un dialogo, usa NARRATORE.
- EMOZIONE: usa una tra neutral, happy, sad, angry, fearful, disgusted, surprised (default neutral se non indicata).
- Non aggiungere spiegazioni o altro testo fuori dai blocchi.
- Copri tutto il chunk, pi� blocchi uno dopo l'altro finch� il chunk e esaurito.";
    }
}



