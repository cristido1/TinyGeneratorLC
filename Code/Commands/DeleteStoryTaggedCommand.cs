using System;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    private sealed class DeleteStoryTaggedCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public DeleteStoryTaggedCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var storyId = context.Story.Id;
                var cleared = _service._database.ClearStoryTagged(storyId);
                if (!cleared)
                {
                    return (false, "Impossibile cancellare story_tagged dal database");
                }

                context.Story.StoryTagged = string.Empty;
                context.Story.StoryTaggedVersion = null;
                context.Story.FormatterModelId = null;
                context.Story.FormatterPromptHash = null;

                var cascadeContext = new StoryCommandContext(context.Story, context.FolderPath, null, context.CancellationToken);
                var (ttsOk, ttsMessage) = await new DeleteTtsCommand(_service).ExecuteAsync(cascadeContext);

                var message = string.IsNullOrWhiteSpace(ttsMessage)
                    ? "Campo story_tagged cancellato"
                    : $"Campo story_tagged cancellato. {ttsMessage}";

                return (ttsOk, message);
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la cancellazione del campo story_tagged per la storia {Id}", context.Story.Id);
                return (false, ex.Message);
            }
        }
    }
}
