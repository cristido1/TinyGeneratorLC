using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    private sealed class DeleteTtsCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public DeleteTtsCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                _service.CleanBeforeTtsAudioGeneration(context.Story.Id, context.FolderPath);

                var cascadeContext = new StoryCommandContext(context.Story, context.FolderPath, null, context.CancellationToken);
                var cascadeCommands = new IStoryCommand[]
                {
                    new DeleteAmbienceCommand(_service),
                    new DeleteMusicCommand(_service),
                    new DeleteFxCommand(_service),
                    new DeleteFinalMixCommand(_service)
                };

                var messages = new List<string> { "Tracce TTS cancellate" };
                var allOk = true;

                foreach (var cmd in cascadeCommands)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var (ok, msg) = await cmd.ExecuteAsync(cascadeContext);
                    allOk &= ok;
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        messages.Add(msg);
                    }
                }

                return (allOk, string.Join(" | ", messages));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la cancellazione TTS per la storia {Id}", context.Story.Id);
                return (false, ex.Message);
            }
        }
    }
}
