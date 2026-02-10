using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    internal sealed class FunctionCallCommand : IStoryCommand
    {
        private readonly StoriesService _service;
        private readonly StoryStatus _status;

        public FunctionCallCommand(StoriesService service, StoryStatus status)
        {
            _service = service;
            _status = status;
        }

        public bool RequireStoryText => true;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var functionName = _status.FunctionName ?? _status.Code ?? $"status_{_status.Id}";
            _service._logger?.LogWarning("FunctionCallCommand: function {Function} not implemented yet", functionName);
            return Task.FromResult<(bool success, string? message)>((false, $"Funzione '{functionName}' non ancora implementata."));
        }
    }

    internal sealed class NotImplementedCommand : IStoryCommand
    {
        private readonly string _message;

        public NotImplementedCommand(string reason)
        {
            _message = $"Operazione non implementata ({reason}).";
        }

        public bool RequireStoryText => true;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<(bool success, string? message)>((false, _message));
        }
    }
}
