using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed class AutoStateDrivenSeriesEpisodeService : BackgroundService
{
    private const int MaxAutoEpisodeNumber = 6;
    private readonly DatabaseService _database;
    private readonly StoriesService _storiesService;
    private readonly CommandDispatcher _dispatcher;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICustomLogger _customLogger;
    private readonly ILogger<AutoStateDrivenSeriesEpisodeService> _logger;
    private readonly IOptionsMonitor<AutomaticOperationsOptions> _optionsMonitor;
    private readonly CommandTuningOptions _tuning;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TextValidationService _textValidationService;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(30);

    private DateTime _lastAttemptUtc = DateTime.UtcNow;

    public AutoStateDrivenSeriesEpisodeService(
        DatabaseService database,
        StoriesService storiesService,
        CommandDispatcher dispatcher,
        ILangChainKernelFactory kernelFactory,
        ICustomLogger customLogger,
        ILogger<AutoStateDrivenSeriesEpisodeService> logger,
        IOptionsMonitor<AutomaticOperationsOptions> optionsMonitor,
        IOptions<CommandTuningOptions> tuningOptions,
        IServiceScopeFactory scopeFactory,
        TextValidationService textValidationService)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _storiesService = storiesService ?? throw new ArgumentNullException(nameof(storiesService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _customLogger = customLogger ?? throw new ArgumentNullException(nameof(customLogger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _tuning = tuningOptions.Value ?? new CommandTuningOptions();
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _textValidationService = textValidationService ?? throw new ArgumentNullException(nameof(textValidationService));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, stoppingToken);

                var opts = _optionsMonitor.CurrentValue ?? new AutomaticOperationsOptions();
                if (!opts.Enabled)
                {
                    continue;
                }

                var auto = opts.AutoStateDrivenSeriesEpisode;
                if (auto == null || !auto.Enabled)
                {
                    continue;
                }

                var interval = TimeSpan.FromMinutes(Math.Max(1, auto.IntervalMinutes));
                var nowUtc = DateTime.UtcNow;
                if (nowUtc - _lastAttemptUtc < interval)
                {
                    continue;
                }

                if (IsOperationQueued("StateDrivenEpisodeAuto"))
                {
                    _lastAttemptUtc = nowUtc;
                    continue;
                }

                if (!TryEnqueueNextEpisode(auto))
                {
                    _lastAttemptUtc = nowUtc;
                    continue;
                }

                _lastAttemptUtc = nowUtc;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto state-driven episode loop failed");
            }
        }
    }

    private bool TryEnqueueNextEpisode(AutoStateDrivenSeriesEpisodeOptions auto)
    {
        var serieId = _database.GetSeriesIdWithMostEpisodesWritten(includeDeleted: false);
        if (!serieId.HasValue) return false;

        var serie = _database.GetSeriesById(serieId.Value);
        if (serie == null) return false;

        var writer = ResolveWriterAgent(auto.WriterAgentId);
        if (writer == null) return false;

        var nextEpisodeNumber = _database.GetNextSeriesEpisodeNumber(serie.Id);
        if (nextEpisodeNumber > MaxAutoEpisodeNumber)
        {
            _logger.LogInformation(
                "Auto episode generation stopped: next episode {EpisodeNumber} exceeds max {MaxEpisode}.",
                nextEpisodeNumber,
                MaxAutoEpisodeNumber);
            return false;
        }

        var episode = _database.EnsureSeriesEpisode(serie.Id, nextEpisodeNumber);
        if (episode == null) return false;

        var narrativeProfileId = serie.DefaultNarrativeProfileId.GetValueOrDefault(1);
        if (narrativeProfileId <= 0) narrativeProfileId = 1;

        var plannerMode = string.IsNullOrWhiteSpace(serie.DefaultPlannerMode) ? null : serie.DefaultPlannerMode!.Trim();

        var theme = SeriesEpisodePromptBuilder.BuildStateDrivenEpisodeTheme(serie, episode);
        var title = SeriesEpisodePromptBuilder.BuildStateDrivenEpisodeTitle(serie, episode);

        var genId = Guid.NewGuid();
        _customLogger.Start(genId.ToString());
        _customLogger.Append(genId.ToString(), $"Avvio episodio automatico: serie={serie.Id}, ep={episode.Number}");

        var startCmd = new StartStateDrivenStoryCommand(_database);
        var start = startCmd.ExecuteAsync(
            theme: theme,
            title: title,
            narrativeProfileId: narrativeProfileId,
            serieId: serie.Id,
            serieEpisode: episode.Number,
            plannerMode: plannerMode,
            ct: CancellationToken.None).GetAwaiter().GetResult();

        if (!start.success || start.storyId <= 0)
        {
            _customLogger.Append(genId.ToString(), $"Errore avvio storia: {start.error}", "error");
            _customLogger.MarkCompleted(genId.ToString(), "failed");
            return false;
        }

        var minutes = auto.TargetMinutes <= 0 ? 20 : auto.TargetMinutes;
        var wpm = auto.WordsPerMinute <= 0 ? 150 : auto.WordsPerMinute;
        var priority = Math.Max(1, auto.Priority);

        _dispatcher.Enqueue(
            "StateDrivenEpisodeAuto",
            async ctx =>
            {
                try
                {
                    var cmd = new GenerateStateDrivenEpisodeToDurationCommand(
                        storyId: start.storyId,
                        writerAgentId: writer.Id,
                        targetMinutes: minutes,
                        wordsPerMinute: wpm,
                        database: _database,
                        kernelFactory: _kernelFactory,
                        storiesService: _storiesService,
                        logger: _customLogger,
                        textValidationService: _textValidationService,
                        tuning: _tuning,
                        scopeFactory: _scopeFactory);

                    return await cmd.ExecuteAsync(runIdForProgress: genId.ToString(), ct: ctx.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _customLogger.Append(genId.ToString(), "Operazione annullata.", "warning");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), "cancelled");
                    _ = _customLogger.BroadcastTaskComplete(genId, "cancelled");
                    return new CommandResult(false, "cancelled");
                }
                catch (Exception ex)
                {
                    _customLogger.Append(genId.ToString(), "Errore: " + ex.Message, "error");
                    await _customLogger.MarkCompletedAsync(genId.ToString(), ex.Message);
                    _ = _customLogger.BroadcastTaskComplete(genId, "failed");
                    return new CommandResult(false, ex.Message);
                }
            },
            runId: genId.ToString(),
            metadata: new Dictionary<string, string>
            {
                ["operation"] = "state_driven_series_episode_auto",
                ["storyId"] = start.storyId.ToString(),
                ["serieId"] = serie.Id.ToString(),
                ["episodeId"] = episode.Id.ToString(),
                ["episodeNumber"] = episode.Number.ToString(),
                ["writerAgentId"] = writer.Id.ToString(),
                ["writerName"] = writer.Name,
                ["targetMinutes"] = minutes.ToString(),
                ["wordsPerMinute"] = wpm.ToString()
            },
            priority: priority);

        return true;
    }

    private Agent? ResolveWriterAgent(int writerAgentId)
    {
        if (writerAgentId > 0)
        {
            var agent = _database.GetAgentById(writerAgentId);
            if (agent != null && agent.IsActive) return agent;
        }

        var writers = _database.ListAgents()
            .Where(a => a.IsActive &&
                        a.Role.Contains("writer", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (writers.Count == 0) return null;
        if (writers.Count == 1) return writers[0];

        var weighted = new List<(Agent Agent, double Weight)>();
        double totalWeight = 0;
        foreach (var writer in writers)
        {
            var score = GetWriterScore(writer);
            var weight = Math.Max(1.0, score);
            weighted.Add((writer, weight));
            totalWeight += weight;
        }

        if (totalWeight <= 0)
        {
            return writers[Random.Shared.Next(writers.Count)];
        }

        var roll = Random.Shared.NextDouble() * totalWeight;
        foreach (var entry in weighted)
        {
            roll -= entry.Weight;
            if (roll <= 0)
            {
                return entry.Agent;
            }
        }

        return weighted[weighted.Count - 1].Agent;
    }

    private double GetWriterScore(Agent writer)
    {
        if (writer.ModelId.HasValue)
        {
            var model = _database.GetModelInfoById(writer.ModelId.Value);
            if (model != null)
            {
                if (model.WriterScore > 0) return model.WriterScore;
                if (model.TotalScore > 0) return model.TotalScore;
            }
        }

        return 1.0;
    }

    private bool IsOperationQueued(string operationName)
    {
        try
        {
            return _dispatcher.GetActiveCommands().Any(s =>
                string.Equals(s.OperationName, operationName, StringComparison.OrdinalIgnoreCase) ||
                (s.Metadata != null &&
                 s.Metadata.TryGetValue("operation", out var op) &&
                 string.Equals(op, operationName, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }
}
