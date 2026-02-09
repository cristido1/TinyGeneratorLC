using System;
using System.Threading.Tasks;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    internal sealed class DeleteMusicCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public DeleteMusicCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                _service.CleanMusicForRegeneration(context.Story.Id, context.FolderPath);
                var (mixSuccess, mixMessage) = _service.DeleteFinalMixAssets(context.Story.Id, context.FolderPath);
                string? message = mixSuccess
                    ? "Musica cancellata e mix finale rimosso"
                    : string.IsNullOrWhiteSpace(mixMessage)
                        ? "Musica cancellata con avvisi sul mix finale"
                        : $"Musica cancellata. {mixMessage}";
                return Task.FromResult<(bool success, string? message)>((mixSuccess, message));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la cancellazione della musica per la storia {Id}", context.Story.Id);
                return Task.FromResult<(bool success, string? message)>((false, ex.Message));
            }
        }
    }
}
