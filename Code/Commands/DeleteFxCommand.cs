using System;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    internal sealed class DeleteFxCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public DeleteFxCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                _service.CleanFxForRegeneration(context.Story.Id, context.FolderPath);
                var (mixSuccess, mixMessage) = _service.DeleteFinalMixAssets(context.Story.Id, context.FolderPath);
                string? message = mixSuccess
                    ? "Effetti sonori cancellati e mix finale aggiornato"
                    : string.IsNullOrWhiteSpace(mixMessage)
                        ? "Effetti cancellati con avvisi sul mix finale"
                        : $"Effetti cancellati. {mixMessage}";
                return Task.FromResult<(bool success, string? message)>((mixSuccess, message));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la cancellazione degli effetti per la storia {Id}", context.Story.Id);
                return Task.FromResult<(bool success, string? message)>((false, ex.Message));
            }
        }
    }
}
