using System.Text;
using System.Text.Json;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Generate a series episode using series + characters + episode data from DB.
    /// </summary>
    public sealed class GenerateSeriesEpisodeFromDbCommand : ICommand
    {
        private readonly int _serieId;
        private readonly int _episodeId;
        private readonly int _writerAgentId;
        private readonly Guid _generationId;
        private readonly DatabaseService _database;
        private readonly MultiStepOrchestrationService _orchestrator;
        private readonly ICommandEnqueuer _dispatcher;
        private readonly ICustomLogger _logger;

        public GenerateSeriesEpisodeFromDbCommand(
            int serieId,
            int episodeId,
            int writerAgentId,
            Guid generationId,
            DatabaseService database,
            MultiStepOrchestrationService orchestrator,
            ICommandEnqueuer dispatcher,
            ICustomLogger logger)
        {
            _serieId = serieId;
            _episodeId = episodeId;
            _writerAgentId = writerAgentId;
            _generationId = generationId;
            _database = database;
            _orchestrator = orchestrator;
            _dispatcher = dispatcher;
            _logger = logger;
        }

        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var threadId = _generationId.GetHashCode();

            _logger.Log("Information", "SeriesEpisode", $"Start episode generation - Serie: {_serieId}, Episode: {_episodeId}, Agent: {_writerAgentId}");

            var serie = _database.GetSeriesById(_serieId);
            if (serie == null)
            {
                _logger.Log("Error", "SeriesEpisode", $"Serie {_serieId} not found");
                return;
            }

            var episode = _database.GetSeriesEpisodeById(_episodeId);
            if (episode == null || episode.SerieId != _serieId)
            {
                _logger.Log("Error", "SeriesEpisode", $"Episode {_episodeId} not found for serie {_serieId}");
                return;
            }

            var agent = _database.GetAgentById(_writerAgentId);
            if (agent == null)
            {
                _logger.Log("Error", "SeriesEpisode", $"Agent {_writerAgentId} not found");
                return;
            }
            if (!agent.MultiStepTemplateId.HasValue)
            {
                _logger.Log("Error", "SeriesEpisode", $"Agent {agent.Name} has no multi-step template configured");
                return;
            }

            var template = _database.GetStepTemplateById(agent.MultiStepTemplateId.Value);
            if (template == null)
            {
                _logger.Log("Error", "SeriesEpisode", $"Template {agent.MultiStepTemplateId.Value} not found");
                return;
            }

            var characters = _database.ListSeriesCharacters(_serieId);

            var plannerMethod = serie.PlannerMethodId.HasValue
                ? _database.GetPlannerMethodById(serie.PlannerMethodId.Value)
                : null;

            var seriesTipoPlanning = serie.DefaultTipoPlanningId.HasValue
                ? _database.GetTipoPlanningById(serie.DefaultTipoPlanningId.Value)
                : null;

            var episodeTipoPlanning = episode.TipoPlanningId.HasValue
                ? _database.GetTipoPlanningById(episode.TipoPlanningId.Value)
                : null;

            var effectiveTipoPlanning = episodeTipoPlanning ?? seriesTipoPlanning;

            var seriesContext = SeriesEpisodeCommandSupport.BuildSeriesContextForEpisode(
                serie,
                episode,
                characters,
                plannerMethod,
                effectiveTipoPlanning);

            var configOverrides = SeriesEpisodeExecutionSupport.BuildConfigOverridesForExistingEpisode(
                _serieId,
                episode,
                plannerMethod,
                effectiveTipoPlanning,
                episodeTipoPlanning,
                seriesTipoPlanning,
                template.CharactersStep,
                _logger);

            var executionId = await _orchestrator.StartTaskExecutionAsync(
                taskType: "story",
                entityId: null,
                stepPrompt: template.StepPrompt,
                executorAgentId: agent.Id,
                checkerAgentId: null,
                configOverrides: configOverrides,
                initialContext: seriesContext,
                threadId: threadId,
                templateInstructions: string.IsNullOrWhiteSpace(template.Instructions) ? null : template.Instructions
            );
            ct.ThrowIfCancellationRequested();

            SeriesEpisodeExecutionSupport.EnqueueExecuteMultiStepTask(
                _dispatcher,
                _generationId,
                executionId,
                _orchestrator,
                _database,
                _logger,
                agent.Name ?? string.Empty,
                agent.ModelName ?? string.Empty,
                serie.Titolo,
                episode.Number);
        }

    }
}


