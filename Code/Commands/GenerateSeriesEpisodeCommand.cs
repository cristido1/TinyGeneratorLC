using System.Text;
using System.Text.Json;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Comando per generare un nuovo episodio di una serie esistente.
    /// Carica i dati della serie dal database e li usa come contesto per la generazione.
    /// </summary>
    public class GenerateSeriesEpisodeCommand : ICommand
    {
        private readonly int _serieId;
        private readonly int _writerAgentId;
        private readonly Guid _generationId;
        private readonly DatabaseService _database;
        private readonly MultiStepOrchestrationService _orchestrator;
        private readonly ICommandEnqueuer _dispatcher;
        private readonly ICustomLogger _logger;
        private readonly string? _customPrompt; // Prompt aggiuntivo opzionale per questo episodio specifico

        public GenerateSeriesEpisodeCommand(
            int serieId,
            int writerAgentId,
            Guid generationId,
            DatabaseService database,
            MultiStepOrchestrationService orchestrator,
            ICommandEnqueuer dispatcher,
            ICustomLogger logger,
            string? customPrompt = null)
        {
            _serieId = serieId;
            _writerAgentId = writerAgentId;
            _generationId = generationId;
            _database = database;
            _orchestrator = orchestrator;
            _dispatcher = dispatcher;
            _logger = logger;
            _customPrompt = customPrompt;
        }

        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var threadId = _generationId.GetHashCode();

            _logger.Log("Information", "SeriesEpisode", $"Starting series episode generation - Serie: {_serieId}, Agent: {_writerAgentId}");

            try
            {
                // â•”â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•—
                // â•‘ FASE 1: Carica serie dal database                            â•‘
                // â•šâ•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                var serie = _database.GetSeriesById(_serieId);
                if (serie == null)
                {
                    _logger.Log("Error", "SeriesEpisode", $"Serie {_serieId} not found, aborting");
                    return;
                }

                // â•”â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•—
                // â•‘ FASE 2: Carica agente writer                                 â•‘
                // â•šâ•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                var agent = _database.GetAgentById(_writerAgentId);
                if (agent == null)
                {
                    _logger.Log("Error", "SeriesEpisode", $"Agent {_writerAgentId} not found, aborting");
                    return;
                }

                // Check if agent has multi-step template
                if (!agent.MultiStepTemplateId.HasValue)
                {
                    _logger.Log("Error", "SeriesEpisode", $"Agent {agent.Name} has no multi-step template configured");
                    return;
                }

                // Load template
                var template = _database.GetStepTemplateById(agent.MultiStepTemplateId.Value);
                if (template == null)
                {
                    _logger.Log("Error", "SeriesEpisode", $"Template {agent.MultiStepTemplateId} not found, aborting");
                    return;
                }

                _logger.Log("Information", "SeriesEpisode", $"Using template: {template.Name} for series '{serie.Titolo}'");

                var plannerMethod = serie.PlannerMethodId.HasValue
                    ? _database.GetPlannerMethodById(serie.PlannerMethodId.Value)
                    : null;

                var defaultTipoPlanning = serie.DefaultTipoPlanningId.HasValue
                    ? _database.GetTipoPlanningById(serie.DefaultTipoPlanningId.Value)
                    : null;

                // â•”â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•—
                // â•‘ FASE 3: Costruisci prompt con i dati della serie            â•‘
                // â•šâ•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                var seriesContext = SeriesEpisodeCommandSupport.BuildSeriesContext(
                    serie,
                    plannerMethod,
                    defaultTipoPlanning,
                    _customPrompt,
                    serie.EpisodiGenerati + 1);

                // Calcola numero episodio (ultimo + 1)
                var episodeNumber = serie.EpisodiGenerati + 1;

                // Titolo dell'episodio: "Serie - Episodio N"
                var episodeTitle = $"{serie.Titolo} - Episodio {episodeNumber}";

                // â•”â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•—
                // â•‘ FASE 4: Build config con serie_id e episode number          â•‘
                // â•šâ•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                var configOverrides = SeriesEpisodeExecutionSupport.BuildConfigOverridesForNextEpisode(
                    _serieId,
                    episodeNumber,
                    episodeTitle,
                    plannerMethod,
                    defaultTipoPlanning,
                    template.CharactersStep,
                    _logger);

                // Store model info for later use (id-only)
                int? modelId = agent.ModelId;

                // â•”â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•—
                // â•‘ FASE 5: Start task execution                                 â•‘
                // â•šâ•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                var executionId = await _orchestrator.StartTaskExecutionAsync(
                    taskType: "story",
                    entityId: null, // Story will be created on success
                    stepPrompt: template.StepPrompt,
                    executorAgentId: agent.Id,
                    checkerAgentId: null,
                    configOverrides: configOverrides,
                    initialContext: seriesContext, // â† Context della serie!
                    threadId: threadId,
                    templateInstructions: string.IsNullOrWhiteSpace(template.Instructions) ? null : template.Instructions
                );

                _logger.Log("Information", "SeriesEpisode", $"Started task execution {executionId} for episode {episodeNumber}");

                // â•”â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•—
                // â•‘ FASE 6: Enqueue execution command                            â•‘
                // â•šâ•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
                SeriesEpisodeExecutionSupport.EnqueueExecuteMultiStepTask(
                    _dispatcher,
                    _generationId,
                    executionId,
                    _orchestrator,
                    _database,
                    _logger,
                    agent.Name ?? string.Empty,
                    modelId.HasValue ? (_database.GetModelInfoById(modelId.Value)?.Name ?? string.Empty) : string.Empty,
                    serie.Titolo,
                    episodeNumber,
                    afterSuccess: _ => IncrementEpisodeCounterAsync(_serieId));
                ct.ThrowIfCancellationRequested();

                _logger.Log("Information", "SeriesEpisode", $"Enqueued ExecuteMultiStepTaskCommand for series episode");
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "SeriesEpisode", $"Failed to start series episode generation: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Incrementa il contatore episodi_generati della serie
        /// </summary>
        private async Task IncrementEpisodeCounterAsync(int serieId)
        {
            try
            {
                _database.IncrementSeriesEpisodeCount(serieId);
                _logger.Log("Information", "SeriesEpisode", $"Incremented episode counter for series {serieId}");
            }
            catch (Exception ex)
            {
                _logger.Log("Warning", "SeriesEpisode", $"Failed to increment episode counter: {ex.Message}");
            }
        }
    }
}


