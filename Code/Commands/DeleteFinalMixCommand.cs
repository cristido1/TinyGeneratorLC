using System;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    internal sealed class DeleteFinalMixCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public DeleteFinalMixCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_service.DeleteFinalMixAssets(context.Story.Id, context.FolderPath));
        }
    }
}
