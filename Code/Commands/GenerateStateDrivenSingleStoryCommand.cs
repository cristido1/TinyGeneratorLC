using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class GenerateStateDrivenSingleStoryCommand
{
    private readonly string _theme;
    private readonly string _title;
    private readonly int _narrativeProfileId;
    private readonly string? _plannerMode;
    private readonly int _writerAgentId;
    private readonly int _targetMinutes;
    private readonly int _wordsPerMinute;
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly StoriesService _storiesService;
    private readonly TextValidationService _textValidationService;
    private readonly ICustomLogger? _logger;
    private readonly CommandTuningOptions _tuning;
    private readonly IServiceScopeFactory? _scopeFactory;

    public GenerateStateDrivenSingleStoryCommand(
        string theme,
        string title,
        int narrativeProfileId,
        string? plannerMode,
        int writerAgentId,
        int targetMinutes,
        int wordsPerMinute,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        StoriesService storiesService,
        TextValidationService textValidationService,
        ICustomLogger? logger = null,
        CommandTuningOptions? tuning = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _theme = theme ?? string.Empty;
        _title = title ?? string.Empty;
        _narrativeProfileId = narrativeProfileId;
        _plannerMode = plannerMode;
        _writerAgentId = writerAgentId;
        _targetMinutes = targetMinutes;
        _wordsPerMinute = wordsPerMinute;
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _storiesService = storiesService ?? throw new ArgumentNullException(nameof(storiesService));
        _textValidationService = textValidationService ?? throw new ArgumentNullException(nameof(textValidationService));
        _logger = logger;
        _tuning = tuning ?? new CommandTuningOptions();
        _scopeFactory = scopeFactory;
    }

    public long StoryId { get; private set; }

    public async Task<CommandResult> ExecuteAsync(string? runIdForProgress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(_theme))
        {
            return new CommandResult(false, "Theme is required");
        }

        if (string.IsNullOrWhiteSpace(_title))
        {
            return new CommandResult(false, "Title is required");
        }

        if (_narrativeProfileId <= 0)
        {
            return new CommandResult(false, "NarrativeProfileId is required");
        }

        if (_writerAgentId <= 0)
        {
            return new CommandResult(false, "WriterAgentId is required");
        }

        var startCmd = new StartStateDrivenStoryCommand(_database);
        var (success, storyId, error) = await startCmd.ExecuteAsync(
            theme: _theme,
            title: _title,
            narrativeProfileId: _narrativeProfileId,
            serieId: null,
            serieEpisode: null,
            plannerMode: _plannerMode,
            ct: ct).ConfigureAwait(false);

        if (!success || storyId <= 0)
        {
            return new CommandResult(false, error ?? "Failed to start state-driven story");
        }

        StoryId = storyId;
        _logger?.Append(runIdForProgress ?? string.Empty, $"[story {storyId}] Story state-driven creata.");

        var generateCmd = new GenerateStateDrivenEpisodeToDurationCommand(
            storyId: storyId,
            writerAgentId: _writerAgentId,
            targetMinutes: _targetMinutes,
            wordsPerMinute: _wordsPerMinute,
            database: _database,
            kernelFactory: _kernelFactory,
            storiesService: _storiesService,
            logger: _logger,
            textValidationService: _textValidationService,
            tuning: _tuning,
            scopeFactory: _scopeFactory);

        var generation = await generateCmd.ExecuteAsync(runIdForProgress: runIdForProgress, ct: ct).ConfigureAwait(false);
        if (!generation.Success)
        {
            return generation;
        }

        return new CommandResult(true, $"{generation.Message} storyId={storyId}");
    }
}
