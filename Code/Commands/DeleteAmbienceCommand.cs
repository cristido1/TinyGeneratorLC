using System;
using System.Threading.Tasks;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    internal sealed class DeleteAmbienceCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public DeleteAmbienceCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                _service.CleanAmbienceForRegeneration(context.Story.Id, context.FolderPath);
                var (mixSuccess, mixMessage) = _service.DeleteFinalMixAssets(context.Story.Id, context.FolderPath);
                string? message = mixSuccess
                    ? "Rumori ambientali cancellati e mix finale aggiornato"
                    : string.IsNullOrWhiteSpace(mixMessage)
                        ? "Rumori ambientali cancellati con avvisi sul mix finale"
                        : $"Rumori ambientali cancellati. {mixMessage}";
                return Task.FromResult<(bool success, string? message)>((mixSuccess, message));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la cancellazione dei rumori ambientali per la storia {Id}", context.Story.Id);
                return Task.FromResult<(bool success, string? message)>((false, ex.Message));
            }
        }
    }
}
