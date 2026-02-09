using System;
using System.Threading.Tasks;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    private sealed class DeleteTtsSchemaCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public DeleteTtsSchemaCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            return Task.FromResult<(bool success, string? message)>(
                _service.DeleteTtsSchemaAssets(context.Story.Id, context.FolderPath));
        }
    }
}
