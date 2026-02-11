using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoryMainCommands
{
    private sealed class AddAmbientTagsToStoryStateCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public AddAmbientTagsToStoryStateCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => false;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (_service.KernelFactory == null)
            {
                return (false, "Kernel factory non disponibile per add_ambient_tags_to_story");
            }

            var command = new AddAmbientTagsToStoryCommand(
                context.Story.Id,
                _service.Database,
                _service.KernelFactory,
                _service,
                _service.CustomLogger,
                _service.Tuning);

            var result = await command.ExecuteAsync(context.CancellationToken, _service.CurrentDispatcherRunId);
            return (result.Success, result.Message);
        }
    }

    private sealed class AddFxTagsToStoryStateCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public AddFxTagsToStoryStateCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => false;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (_service.KernelFactory == null)
            {
                return (false, "Kernel factory non disponibile per add_fx_tags_to_story");
            }

            var command = new AddFxTagsToStoryCommand(
                context.Story.Id,
                _service.Database,
                _service.KernelFactory,
                _service,
                _service.CustomLogger,
                _service.Tuning);

            var result = await command.ExecuteAsync(context.CancellationToken, _service.CurrentDispatcherRunId);
            return (result.Success, result.Message);
        }
    }

    private sealed class AddMusicTagsToStoryStateCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public AddMusicTagsToStoryStateCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => false;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (_service.KernelFactory == null)
            {
                return (false, "Kernel factory non disponibile per add_music_tags_to_story");
            }

            var command = new AddMusicTagsToStoryCommand(
                context.Story.Id,
                _service.Database,
                _service.KernelFactory,
                _service,
                _service.CustomLogger,
                _service.Tuning);

            var result = await command.ExecuteAsync(context.CancellationToken, _service.CurrentDispatcherRunId);
            return (result.Success, result.Message);
        }
    }

    private sealed class EvaluateStoryCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public EvaluateStoryCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => false;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var story = context.Story;
            var evaluators = _service.Database.ListAgents()
                .Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Role) &&
                    (a.Role.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) ||
                     a.Role.Equals("evaluator", StringComparison.OrdinalIgnoreCase) ||
                     a.Role.Equals("writer_evaluator", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(a => a.Id)
                .ToList();

            if (evaluators.Count == 0)
            {
                return (false, "Nessun agente valutatore configurato");
            }

            return await RunParallelAsync(story, evaluators);
        }

        private async Task<(bool success, string? message)> RunParallelAsync(StoryRecord story, List<Agent> evaluators)
        {
            if (_service.CommandDispatcher != null)
            {
                var enqueued = new List<string>();
                foreach (var evaluator in evaluators)
                {
                    try
                    {
                        var runId = $"evaluate_story_{story.Id}_agent_{evaluator.Id}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
                        var meta = new Dictionary<string, string>
                        {
                            ["storyId"] = story.Id.ToString(),
                            ["agentId"] = evaluator.Id.ToString(),
                            ["agentName"] = evaluator.Name ?? string.Empty,
                            ["operation"] = "evaluate_story"
                        };

                        _service.CommandDispatcher.Enqueue(
                            "evaluate_story",
                            async ctx =>
                            {
                                try
                                {
                                    var (success, score, error) = await _service.EvaluateStoryWithAgentAsync(story.Id, evaluator.Id);
                                    var msg = success ? $"Valutazione completata. Score: {score:F2}" : $"Valutazione fallita: {error}";
                                    return new CommandResult(success, msg);
                                }
                                catch (Exception ex)
                                {
                                    return new CommandResult(false, ex.Message);
                                }
                            },
                            runId: runId,
                            threadScope: $"story/evaluate/agent_{evaluator.Id}",
                            metadata: meta);

                        enqueued.Add($"{evaluator.Name ?? ("Evaluator " + evaluator.Id)} ({runId})");
                    }
                    catch (Exception ex)
                    {
                        _service.Logger?.LogWarning(ex, "Failed to enqueue evaluation for story {StoryId} agent {AgentId}", story.Id, evaluator.Id);
                    }
                }

                var msg = enqueued.Count > 0 ? $"Valutazioni accodate: {string.Join("; ", enqueued)}" : "Nessuna valutazione accodata";
                return (enqueued.Count > 0, msg);
            }

            var tasks = evaluators.Select(async evaluator =>
            {
                try
                {
                    var (success, score, error) = await _service.EvaluateStoryWithAgentAsync(story.Id, evaluator.Id);
                    var label = string.IsNullOrWhiteSpace(evaluator.Name) ? $"Evaluator {evaluator.Id}" : evaluator.Name;
                    return success
                        ? (success: true, message: $"{label}: punteggio {score:F2}")
                        : (success: false, message: $"{label}: errore {error ?? "sconosciuto"}");
                }
                catch (Exception ex)
                {
                    var label = string.IsNullOrWhiteSpace(evaluator.Name) ? $"Evaluator {evaluator.Id}" : evaluator.Name;
                    return (success: false, message: $"{label}: eccezione {ex.Message}");
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            var allOk = results.All(r => r.success);
            var joined = string.Join("; ", results.Select(r => r.message));

            return allOk
                ? (true, $"Valutazione completata. {joined}")
                : (false, $"Valutazione parziale. {joined}");
        }
    }

    private sealed class ReviseStoryCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public ReviseStoryCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => false;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var runId = _service.EnqueueReviseStoryCommand(context.Story.Id, trigger: "status_flow", priority: 2, force: true);
            return Task.FromResult<(bool success, string? message)>(string.IsNullOrWhiteSpace(runId)
                ? (false, "Revisione non accodata")
                : (true, $"Revisione accodata (run {runId})"));
        }
    }

    private sealed class TagStoryCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public TagStoryCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => false;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (_service.KernelFactory == null)
            {
                return (false, "Kernel factory non disponibile");
            }

            var cmd = new AddVoiceTagsToStoryCommand(
                context.Story.Id,
                _service.Database,
                _service.KernelFactory,
                _service,
                _service.CustomLogger,
                _service.Tuning);

            var result = await cmd.ExecuteAsync(context.CancellationToken, _service.CurrentDispatcherRunId);
            return (result.Success, result.Message);
        }
    }

    private sealed class PrepareTtsSchemaCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public PrepareTtsSchemaCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            var overallSuccess = true;

            try
            {
                var (ttsOk, ttsMsg) = await _service.GenerateTtsSchemaJsonAsync(context.Story.Id);
                sb.AppendLine($"GenerateTtsSchema: {ttsMsg}");
                if (!ttsOk) overallSuccess = false;
            }
            catch (Exception ex)
            {
                sb.AppendLine("GenerateTtsSchema: exception " + ex.Message);
                overallSuccess = false;
            }

            try
            {
                var (normCharOk, normCharMsg) = await _service.NormalizeCharacterNamesAsync(context.Story.Id);
                sb.AppendLine($"NormalizeCharacterNames: {normCharMsg}");
                if (!normCharOk) overallSuccess = false;
            }
            catch (Exception ex)
            {
                sb.AppendLine("NormalizeCharacterNames: exception " + ex.Message);
                overallSuccess = false;
            }

            try
            {
                var (assignOk, assignMsg) = await _service.AssignVoicesAsync(context.Story.Id);
                sb.AppendLine($"AssignVoices: {assignMsg}");
                if (!assignOk) overallSuccess = false;
            }
            catch (Exception ex)
            {
                sb.AppendLine("AssignVoices: exception " + ex.Message);
                overallSuccess = false;
            }

            try
            {
                var (normSentOk, normSentMsg) = await _service.NormalizeSentimentsAsync(context.Story.Id);
                sb.AppendLine($"NormalizeSentiments: {normSentMsg}");
                if (!normSentOk) overallSuccess = false;
            }
            catch (Exception ex)
            {
                sb.AppendLine("NormalizeSentiments: exception " + ex.Message);
                overallSuccess = false;
            }

            if (overallSuccess && _service.IsTtsSchemaAutoLaunchEnabled())
            {
                try
                {
                    var refreshed = _service.GetStoryById(context.Story.Id) ?? context.Story;
                    _service.EnqueueNextStatusCommand(refreshed, "tts_schema_completed", priority: 3);
                }
                catch
                {
                    // best-effort autolaunch
                }
            }

            return (overallSuccess, sb.ToString());
        }
    }

    private sealed class GenerateTtsAudioCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public GenerateTtsAudioCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => true;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => true;

        public Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var enqueue = _service.TryEnqueueGenerateTtsAudioCommandInternal(
                context.Story.Id,
                trigger: "status_transition",
                priority: 3,
                targetStatusId: context.TargetStatus?.Id);

            var success = enqueue.Enqueued || !string.IsNullOrWhiteSpace(enqueue.RunId);
            return Task.FromResult<(bool success, string? message)>((success, enqueue.Enqueued
                ? $"Generazione audio TTS accodata (run {enqueue.RunId})."
                : enqueue.Message));
        }
    }

    private sealed class GenerateAmbienceAudioCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public GenerateAmbienceAudioCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => true;

        public async Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var deleteCmd = new StoriesService.DeleteAmbienceCommand(_service);
            var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
            if (!cleanupOk)
            {
                return (false, cleanupMessage ?? "Impossibile cancellare i rumori ambientali esistenti");
            }

            var runId = _service.CurrentDispatcherRunId;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                var (success, message) = await _service.GenerateAmbienceAudioInternalAsync(context, runId);
                if (success)
                {
                    var story = _service.GetStoryById(context.Story.Id) ?? context.Story;
                    _service.ApplyStatusTransitionWithCleanup(story, "ambient_generated", runId);
                }
                return (success, message);
            }

            return await _service.StartAmbienceAudioGenerationAsync(context);
        }
    }

    private sealed class GenerateFxAudioCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public GenerateFxAudioCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => true;

        public async Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var deleteCmd = new StoriesService.DeleteFxCommand(_service);
            var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
            if (!cleanupOk)
            {
                return (false, cleanupMessage ?? "Impossibile cancellare gli effetti sonori esistenti");
            }

            var runId = _service.CurrentDispatcherRunId;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                var (success, message) = await _service.GenerateFxAudioInternalAsync(context, runId);
                if (success)
                {
                    var story = _service.GetStoryById(context.Story.Id) ?? context.Story;
                    _service.ApplyStatusTransitionWithCleanup(story, "fx_generated", runId);
                }
                return (success, message);
            }

            return await _service.StartFxAudioGenerationAsync(context);
        }
    }

    private sealed class GenerateMusicCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public GenerateMusicCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => true;

        public async Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var deleteCmd = new StoriesService.DeleteMusicCommand(_service);
            var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
            if (!cleanupOk)
            {
                return (false, cleanupMessage ?? "Impossibile cancellare la musica esistente");
            }

            var runId = _service.CurrentDispatcherRunId;
            if (!string.IsNullOrWhiteSpace(runId))
            {
                var (success, message) = await _service.GenerateMusicInternalAsync(context, runId);
                if (success)
                {
                    var story = _service.GetStoryById(context.Story.Id) ?? context.Story;
                    _service.ApplyStatusTransitionWithCleanup(story, "music_generated", runId);
                }
                return (success, message);
            }

            return await _service.StartMusicGenerationAsync(context);
        }
    }

    private sealed class MixFinalAudioCommand : StoriesService.IStoryCommand
    {
        private readonly StoriesService _service;

        public MixFinalAudioCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => true;

        public async Task<(bool success, string? message)> ExecuteAsync(StoriesService.StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var deleteCmd = new StoriesService.DeleteFinalMixCommand(_service);
            var (cleanupOk, cleanupMessage) = await deleteCmd.ExecuteAsync(context);
            if (!cleanupOk)
            {
                return (false, cleanupMessage ?? "Impossibile cancellare il mix finale esistente");
            }

            return await _service.StartMixFinalAudioAsync(context);
        }
    }
}
