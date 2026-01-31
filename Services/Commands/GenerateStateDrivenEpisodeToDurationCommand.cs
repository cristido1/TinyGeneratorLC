using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class GenerateStateDrivenEpisodeToDurationCommand
{
    private readonly CommandTuningOptions _tuning;

    private readonly long _storyId;
    private readonly int _writerAgentId;
    private readonly int _targetMinutes;
    private readonly int _wordsPerMinute;
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly StoriesService _storiesService;
    private readonly ICustomLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;

    public GenerateStateDrivenEpisodeToDurationCommand(
        long storyId,
        int writerAgentId,
        int targetMinutes,
        int wordsPerMinute,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        StoriesService storiesService,
        ICustomLogger? logger = null,
        CommandTuningOptions? tuning = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _storyId = storyId;
        _writerAgentId = writerAgentId;
        _targetMinutes = Math.Max(1, targetMinutes);
        _wordsPerMinute = Math.Max(80, wordsPerMinute);
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _storiesService = storiesService ?? throw new ArgumentNullException(nameof(storiesService));
        _logger = logger;
        _tuning = tuning ?? new CommandTuningOptions();
        _scopeFactory = scopeFactory;
    }

    public async Task<CommandResult> ExecuteAsync(string? runIdForProgress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var snap = _database.GetStateDrivenStorySnapshot(_storyId);
        if (snap == null) return new CommandResult(false, $"Story {_storyId}: snapshot not found");
        if (!snap.IsActive) return new CommandResult(false, $"Story {_storyId}: runtime not active");

        var targetWords = _targetMinutes * _wordsPerMinute;
        var maxChunks = Math.Max(6, Math.Min(40, (int)Math.Ceiling(targetWords / 250.0) + 6));

        var assembled = new StringBuilder(targetWords * 6);

        // If the story already has some chunks, include them in the assembled text and continue from there.
        // This allows resuming.
        try
        {
            var existing = _database.ListChaptersForStory(_storyId);
            foreach (var ch in existing)
            {
                if (!string.IsNullOrWhiteSpace(ch.Content))
                {
                    assembled.AppendLine(ch.Content.Trim());
                    assembled.AppendLine();
                }
            }
        }
        catch
        {
            // best-effort
        }

        var currentWords = CountWords(assembled.ToString());

        _logger?.Append(runIdForProgress ?? string.Empty,
            $"[story {_storyId}] Target ~{_targetMinutes} min TTS => ~{targetWords} parole (wpm={_wordsPerMinute}). StartWords={currentWords}.");

        for (var i = 0; i < maxChunks; i++)
        {
            ct.ThrowIfCancellationRequested();

            snap = _database.GetStateDrivenStorySnapshot(_storyId);
            if (snap == null) return new CommandResult(false, $"Story {_storyId}: snapshot not found during loop");
            if (!snap.IsActive) return new CommandResult(false, $"Story {_storyId}: runtime became inactive");

            currentWords = CountWords(assembled.ToString());
            var remaining = Math.Max(0, targetWords - currentWords);

            // Decide if this is the final chunk.
            // If we're close enough, we do a concluding chunk and stop.
            var isFinal = remaining <= 450;

            var targetChunkWords = isFinal
                ? Math.Max(250, remaining) // try to land near the target
                : Math.Clamp(remaining, 280, 450);

            var options = new GenerateNextChunkCommand.GenerateChunkOptions(
                RequireCliffhanger: !isFinal,
                IsFinalChunk: isFinal,
                TargetWords: targetChunkWords);

            if (!string.IsNullOrWhiteSpace(runIdForProgress))
            {
                try
                {
                    var done = Math.Min(maxChunks, i + 1);
                    _logger?.Append(runIdForProgress,
                        $"[story {_storyId}] Chunk {snap.CurrentChunkIndex + 1}: remainingWords={remaining}, final={isFinal}, targetChunkWords={targetChunkWords}");
                }
                catch { }
            }

            var chunkCmd = new GenerateNextChunkCommand(
                storyId: _storyId,
                writerAgentId: _writerAgentId,
                database: _database,
                kernelFactory: _kernelFactory,
                logger: _logger,
                options: options,
                tuning: _tuning,
                scopeFactory: _scopeFactory);

            var result = await chunkCmd.ExecuteAsync(ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return new CommandResult(false, $"Auto-episode stopped: {result.Message}");
            }

            var lastText = _database.GetLatestChapterTextForStory(_storyId) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(lastText))
            {
                assembled.AppendLine(lastText.Trim());
                assembled.AppendLine();
            }

            currentWords = CountWords(assembled.ToString());

            if (isFinal || currentWords >= targetWords)
            {
                break;
            }
        }

        var finalText = assembled.ToString().Trim();
        if (string.IsNullOrWhiteSpace(finalText))
        {
            return new CommandResult(false, "Auto-episode produced empty text");
        }

        if (!_database.CompleteStateDrivenStory(_storyId, finalText, out var completeError))
        {
            return new CommandResult(false, $"Failed to finalize story: {completeError}");
        }

        // Enqueue post-episode state-driven pipeline (canon/delta/continuity/state/recap).
        _storiesService.EnqueueStateDrivenPostEpisodePipeline(_storyId, trigger: "state_driven_episode_completed", priority: 3);

        // Enqueue revise => it will enqueue evaluations automatically.
        var reviseRunId = _storiesService.EnqueueReviseStoryCommand(_storyId, trigger: "state_driven_episode_completed", priority: 2, force: true);

        var msg = string.IsNullOrWhiteSpace(reviseRunId)
            ? $"Episode completed (storyId={_storyId}). Revision not queued (dispatcher unavailable?)"
            : $"Episode completed (storyId={_storyId}). Revision queued: {reviseRunId} (evaluations will follow).";

        return new CommandResult(true, msg);
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (!inWord)
                {
                    count++;
                    inWord = true;
                }
            }
            else
            {
                inWord = false;
            }
        }

        return count;
    }
}
