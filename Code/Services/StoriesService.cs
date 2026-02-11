using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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
    private readonly IOptionsMonitor<AutomaticOperationsOptions>? _idleAutoOptions;
    private readonly IOptionsMonitor<StoryTaggingPipelineOptions>? _storyTaggingOptions;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IServiceHealthMonitor? _healthMonitor;
    private readonly StoryMainCommands _mainCommands;
    private readonly ConcurrentDictionary<long, StatusChainState> _statusChains = new();
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
        IOptionsMonitor<AutomaticOperationsOptions>? idleAutoOptions = null,
        IServiceScopeFactory? scopeFactory = null,
        IOptionsMonitor<StoryTaggingPipelineOptions>? storyTaggingOptions = null,
        IServiceHealthMonitor? healthMonitor = null)
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
        _scopeFactory = scopeFactory;
        _idleAutoOptions = idleAutoOptions;
        _storyTaggingOptions = storyTaggingOptions;
        _healthMonitor = healthMonitor;
        _mainCommands = new StoryMainCommands(this);
    }

    internal DatabaseService Database => _database;
    internal ILogger<StoriesService>? Logger => _logger;
    internal ILangChainKernelFactory? KernelFactory => _kernelFactory;
    internal ICustomLogger? CustomLogger => _customLogger;
    internal ICommandDispatcher? CommandDispatcher => _commandDispatcher;
    internal CommandTuningOptions Tuning => _tuning;
    internal IServiceScopeFactory? ScopeFactory => _scopeFactory;

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

        var newStoryId = InsertSingleStory(
            story.Prompt ?? string.Empty,
            revised,
            story.ModelId,
            story.AgentId,
            score: 0.0,
            eval: null,
            approved: 0,
            statusId: null,
            memoryKey: null,
            title: cloneTitle,
            storyId: null,
            serieId: story.SerieId,
            serieEpisode: story.SerieEpisode);

        return (true, newStoryId, $"Nuova storia {newStoryId} creata dalla revisione.");
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

    public string? EnqueueAllNextStatusCommand(long storyId, string trigger, int priority = 2, bool ignoreActiveChain = false)
    {
        var story = GetStoryById(storyId);
        if (story == null)
            return null;

        return EnqueueAllNextStatusCommand(story, trigger, priority, ignoreActiveChain);
    }

    public string? EnqueueAllNextStatusCommand(StoryRecord story, string trigger, int priority = 2, bool ignoreActiveChain = false)
    {
        if (story == null) return null;
        if (_commandDispatcher == null) return null;

        if (ignoreActiveChain)
        {
            StopStatusChain(story.Id);
        }

        return EnqueueStatusChain(story.Id);
    }

    private sealed class StatusChainState
    {
        public string ChainId { get; }
        public int? LastEnqueuedStatusId { get; set; }

        public StatusChainState(string chainId)
        {
            ChainId = chainId;
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

    public string? EnqueueStatusChain(long storyId)
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
        var state = new StatusChainState(chainId);
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
                
                return new CommandResult(success, message);
            },
            runId: runId,
            threadScope: $"story/status_chain/{story.Id}",
            metadata: metadata,
            priority: 2);

        state.LastEnqueuedStatusId = next.Id;
        return true;
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

    public async Task<(bool success, double score, string? error)> EvaluateStoryWithAgentAsync(long storyId, int agentId)
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
        using var scope = LogScope.Push($"story_evaluation_{story.Id}", evaluationOpId, null, null, agent.Name);

        try
        {

            async Task<(bool success, double score, string? error)> ExecuteEvaluationWithModelAsync(
                string evaluationModelName,
                int evaluationModelId,
                string? instructionsOverride,
                double? topPOverride,
                int? topKOverride)
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

                    var chatBridge = _kernelFactory.CreateChatBridge(
                        evaluationModelName,
                        agent.Temperature,
                        topPOverride ?? agent.TopP,
                        agent.RepeatPenalty,
                        topKOverride ?? agent.TopK,
                        agent.RepeatLastN,
                        agent.NumPredict);

                    var isStoryEvaluatorTextProtocol = agent.Role?.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) ?? false;
                    var baseSystemMessage = (isStoryEvaluatorTextProtocol
                        ? StoriesServiceDefaults.EvaluatorTextProtocolInstructions
                        : (!string.IsNullOrWhiteSpace(agent.Instructions) ? agent.Instructions : ComposeSystemMessage(agent)))
                        ?? string.Empty;
                    var systemMessage = !string.IsNullOrWhiteSpace(instructionsOverride)
                        ? instructionsOverride!
                        : baseSystemMessage;

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
                            chatBridge,
                            systemMessage,
                            modelId: evaluationModelId,
                            agentId: agent.Id).ConfigureAwait(false);

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

                        var reactLoop = new ReActLoopOrchestrator(
                            orchestrator,
                            _customLogger,
                            runId: runId,
                            modelBridge: chatBridge,
                            systemMessage: systemMessage,
                            responseChecker: _responseChecker,
                            agentRole: agent.Role);

                        var reactResult = await reactLoop.ExecuteAsync(prompt);
                        if (!reactResult.Success)
                        {
                            var error = reactResult.Error ?? "Valutazione fallita";
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
                            TryLogEvaluationResult(runId, storyId, agent?.Name, success: false, msg);
                            return (false, 0, msg);
                        }

                        var score = globalCoherence.GlobalCoherenceValue * 10; // Convert 0-1 to 0-10 scale
                        _customLogger?.Append(runId, $"[{storyId}] Valutazione di coerenza completata. Score: {score:F2}");
                        TryLogEvaluationResult(runId, storyId, agent?.Name, success: true, $"Valutazione di coerenza completata. Score: {score:F2}");
                        var allowNext = true;
                        var (count, _) = _database.GetStoryEvaluationStats(storyId);
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
                            TryLogEvaluationResult(runId, storyId, agent?.Name, success: false, msg);
                            return (false, 0, msg);
                        }

                        var avgScore = afterEvaluations.Average(e => e.TotalScore);
                        _customLogger?.Append(runId, $"[{storyId}] Valutazione completata. Score medio: {avgScore:F2}");
                        TryLogEvaluationResult(runId, storyId, agent?.Name, success: true, $"Valutazione completata. Score medio: {avgScore:F2}");

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
                                        _customLogger!);

                                    _commandDispatcher.Enqueue(
                                        "SummarizeStory",
                                        async ctx =>
                                        {
                                            bool success = await cmd.ExecuteAsync(ctx.CancellationToken);
                                            return new CommandResult(success, success ? "Summary generated" : "Failed to generate summary");
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
                        var (count, _) = _database.GetStoryEvaluationStats(storyId);
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
                    TryLogEvaluationResult(runId, storyId, agent?.Name, success: false, ex.Message);
                    return (false, 0, ex.Message);
                }
            }

            var primaryResult = await ExecuteEvaluationWithModelAsync(
                modelName,
                agent.ModelId.Value,
                agent.Instructions,
                agent.TopP,
                agent.TopK);

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
                        string.IsNullOrWhiteSpace(modelRole.Instructions) ? agent.Instructions : modelRole.Instructions,
                        modelRole.TopP ?? agent.TopP,
                        modelRole.TopK ?? agent.TopK);
                },
                validateResult: r => r.success,
                shouldTryModelRole: modelRole =>
                {
                    var name = modelRole.Model?.Name;
                    return !string.IsNullOrWhiteSpace(name) && triedModelNames.Add(name);
                });

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
            TryLogEvaluationResult(runId, storyId, agent?.Name, success: false, ex.Message);
            return (false, 0, ex.Message);
        }
        finally
        {
            _customLogger?.MarkCompleted(runId);
        }

    }

    private async Task<(bool success, double score, string? error)> EvaluateStoryWithTextProtocolAsync(
        StoryRecord story,
        LangChainChatBridge chatBridge,
        string systemMessage,
        int? modelId,
        int agentId)
    {
        if (story == null) return (false, 0, "Storia non trovata");
        var storyText = !string.IsNullOrWhiteSpace(story.StoryRevised)
            ? story.StoryRevised
            : story.StoryRaw;

        if (string.IsNullOrWhiteSpace(storyText)) return (false, 0, "Storia non trovata o priva di contenuto");

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
            Content = $"TESTO:\n\n{storyText}\n\nVALUTA"
        });

        var parsed = new ParsedEvaluation(0, string.Empty, 0, string.Empty, 0, string.Empty, 0, string.Empty);
        string? parseError = null;
        string evalText = string.Empty;

        var evalOk = false;
        for (var attempt = 1; attempt <= maxAttemptsFinalEvaluation; attempt++)
        {
            var stepId = RequestIdGenerator.Generate();
            _logger?.LogInformation("[StepID: {StepId}][Story: {StoryId}] Valutazione tentativo {Attempt}/{Max}", stepId, story.Id, attempt, maxAttemptsFinalEvaluation);

            var evalRawJson = await chatBridge.CallModelWithToolsAsync(messages, tools: new List<Dictionary<string, object>>(), skipResponseChecker: true).ConfigureAwait(false);
            var (evalResponseText, _) = LangChainChatBridge.ParseChatResponse(evalRawJson);
            evalText = NormalizeEvaluatorOutput((evalResponseText ?? string.Empty));
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
            return (false, 0, parseError ?? "Formato valutazione non valido.");
        }

        var totalScore = (double)(parsed.NarrativeScore10 + parsed.OriginalityScore10 + parsed.EmotionalScore10 + parsed.ActionScore10);
        try
        {
            _database.AddStoryEvaluation(
                story.Id,
                parsed.NarrativeScore10, parsed.NarrativeExplanation,
                parsed.OriginalityScore10, parsed.OriginalityExplanation,
                parsed.EmotionalScore10, parsed.EmotionalExplanation,
                parsed.ActionScore10, parsed.ActionExplanation,
                totalScore,
                rawJson: evalText,
                modelId: modelId,
                agentId: agentId);
        }
        catch (Exception ex)
        {
            return (false, 0, $"Errore nel salvataggio della valutazione: {ex.Message}");
        }

        return (true, totalScore, null);
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

        return ApplyStatusTransitionWithCleanup(latestStory, "evaluated", runId);
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
            if (string.IsNullOrWhiteSpace(seg)) return (0, string.Empty);

            var mScore = Regex.Match(seg, @"\b([1-5])\b");
            if (!mScore.Success) return (0, string.Empty);
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

        if (nScore5 < 1 || oScore5 < 1 || eScore5 < 1 || aScore5 < 1)
        {
            error = "Formato valutazione non valido: punteggi non trovati o fuori range (atteso 1-5 per sezione).";
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
                ThreadScope = runId,
                ThreadId = 0,
                AgentName = agentName,
                Result = success ? "SUCCESS" : "FAILED"
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
        using var scope = LogScope.Push($"action_evaluation_{story.Id}", evaluationOpId, null, null, agent.Name);

        try
        {
            var orchestrator = _kernelFactory.CreateOrchestrator(modelName, allowedPlugins, agent.Id);

            var chatBridge = _kernelFactory.CreateChatBridge(
                modelName,
                agent.Temperature,
                agent.TopP,
                agent.RepeatPenalty,
                agent.TopK,
                agent.RepeatLastN,
                agent.NumPredict);

            var systemMessage = !string.IsNullOrWhiteSpace(agent.Instructions)
                ? agent.Instructions
                : ComposeSystemMessage(agent);

            var prompt = BuildActionPacingPrompt(story);

            var reactLoop = new ReActLoopOrchestrator(
                orchestrator,
                _customLogger,
                runId: runId,
                modelBridge: chatBridge,
                systemMessage: systemMessage,
                responseChecker: _responseChecker,
                agentRole: agent.Role);

            var reactResult = await reactLoop.ExecuteAsync(prompt);
            if (!reactResult.Success)
            {
                var error = reactResult.Error ?? "Valutazione azione/ritmo fallita";
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

            var folderName = !string.IsNullOrWhiteSpace(story.Folder)
                ? story.Folder
                : new DirectoryInfo(EnsureStoryFolder(story)).Name;

            var basePriority = Math.Max(1, priority);

            void EnqueueIfNotQueued(string operationName, string runPrefix, Func<CommandContext, Task<CommandResult>> handler, int opPriority)
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
                    priority: Math.Max(1, opPriority));
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

            EnqueueIfNotQueued(
                "normalize_characters",
                $"normalize_characters_{storyId}_",
                async _ =>
                {
                    var (ok, msg) = await NormalizeCharacterNamesAsync(storyId);
                    return new CommandResult(ok, ok ? "Nomi personaggi normalizzati." : msg);
                },
                basePriority + 1);

            EnqueueIfNotQueued(
                "assign_voices",
                $"assign_voices_{storyId}_",
                async _ =>
                {
                    var (ok, msg) = await AssignVoicesAsync(storyId);
                    return new CommandResult(ok, ok ? "Voci assegnate." : msg);
                },
                basePriority + 2);

            EnqueueIfNotQueued(
                "normalize_sentiments",
                $"normalize_sentiments_{storyId}_",
                async _ =>
                {
                    var (ok, msg) = await NormalizeSentimentsAsync(storyId);
                    return new CommandResult(ok, ok ? "Sentimenti normalizzati." : msg);
                },
                basePriority + 3);

            var ttsRunId = EnqueueGenerateTtsAudioCommand(storyId, trigger: trigger, priority: basePriority + 4);
            _ = ttsRunId;

            EnqueueIfNotQueued(
                "generate_music",
                $"generate_music_{storyId}_",
                async ctx =>
                {
                    var (ok, err) = await GenerateMusicForStoryAsync(storyId, folderName, ctx.RunId);
                    return new CommandResult(ok, ok ? "Generazione musica completata." : err);
                },
                basePriority + 5);

            EnqueueIfNotQueued(
                "generate_ambience_audio",
                $"generate_ambience_audio_{storyId}_",
                async ctx =>
                {
                    var (ok, err) = await GenerateAmbienceForStoryAsync(storyId, folderName, ctx.RunId);
                    return new CommandResult(ok, ok ? "Generazione audio ambientale completata." : err);
                },
                basePriority + 6);

            EnqueueIfNotQueued(
                "generate_fx_audio",
                $"generate_fx_audio_{storyId}_",
                async ctx =>
                {
                    var (ok, err) = await GenerateFxForStoryAsync(storyId, folderName, ctx.RunId);
                    return new CommandResult(ok, ok ? "Generazione effetti sonori completata." : err);
                },
                basePriority + 7);

            EnqueueIfNotQueued(
                "mix_final_audio",
                $"mix_final_audio_{storyId}_",
                async ctx =>
                {
                    var (ok, err) = await MixFinalAudioForStoryAsync(storyId, folderName, ctx.RunId);
                    return new CommandResult(ok, ok ? "Mixaggio audio completato." : err);
                },
                basePriority + 8);

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

            void EnqueueIfNotQueued(string operationName, string runPrefix, Func<CommandContext, Task<CommandResult>> handler)
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
                    priority: priority);
            }

            EnqueueIfNotQueued(
                "generate_music",
                $"generate_music_{storyId}_",
                async ctx =>
                {
                    var (ok, err) = await GenerateMusicForStoryAsync(storyId, folderName, ctx.RunId);
                    return new CommandResult(ok, ok ? "Generazione musica completata." : err);
                });

            var autoAmbience = _audioGenerationOptions?.CurrentValue?.Ambience?.AutolaunchNextCommand ?? true;
            if (autoAmbience)
            {
                EnqueueIfNotQueued(
                    "generate_ambience_audio",
                    $"generate_ambience_audio_{storyId}_",
                    async ctx =>
                    {
                        var (ok, err) = await GenerateAmbienceForStoryAsync(storyId, folderName, ctx.RunId);
                        return new CommandResult(ok, ok ? "Generazione audio ambientale completata." : err);
                    });
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
                    });
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

                        var revisor = _database.ListAgents()
                            .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                                        a.Role.Equals("revisor", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(a => a.Id)
                            .FirstOrDefault();

                        // If no revisor available, fall back to raw -> revised so the pipeline can continue.
                        if (revisor == null)
                        {
                            _database.UpdateStoryRevised(storyId, StripMarkdown(raw));
                            allowNext = ApplyStatusTransitionWithCleanup(story, "revised", runId);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, allowNext
                                ? "No revisor configured: copied raw to story_revised and enqueued evaluations."
                                : "No revisor configured: copied raw to story_revised.");
                        }

                        if (!revisor.ModelId.HasValue)
                        {
                            _database.UpdateStoryRevised(storyId, StripMarkdown(raw));
                            allowNext = ApplyStatusTransitionWithCleanup(story, "revised", runId);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped_no_model", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, allowNext
                                ? "Revisor has no model: copied raw to story_revised and enqueued evaluations."
                                : "Revisor has no model: copied raw to story_revised.");
                        }

                        var modelInfo = _database.GetModelInfoById(revisor.ModelId.Value);
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

                        var systemPrompt = (revisor.Prompt ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(systemPrompt))
                        {
                            _database.UpdateStoryRevised(storyId, StripMarkdown(raw));
                            allowNext = ApplyStatusTransitionWithCleanup(story, "revised", runId);
                            EnqueueAutomaticStoryEvaluations(storyId, trigger: "revision_skipped_empty_prompt", priority: Math.Max(priority + 1, 3));
                            return new CommandResult(true, allowNext
                                ? "Revisor prompt empty: copied raw to story_revised and enqueued evaluations."
                                : "Revisor prompt empty: copied raw to story_revised.");
                        }

                        var bridge = _kernelFactory.CreateChatBridge(
                            modelInfo.Name,
                            revisor.Temperature,
                            revisor.TopP,
                            revisor.RepeatPenalty,
                            revisor.TopK,
                            revisor.RepeatLastN,
                            revisor.NumPredict);

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

                            // IMPORTANT: do not send any conversation history. Only (systemPrompt + current chunk).
                            var systemPromptWithChunk = systemPrompt + $"\n\n(chunk: {i + 1}/{chunks.Count})";
                            var messages = new List<ConversationMessage>
                            {
                                new ConversationMessage { Role = "system", Content = systemPromptWithChunk },
                                new ConversationMessage { Role = "user", Content = chunk.Text }
                            };

                            var stepId = RequestIdGenerator.Generate();
                            _logger?.LogInformation("[StepID: {StepId}][Story: {StoryId}] Revisione chunk {ChunkNum}/{Total}", stepId, storyId, i + 1, chunks.Count);

                            var responseJson = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ctx.CancellationToken);
                            var (textContent, _) = LangChainChatBridge.ParseChatResponse(responseJson);
                            var revisedChunk = (textContent ?? string.Empty).Trim();
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

    public int EnqueueStoryEvaluations(long storyId, string trigger, int priority = 2, int maxEvaluators = 2)
    {
        try
        {
            if (storyId <= 0) return 0;
            if (_commandDispatcher == null) return 0;

            // De-dup: skip if any evaluation already queued/running for this story.
            try
            {
                var alreadyQueued = _commandDispatcher.GetActiveCommands().Any(s =>
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
            return await GenerateAmbienceAudioInternalAsync(context, dispatcherRunId);
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
            });

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

        var bridge = _kernelFactory.CreateChatBridge(
            modelName,
            temperature,
            topP,
            repeatPenalty,
            topK,
            repeatLastN,
            numPredict);

        var stepId = RequestIdGenerator.Generate();
        _logger?.LogInformation("[StepID: {StepId}] Generazione personaggi da modello {Model}", stepId, modelName);

        var raw = await bridge.CallModelWithToolsAsync(messages, tools: new List<Dictionary<string, object>>());
        var (responseText, _) = LangChainChatBridge.ParseChatResponse(raw);
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

    private static string? ExtractJsonArray(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        var candidate = text.Substring(start, end - start + 1).Trim();
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return candidate;
        }
        catch
        {
            return null;
        }
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
                    "ambientSounds", "AmbientSounds", "ambient_sounds",
                    "fx_description", "FxDescription", "fxDescription",
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
                                    if (!string.IsNullOrWhiteSpace(fn)) DeleteIfExists(Path.Combine(folderPath, fn));

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
                                if (!string.IsNullOrWhiteSpace(fn)) DeleteIfExists(Path.Combine(folderPath, fn));

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

                                var musicFile = ReadString(item, "music_file") ?? ReadString(item, "musicFile") ?? ReadString(item, "MusicFile");
                                if (!string.IsNullOrWhiteSpace(musicFile))
                                {
                                    DeleteIfExists(Path.Combine(folderPath, musicFile));
                                }
                                item.Remove("musicFile");
                                item.Remove("MusicFile");
                                item.Remove("music_file");
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
        if (!string.IsNullOrWhiteSpace(agent.Prompt))
            parts.Add(agent.Prompt);

        if (!string.IsNullOrWhiteSpace(agent.ExecutionPlan))
        {
            var plan = LoadExecutionPlan(agent.ExecutionPlan);
            if (!string.IsNullOrWhiteSpace(plan))
                parts.Add(plan);
        }

        if (!string.IsNullOrWhiteSpace(agent.Instructions))
            parts.Add(agent.Instructions);

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
                metadata["agentName"] = agent.Name ?? role;
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

                var reuse = TryReuseExistingAudioEntry(entry, folderPath, cleanText, characterName, emotion, runId, story.Id, usedFiles);
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
                await File.WriteAllTextAsync(txtFilePath, $"[{characterName}, {emotion}]\n{cleanText}");

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
                var reuse = TryReuseExistingAudioEntry(entry, folderPath, cleanText, characterName, emotion, dispatcherRunId, story.Id, usedFiles);
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
                await File.WriteAllTextAsync(txtFilePath, $"[{characterName}, {emotion}]\n{cleanText}");

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
        string characterName,
        string emotion,
        string runId,
        long storyId,
        HashSet<string> usedFiles)
    {
        var existingFileName = ReadString(entry, "fileName") ??
                               ReadString(entry, "FileName") ??
                               ReadString(entry, "file_name");
        if (string.IsNullOrWhiteSpace(existingFileName))
        {
            return (false, null, 0);
        }

        var filePath = Path.Combine(folderPath, existingFileName);
        var txtPath = Path.ChangeExtension(filePath, ".txt");
        if (!File.Exists(filePath) || !File.Exists(txtPath))
        {
            return (false, null, 0);
        }

        try
        {
            var lines = File.ReadAllLines(txtPath);
            if (lines.Length < 2)
            {
                return (false, null, 0);
            }

            if (!TryParseTtsSidecarHeader(lines[0], out var recordedCharacter, out var recordedEmotion))
            {
                return (false, null, 0);
            }

            if (!string.Equals(recordedCharacter, characterName, StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, 0);
            }

            var normalizedRecordedEmotion = NormalizeEmotionForTts(recordedEmotion);
            var normalizedTargetEmotion = NormalizeEmotionForTts(emotion);
            if (!string.Equals(normalizedRecordedEmotion, normalizedTargetEmotion, StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, 0);
            }

            var recordedText = string.Join("\n", lines.Skip(1));
            if (!string.Equals(NormalizeTextForComparison(recordedText), NormalizeTextForComparison(cleanText), StringComparison.Ordinal))
            {
                return (false, null, 0);
            }
        }
        catch
        {
            return (false, null, 0);
        }

        var duration = TryGetWavDurationFromFile(filePath) ?? 0;
        usedFiles.Add(existingFileName);
        _customLogger?.Append(runId, $"[{storyId}] Riutilizzo file audio esistente {existingFileName} (testo invariato).");
        return (true, existingFileName, duration);
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
        var hasAmbientDefinitions = phraseEntries.Any(e => !string.IsNullOrWhiteSpace(ReadAmbientSoundsDescription(e)));

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
            return (true, msg);
        }

        _customLogger?.Append(runId, $"[{story.Id}] Trovati {ambienceSegments.Count} segmenti ambient sounds da generare");

        // Create AudioCraft tool
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var audioCraft = new AudioCraftTool(httpClient, forceCpu: false, logger: _customLogger);

        Directory.CreateDirectory(folderPath);

        int segmentCounter = 0;
        foreach (var segment in ambienceSegments)
        {
            segmentCounter++;
            var durationSeconds = (int)Math.Ceiling(segment.DurationMs / 1000.0);
            if (durationSeconds < 1) durationSeconds = 1;
            if (durationSeconds > 30) durationSeconds = 30; // AudioCraft limit

            _customLogger?.Append(runId, $"[{story.Id}] Generazione segmento {segmentCounter}/{ambienceSegments.Count}: '{segment.AmbiencePrompt}' ({durationSeconds}s)");

            // Update dispatcher progress if available
            if (_commandDispatcher != null)
            {
                _commandDispatcher.UpdateStep(runId, segmentCounter, ambienceSegments.Count);
            }

            try
            {
                // Call AudioCraft to generate ambient sound
                var audioRequest = new
                {
                    operation = "generate_sound",
                    prompt = segment.AmbiencePrompt,
                    duration = durationSeconds,
                    model = "facebook/audiogen-medium"
                };
                var requestJson = JsonSerializer.Serialize(audioRequest);
                string? resultJson = null;
                
                // Try to execute with retry logic if service is down
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    resultJson = await audioCraft.ExecuteAsync(requestJson);
                }
                catch (Exception executeEx) when (executeEx.Message.Contains("Connection") || executeEx.Message.Contains("No connection") || executeEx is HttpRequestException)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Errore connessione AudioCraft: {executeEx.Message}. Tentativo di riavvio del servizio...");
                    
                    // Try to restart AudioCraft service
                    var restarted = await StartupTasks.TryRestartAudioCraftAsync(_healthMonitor, _logger);
                    if (restarted)
                    {
                        _customLogger?.Append(runId, $"[{story.Id}] Servizio AudioCraft riavviato. Retry generazione segmento {segmentCounter}...");
                        
                        // Retry the request
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            resultJson = await audioCraft.ExecuteAsync(requestJson);
                        }
                        catch (Exception retryEx)
                        {
                            _customLogger?.Append(runId, $"[{story.Id}] Errore anche dopo riavvio servizio: {retryEx.Message}");
                            throw;
                        }
                    }
                    else
                    {
                        _customLogger?.Append(runId, $"[{story.Id}] Impossibile riavviare il servizio AudioCraft. Generazione fallita.");
                        return (false, "Servizio AudioCraft non disponibile e impossibile riavviare");
                    }
                }

                // Parse result to get filename
                var resultNode = JsonNode.Parse(resultJson) as JsonObject;
                if (resultNode?["error"] != null)
                {
                    var err = resultNode["error"]?.ToString() ?? "Errore AudioCraft";
                    _customLogger?.Append(runId, $"[{story.Id}] AudioCraft error: {err}");
                    return (false, err);
                }
                var resultField = resultNode?["result"]?.ToString();

                _customLogger?.Append(runId, $"[{story.Id}] AudioCraft response: {resultJson?.Substring(0, Math.Min(500, resultJson?.Length ?? 0))}");

                string? generatedFileName = null;
                
                // First check LastGeneratedSoundFile (set by AudioCraftTool)
                if (!string.IsNullOrWhiteSpace(audioCraft.LastGeneratedSoundFile))
                {
                    generatedFileName = audioCraft.LastGeneratedSoundFile;
                }
                
                // If not found, try to parse from result field
                if (string.IsNullOrWhiteSpace(generatedFileName) && !string.IsNullOrWhiteSpace(resultField))
                {
                    // Try to extract filename from result (format varies)
                    try
                    {
                        var resultInner = JsonNode.Parse(resultField) as JsonObject;
                        generatedFileName = resultInner?["file"]?.ToString() 
                            ?? resultInner?["filename"]?.ToString()
                            ?? resultInner?["output"]?.ToString();
                    }
                    catch
                    {
                        // resultField might not be valid JSON, check if it's a plain filename
                        if (resultField.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                            resultField.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        {
                            generatedFileName = resultField.Trim();
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(generatedFileName))
                {
                    var err = $"Impossibile determinare il file generato per il segmento {segmentCounter}. Result: {resultJson}";
                    _customLogger?.Append(runId, $"[{story.Id}] {err}");
                    return (false, err);
                }
                generatedFileName = NormalizeAudioCraftFileName(generatedFileName);

                // Download file from AudioCraft server
                var localFileName = $"ambience_{segmentCounter:D3}.wav";
                var localFilePath = Path.Combine(folderPath, localFileName);

                var (downloadOk, downloadError) = await TryDownloadAudioCraftFileAsync(
                    httpClient,
                    generatedFileName,
                    localFilePath,
                    story.Id,
                    runId,
                    cancellationToken);
                if (!downloadOk)
                {
                    return (false, downloadError ?? $"Impossibile scaricare {generatedFileName}");
                }
                _customLogger?.Append(runId, $"[{story.Id}] Salvato: {localFileName}");

                // Save prompt to .txt file with same name
                var promptFileName = Path.ChangeExtension(localFileName, ".txt");
                var promptFilePath = Path.Combine(folderPath, promptFileName);
                await File.WriteAllTextAsync(promptFilePath, segment.AmbiencePrompt);
                _customLogger?.Append(runId, $"[{story.Id}] Salvato prompt: {promptFileName}");

                // Update tts_schema.json: add ambient sound file reference only on the definition record
                if (segment.DefinitionIndex >= 0 && segment.DefinitionIndex < phraseEntries.Count)
                {
                    phraseEntries[segment.DefinitionIndex]["ambient_sound_file"] = localFileName;
                    phraseEntries[segment.DefinitionIndex]["ambientSoundsFile"] = localFileName; // backward compat
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
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
    }

    private sealed record AmbienceSegment(string AmbiencePrompt, int StartMs, int DurationMs, int DefinitionIndex);

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
        var definitionPoints = new List<(int Index, string Prompt, int StartMs)>();
        for (int i = 0; i < entries.Count; i++)
        {
            var ambientSounds = ReadAmbientSoundsDescription(entries[i]);
            if (string.IsNullOrWhiteSpace(ambientSounds)) continue;

            var startMs = ReadEntryStartMs(entries[i]);
            definitionPoints.Add((i, ambientSounds!.Trim(), startMs));
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
                segments.Add(new AmbienceSegment(current.Prompt, segmentStartMs, duration, current.Index));
            }
        }

        return segments;
    }

    private static string? ReadAmbientSoundsDescription(JsonObject entry)
    {
        // Read from standardized snake_case first, then legacy/camelCase
        return ReadString(entry, "ambient_sound_description") ??
               ReadString(entry, "ambientSoundDescription") ??
               ReadString(entry, "AmbientSoundDescription") ??
               ReadString(entry, "ambientSounds") ??
               ReadString(entry, "AmbientSounds") ??
               ReadString(entry, "ambient_sounds");
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
        var fxEntries = new List<(int Index, JsonObject Entry, string Description, int Duration)>();
        for (int i = 0; i < phraseEntries.Count; i++)
        {
            var entry = phraseEntries[i];
            var fxDesc = ReadString(entry, "fxDescription") ?? ReadString(entry, "FxDescription") ?? ReadString(entry, "fx_description");
            if (!string.IsNullOrWhiteSpace(fxDesc))
            {
                int fxDuration = 5; // default
                if (TryReadNumber(entry, "fxDuration", out var dur) || TryReadNumber(entry, "FxDuration", out dur) || TryReadNumber(entry, "fx_duration", out dur))
                    fxDuration = (int)dur;
                fxEntries.Add((i, entry, fxDesc, fxDuration));
            }
        }

        if (fxEntries.Count == 0)
        {
            var msg = "Nessun effetto sonoro da generare (nessuna propriet� 'fxDescription' presente)";
            _customLogger?.Append(runId, $"[{story.Id}] {msg}");
            return (true, msg);
        }

        _customLogger?.Append(runId, $"[{story.Id}] Trovati {fxEntries.Count} effetti sonori da generare");

        // Create AudioCraft tool
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var audioCraft = new AudioCraftTool(httpClient, forceCpu: false, logger: _customLogger);

        Directory.CreateDirectory(folderPath);

        int fxCounter = 0;
        foreach (var (index, entry, description, duration) in fxEntries)
        {
            fxCounter++;
            var durationSeconds = duration;
            if (durationSeconds < 1) durationSeconds = 1;
            if (durationSeconds > 30) durationSeconds = 30; // AudioCraft limit

            _customLogger?.Append(runId, $"[{story.Id}] Generazione FX {fxCounter}/{fxEntries.Count}: '{description}' ({durationSeconds}s)");

            // Update dispatcher progress if available
            if (_commandDispatcher != null)
            {
                _commandDispatcher.UpdateStep(runId, fxCounter, fxEntries.Count);
            }

            try
            {
                // Call AudioCraft to generate sound effect
                var audioRequest = new
                {
                    operation = "generate_sound",
                    prompt = description,
                    duration = durationSeconds,
                    model = "facebook/audiogen-medium"
                };
                var requestJson = JsonSerializer.Serialize(audioRequest);
                cancellationToken.ThrowIfCancellationRequested();
                var resultJson = await audioCraft.ExecuteAsync(requestJson);

                // Parse result to get filename
                var resultNode = JsonNode.Parse(resultJson) as JsonObject;
                if (resultNode?["error"] != null)
                {
                    var err = resultNode["error"]?.ToString() ?? "Errore AudioCraft";
                    _customLogger?.Append(runId, $"[{story.Id}] AudioCraft error: {err}");
                    return (false, err);
                }
                var resultField = resultNode?["result"]?.ToString();

                _customLogger?.Append(runId, $"[{story.Id}] AudioCraft FX response: {resultJson?.Substring(0, Math.Min(500, resultJson?.Length ?? 0))}");

                string? generatedFileName = null;
                
                // First check LastGeneratedSoundFile (set by AudioCraftTool)
                if (!string.IsNullOrWhiteSpace(audioCraft.LastGeneratedSoundFile))
                {
                    generatedFileName = audioCraft.LastGeneratedSoundFile;
                }
                
                // If not found, try to parse from result field
                if (string.IsNullOrWhiteSpace(generatedFileName) && !string.IsNullOrWhiteSpace(resultField))
                {
                    // Try to extract filename from result
                    try
                    {
                        var resultInner = JsonNode.Parse(resultField) as JsonObject;
                        generatedFileName = resultInner?["file"]?.ToString()
                            ?? resultInner?["filename"]?.ToString()
                            ?? resultInner?["file_path"]?.ToString()
                            ?? resultInner?["file_url"]?.ToString();
                        
                        // Extract just filename from path/url
                        if (!string.IsNullOrWhiteSpace(generatedFileName))
                        {
                            generatedFileName = Path.GetFileName(generatedFileName.Replace("/download/", ""));
                        }
                    }
                    catch
                    {
                        // resultField might not be valid JSON, check if it's a plain filename
                        if (resultField.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                            resultField.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        {
                            generatedFileName = resultField.Trim();
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(generatedFileName))
                {
                    var err = $"Impossibile determinare il file generato per FX {fxCounter}. Result: {resultJson}";
                    _customLogger?.Append(runId, $"[{story.Id}] {err}");
                    return (false, err);
                }
                generatedFileName = NormalizeAudioCraftFileName(generatedFileName);

                // Download file from AudioCraft server
                var localFileName = $"fx_{fxCounter:D3}.wav";
                var localFilePath = Path.Combine(folderPath, localFileName);

                var (downloadOk, downloadError) = await TryDownloadAudioCraftFileAsync(
                    httpClient,
                    generatedFileName,
                    localFilePath,
                    story.Id,
                    runId,
                    cancellationToken);
                if (!downloadOk)
                {
                    return (false, downloadError ?? $"Impossibile scaricare {generatedFileName}");
                }
                _customLogger?.Append(runId, $"[{story.Id}] Salvato: {localFileName}");
                
                // Save prompt to .txt file with same name
                var promptFileName = Path.ChangeExtension(localFileName, ".txt");
                var promptFilePath = Path.Combine(folderPath, promptFileName);
                await File.WriteAllTextAsync(promptFilePath, description);
                _customLogger?.Append(runId, $"[{story.Id}] Salvato prompt: {promptFileName}");

                // Update tts_schema.json: add fxFile property
                entry["fxFile"] = localFileName;
                entry["fx_file"] = localFileName;
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

        var successMsg = $"Generazione effetti sonori completata ({fxCounter} effetti)";
        _customLogger?.Append(runId, $"[{story.Id}] {successMsg}");
        return (true, successMsg);
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
            return await GenerateFxAudioInternalAsync(context, dispatcherRunId);
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

        // Step 2: Assign music files from curated library (series_folder or data/music_stories)
        var musicLibraryFolder = GetMusicLibraryFolderForStory(story);
        var libraryFiles = new List<string>();

        if (string.IsNullOrWhiteSpace(musicLibraryFolder) || !Directory.Exists(musicLibraryFolder))
        {
            if (story.SerieId.HasValue)
            {
                var serie = _database.GetSeriesById(story.SerieId.Value);
                if (serie != null)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Cartella musica serie non trovata: {musicLibraryFolder}. Avvio generazione con AudioCraft.");
                    var gen = await TryGenerateSeriesMusicLibraryAsync(story, serie, phraseEntries, runId);
                    if (gen.success)
                    {
                        libraryFiles = gen.files;
                        musicLibraryFolder = Path.GetDirectoryName(gen.files.First()) ?? musicLibraryFolder;
                    }
                    else
                    {
                        var err = gen.error ?? $"Cartella musica serie non trovata: {musicLibraryFolder}";
                        _customLogger?.Append(runId, $"[{story.Id}] {err}");
                        return (false, err);
                    }
                }
                else
                {
                    var err = $"Cartella musica serie non trovata: {musicLibraryFolder}";
                    _customLogger?.Append(runId, $"[{story.Id}] {err}");
                    return (false, err);
                }
            }
            else
            {
                var err = $"Cartella musica di fallback non trovata: {musicLibraryFolder}";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        if (libraryFiles.Count == 0)
        {
            libraryFiles = Directory
                .GetFiles(musicLibraryFolder)
                .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (libraryFiles.Count == 0)
        {
            if (story.SerieId.HasValue)
            {
                var serie = _database.GetSeriesById(story.SerieId.Value);
                if (serie != null)
                {
                    _customLogger?.Append(runId, $"[{story.Id}] Nessun file musica trovato in {musicLibraryFolder}. Avvio generazione con AudioCraft.");
                    var gen = await TryGenerateSeriesMusicLibraryAsync(story, serie, phraseEntries, runId);
                    if (gen.success)
                    {
                        libraryFiles = gen.files;
                        musicLibraryFolder = Path.GetDirectoryName(gen.files.First()) ?? musicLibraryFolder;
                    }
                    else
                    {
                        var err = gen.error ?? $"Nessun file musica trovato in {musicLibraryFolder}";
                        _customLogger?.Append(runId, $"[{story.Id}] {err}");
                        return (false, err);
                    }
                }
                else
                {
                    var err = $"Nessun file musica trovato in {musicLibraryFolder}";
                    _customLogger?.Append(runId, $"[{story.Id}] {err}");
                    return (false, err);
                }
            }
            else
            {
                var err = $"Nessun file musica trovato in {musicLibraryFolder}";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        var index = BuildMusicLibraryIndex(libraryFiles);

        var usedDestFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in phraseEntries)
        {
            var mf = ReadString(existing, "music_file") ?? ReadString(existing, "musicFile") ?? ReadString(existing, "MusicFile");
            if (!string.IsNullOrWhiteSpace(mf)) usedDestFileNames.Add(mf);
        }

        var assigned = 0;
        for (var i = 0; i < phraseEntries.Count; i++)
        {
            var entry = phraseEntries[i];
            var musicDesc = ReadString(entry, "music_description") ?? ReadString(entry, "musicDescription") ?? ReadString(entry, "MusicDescription");
            if (string.IsNullOrWhiteSpace(musicDesc))
                continue;

            var currentMusicFile = ReadString(entry, "music_file") ?? ReadString(entry, "musicFile") ?? ReadString(entry, "MusicFile");
            if (!string.IsNullOrWhiteSpace(currentMusicFile) && File.Exists(Path.Combine(folderPath, currentMusicFile)))
                continue; // already assigned and present

            var requestedType = InferMusicTypeFromDescription(musicDesc, index.Keys);
            var selected = SelectMusicFileDeterministic(index, requestedType, usedDestFileNames, seedA: story.Id, seedB: i);
            if (string.IsNullOrWhiteSpace(selected) || !File.Exists(selected))
            {
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Nessun file musica selezionabile per type='{requestedType}' desc='{musicDesc}'");
                continue;
            }

            var destFileName = Path.GetFileName(selected);
            var destPath = Path.Combine(folderPath, destFileName);
            try
            {
                if (!File.Exists(destPath))
                {
                    File.Copy(selected, destPath, overwrite: false);
                }
            }
            catch (Exception ex)
            {
                _customLogger?.Append(runId, $"[{story.Id}] [WARN] Copia musica fallita ({destFileName}): {ex.Message}");
                continue;
            }

            entry["music_file"] = destFileName;
            entry["musicFile"] = destFileName;
            usedDestFileNames.Add(destFileName);
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
            ? $"Musica assegnata da libreria ({assigned} voci in timeline)"
            : "Nessuna musica assegnata (nessuna richiesta o nessun file selezionabile)";
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

        // Step 3: Check for missing ambient sound files (from ambient sound description/file fields)
        var ambientSoundsNeeded = phraseEntries.Any(e =>
            !string.IsNullOrWhiteSpace(
                ReadString(e, "ambient_sound_description") ??
                ReadString(e, "AmbientSoundDescription") ??
                ReadString(e, "ambientSoundDescription") ??
                ReadString(e, "ambientSounds") ??
                ReadString(e, "AmbientSounds") ??
                ReadString(e, "ambient_sounds")));

        if (ambientSoundsNeeded)
        {
            var missingAmbientSounds = phraseEntries.Where(e =>
            {
                var ambientSounds =
                    ReadString(e, "ambient_sound_description") ??
                    ReadString(e, "AmbientSoundDescription") ??
                    ReadString(e, "ambientSoundDescription") ??
                    ReadString(e, "ambientSounds") ??
                    ReadString(e, "AmbientSounds") ??
                    ReadString(e, "ambient_sounds");
                if (string.IsNullOrWhiteSpace(ambientSounds)) return false;

                var ambientSoundFile =
                    ReadString(e, "ambient_sound_file") ??
                    ReadString(e, "AmbientSoundFile") ??
                    ReadString(e, "ambientSoundFile") ??
                    ReadString(e, "ambientSoundsFile") ??
                    ReadString(e, "AmbientSoundsFile") ??
                    ReadString(e, "ambient_sounds_file");
                if (string.IsNullOrWhiteSpace(ambientSoundFile)) return true;
                return !File.Exists(Path.Combine(folderPath, ambientSoundFile));
            }).ToList();

            if (missingAmbientSounds.Count > 0)
            {
                var err = $"File ambient sound mancanti per alcune frasi; genere necessario prima del mix. Missing count: {missingAmbientSounds.Count}";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        // Step 4: Check for missing FX audio files
        var fxNeeded = phraseEntries.Any(e => 
            !string.IsNullOrWhiteSpace(ReadString(e, "fxDescription") ?? ReadString(e, "FxDescription")));
        
        if (fxNeeded)
        {
            var missingFx = phraseEntries.Where(e =>
            {
                var fxDesc = ReadString(e, "fxDescription") ?? ReadString(e, "FxDescription");
                if (string.IsNullOrWhiteSpace(fxDesc)) return false;
                var fxFile = ReadString(e, "fxFile") ?? ReadString(e, "FxFile");
                if (string.IsNullOrWhiteSpace(fxFile)) return true;
                return !File.Exists(Path.Combine(folderPath, fxFile));
            }).ToList();

            if (missingFx.Count > 0)
            {
                var err = $"File FX mancanti per alcune frasi (count: {missingFx.Count}). Generazione automatica non consentita durante il mix; genera prima gli FX.";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        // Step 4.5: Check for missing music files and generate if needed (music can be regenerated)
        var musicNeeded = phraseEntries.Any(e => !string.IsNullOrWhiteSpace(ReadString(e, "music_description")));
        if (musicNeeded)
        {
            var missingMusic = phraseEntries.Where(e =>
            {
                var mdesc = ReadString(e, "music_description");
                if (string.IsNullOrWhiteSpace(mdesc)) return false;
                var musicFile = ReadString(e, "music_file");
                if (string.IsNullOrWhiteSpace(musicFile)) return true;
                return !File.Exists(Path.Combine(folderPath, musicFile));
            }).ToList();

            if (missingMusic.Count > 0)
            {
                var err = $"File music mancanti per alcune frasi (count: {missingMusic.Count}). Generazione automatica non consentita durante il mix; genera prima la musica.";
                _customLogger?.Append(runId, $"[{story.Id}] {err}");
                return (false, err);
            }
        }

        _customLogger?.Append(runId, $"[{story.Id}] Tutti i file audio presenti. Avvio mixaggio...");


        var ttsTrackFiles = new List<(string FilePath, int StartMs)>();
        var ambienceTrackFiles = new List<(string FilePath, int StartMs, int DurationMs)>();
        var fxTrackFiles = new List<(string FilePath, int StartMs)>();
        var musicTrackFiles = new List<(string FilePath, int StartMs, int DurationMs)>();

        // === OPENING INTRO (handled ONLY in mix, not in tts_schema.json) ===
        // 15s opening music + narrator announcement at +5s. Then shift the rest of the story by +15s.
        var introShiftMs = 0;
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
                var selectedOpening = SelectMusicFileDeterministic(
                    index,
                    requestedType: "opening",
                    usedDestFileNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    seedA: story.Id,
                    seedB: -100);

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

                    var storyTitle = (story.Title ?? string.Empty).Trim();
                    var titleAlreadyInTimeline = !string.IsNullOrWhiteSpace(storyTitle)
                        && TimelineContainsTitle(phraseEntries, storyTitle);
                    if (!string.IsNullOrWhiteSpace(storyTitle) && _ttsService != null && !string.IsNullOrWhiteSpace(narratorVoice) && !titleAlreadyInTimeline)
                    {
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

                        var announcement = !string.IsNullOrWhiteSpace(seriesTitle) && episodeNumber.HasValue
                            ? $"{seriesTitle}. Episodio {episodeNumber.Value}. {storyTitle}."
                            : !string.IsNullOrWhiteSpace(seriesTitle)
                                ? $"{seriesTitle}. {storyTitle}."
                                : $"{storyTitle}.";

                        var openingTtsFile = Path.Combine(folderPath, "tts_opening.wav");
                        if (!File.Exists(openingTtsFile))
                        {
                            var ttsSafe = ProsodyNormalizer.NormalizeForTTS(announcement);
                            var ttsResult = await _ttsService.SynthesizeAsync(narratorVoice, ttsSafe);
                            if (ttsResult != null && !string.IsNullOrWhiteSpace(ttsResult.AudioBase64))
                            {
                                var audioBytes = Convert.FromBase64String(ttsResult.AudioBase64);
                                await File.WriteAllBytesAsync(openingTtsFile, audioBytes);
                            }
                        }

                        if (File.Exists(openingTtsFile))
                        {
                            ttsTrackFiles.Add((openingTtsFile, 5000));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _customLogger?.Append(runId, $"[{story.Id}] [WARN] Opening intro fallito: {ex.Message}");
        }

        // Accoda il resto della storia come al solito (shift startMs di +introShiftMs)
        int currentTimeMs = introShiftMs;
        foreach (var entry in phraseEntries)
        {
            // TTS file
            var ttsFileName = ReadString(entry, "fileName") ?? ReadString(entry, "FileName") ?? ReadString(entry, "file_name");
            if (!string.IsNullOrWhiteSpace(ttsFileName))
            {
                var ttsFilePath = Path.Combine(folderPath, ttsFileName);
                if (File.Exists(ttsFilePath))
                {
                    int startMs = currentTimeMs;
                    if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                        startMs = (int)s + introShiftMs;
                    ttsTrackFiles.Add((ttsFilePath, startMs));

                    // Update current time based on duration
                    int durationMs = 2000; // default gap
                    if (TryReadNumber(entry, "durationMs", out var d) || TryReadNumber(entry, "DurationMs", out d) || TryReadNumber(entry, "duration_ms", out d))
                        durationMs = (int)d;
                    currentTimeMs = startMs + durationMs;
                }
            }

            // Ambient sounds are handled as continuous segments (from one [RUMORI] tag until the next)

            // FX file - starts at middle of phrase duration
            var fxFile = ReadString(entry, "fxFile") ?? ReadString(entry, "FxFile");
            if (!string.IsNullOrWhiteSpace(fxFile))
            {
                var fxFilePath = Path.Combine(folderPath, fxFile);
                if (File.Exists(fxFilePath))
                {
                    int startMs = introShiftMs;
                    int phraseDurationMs = 0;
                    if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                        startMs = (int)s + introShiftMs;
                    if (TryReadNumber(entry, "durationMs", out var d) || TryReadNumber(entry, "DurationMs", out d) || TryReadNumber(entry, "duration_ms", out d))
                        phraseDurationMs = (int)d;
                    int fxStartMs = startMs + (phraseDurationMs / 2);
                    fxTrackFiles.Add((fxFilePath, fxStartMs));
                }
            }

            // Music file - starts 2 seconds after phrase start
            var musicFile = ReadString(entry, "musicFile") ?? ReadString(entry, "music_file") ?? ReadString(entry, "MusicFile");
            if (!string.IsNullOrWhiteSpace(musicFile))
            {
                var musicFilePath = Path.Combine(folderPath, musicFile);
                if (File.Exists(musicFilePath))
                {
                    int startMs = introShiftMs;
                    if (TryReadNumber(entry, "startMs", out var s) || TryReadNumber(entry, "StartMs", out s) || TryReadNumber(entry, "start_ms", out s))
                        startMs = (int)s + introShiftMs;
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

                if (string.IsNullOrWhiteSpace(ambientFile)) continue;

                int startMs = 0;
                if (TryReadNumber(e, "startMs", out var s) || TryReadNumber(e, "StartMs", out s) || TryReadNumber(e, "start_ms", out s))
                    startMs = (int)s;
                var durationMs = ReadAmbientSoundsDurationMs(e);
                if (durationMs <= 0) continue;

                var fullPath = Path.Combine(folderPath, ambientFile);
                if (!File.Exists(fullPath))
                {
                    _customLogger?.Append(runId, $"[{story.Id}] [WARN] File ambient sound NON TROVATO: {fullPath}");
                    continue;
                }

                ambienceTrackFiles.Add((fullPath, startMs + introShiftMs, durationMs));
                _customLogger?.Append(runId, $"[{story.Id}] Ambient segment: {ambientFile} @ {startMs + introShiftMs}ms for {durationMs}ms");
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
                var finalResult = await MixFinalTracksAsync(trackFiles, outputFile, runId, storyId);
                
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
        long storyId)
    {
        // Mix the 3 tracks: TTS (full volume), Ambience+FX (already has volume), Music (already has volume)
        var inputArgs = new StringBuilder();
        var filterArgs = new StringBuilder();
        
        for (int i = 0; i < trackFiles.Count; i++)
        {
            inputArgs.Append($" -i \"{trackFiles[i]}\"");
            filterArgs.Append($"[{i}]");
        }
        
        // Mix with equal weights, then normalize
        filterArgs.Append($"amix=inputs={trackFiles.Count}:duration=longest:dropout_transition=2:normalize=0[mixed];[mixed]dynaudnorm=p=0.95:s=3[out]");
        
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

    private static Dictionary<string, CharacterVoiceInfo> BuildCharacterMap(JsonArray charactersArray)
    {
        var map = new Dictionary<string, CharacterVoiceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in charactersArray.OfType<JsonObject>())
        {
            var name = ReadString(node, "Name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var voiceId = ReadString(node, "VoiceId");
            var gender = ReadString(node, "Gender");

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
        var charName = ReadString(entry, "Character");
        if (string.IsNullOrWhiteSpace(charName))
            return false;

        character = charName.Trim();
        text = ReadString(entry, "Text") ?? string.Empty;
        emotion = ReadString(entry, "Emotion") ?? "neutral";
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

        var synthesis = await _ttsService.SynthesizeAsync(voiceId, ttsSafeText, "it", normalizedEmotion);
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

    private sealed record CharacterVoiceInfo(string Name, string? VoiceId, string? Gender);

    // TODO: Implement auto-advancement feature with idle detection
    public bool IsAutoAdvancementEnabled()
    {
        try
        {
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






