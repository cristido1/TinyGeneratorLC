using System;
using System.Threading.Tasks;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    internal sealed class DeleteFinalMixCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public DeleteFinalMixCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            return Task.FromResult(_service.DeleteFinalMixAssets(context.Story.Id, context.FolderPath));
        }
    }
}
