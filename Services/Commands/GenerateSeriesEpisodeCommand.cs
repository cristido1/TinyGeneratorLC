using System.Text;
using System.Text.Json;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Comando per generare un nuovo episodio di una serie esistente.
    /// Carica i dati della serie dal database e li usa come contesto per la generazione.
    /// </summary>
    public class GenerateSeriesEpisodeCommand
    {
        private readonly int _serieId;
        private readonly int _writerAgentId;
        private readonly Guid _generationId;
        private readonly DatabaseService _database;
        private readonly MultiStepOrchestrationService _orchestrator;
        private readonly ICommandDispatcher _dispatcher;
        private readonly ICustomLogger _logger;
        private readonly string? _customPrompt; // Prompt aggiuntivo opzionale per questo episodio specifico

        public GenerateSeriesEpisodeCommand(
            int serieId,
            int writerAgentId,
            Guid generationId,
            DatabaseService database,
            MultiStepOrchestrationService orchestrator,
            ICommandDispatcher dispatcher,
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
            var threadId = _generationId.GetHashCode();

            _logger.Log("Information", "SeriesEpisode", $"Starting series episode generation - Serie: {_serieId}, Agent: {_writerAgentId}");

            try
            {
                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 1: Carica serie dal database                            ║
                // ╚══════════════════════════════════════════════════════════════╝
                var serie = _database.GetSeriesById(_serieId);
                if (serie == null)
                {
                    _logger.Log("Error", "SeriesEpisode", $"Serie {_serieId} not found, aborting");
                    return;
                }

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 2: Carica agente writer                                 ║
                // ╚══════════════════════════════════════════════════════════════╝
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

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 3: Costruisci prompt con i dati della serie            ║
                // ╚══════════════════════════════════════════════════════════════╝
                var seriesContext = BuildSeriesContext(serie);

                // Calcola numero episodio (ultimo + 1)
                var episodeNumber = serie.EpisodiGenerati + 1;

                // Titolo dell'episodio: "Serie - Episodio N"
                var episodeTitle = $"{serie.Titolo} - Episodio {episodeNumber}";

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 4: Build config con serie_id e episode number          ║
                // ╚══════════════════════════════════════════════════════════════╝
                string? configOverrides = null;
                try
                {
                    var cfg = new Dictionary<string, object>
                    {
                        ["serie_id"] = _serieId,
                        ["serie_episode"] = episodeNumber,
                        ["title"] = episodeTitle
                    };

                    if (template.CharactersStep.HasValue)
                    {
                        cfg["characters_step"] = template.CharactersStep.Value;
                    }

                    configOverrides = JsonSerializer.Serialize(cfg);
                }
                catch (Exception ex)
                {
                    _logger.Log("Warning", "SeriesEpisode", $"Failed to serialize config: {ex.Message}");
                    configOverrides = null;
                }

                // Store model info for later use (id-only)
                int? modelId = agent.ModelId;

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 5: Start task execution                                 ║
                // ╚══════════════════════════════════════════════════════════════╝
                var executionId = await _orchestrator.StartTaskExecutionAsync(
                    taskType: "story",
                    entityId: null, // Story will be created on success
                    stepPrompt: template.StepPrompt,
                    executorAgentId: agent.Id,
                    checkerAgentId: null,
                    configOverrides: configOverrides,
                    initialContext: seriesContext, // ← Context della serie!
                    threadId: threadId,
                    templateInstructions: string.IsNullOrWhiteSpace(template.Instructions) ? null : template.Instructions
                );

                _logger.Log("Information", "SeriesEpisode", $"Started task execution {executionId} for episode {episodeNumber}");

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 6: Enqueue execution command                            ║
                // ╚══════════════════════════════════════════════════════════════╝
                var executeRunId = $"{_generationId}_exec";
                _dispatcher.Enqueue(
                    "ExecuteMultiStepTask",
                    async ctx => {
                        var executeCmd = new ExecuteMultiStepTaskCommand(
                            executionId,
                            _generationId,
                            _orchestrator,
                            _database,
                            _logger,
                            dispatcher: _dispatcher
                        );
                        await executeCmd.ExecuteAsync(ctx.CancellationToken);
                        
                        // After successful completion, increment episode counter
                        await IncrementEpisodeCounterAsync(_serieId);
                        
                        return new CommandResult(true, "Series episode generation completed");
                    },
                    runId: executeRunId,
                    metadata: new Dictionary<string, string>
                    {
                        ["agentName"] = agent.Name ?? string.Empty,
                        ["modelName"] = modelId.HasValue ? (_database.GetModelInfoById(modelId.Value)?.Name ?? string.Empty) : string.Empty,
                        ["operation"] = "series_episode",
                        ["serieTitle"] = serie.Titolo,
                        ["episodeNumber"] = episodeNumber.ToString()
                    }
                );

                _logger.Log("Information", "SeriesEpisode", $"Enqueued ExecuteMultiStepTaskCommand for series episode");
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "SeriesEpisode", $"Failed to start series episode generation: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Costruisce un prompt completo con tutti i dati della serie
        /// </summary>
        private string BuildSeriesContext(Series serie)
        {
            var sb = new StringBuilder();

            sb.AppendLine("# CONTESTO SERIE");
            sb.AppendLine();
            sb.AppendLine($"**Titolo Serie:** {serie.Titolo}");
            
            if (!string.IsNullOrWhiteSpace(serie.Genere))
            {
                sb.AppendLine($"**Genere:** {serie.Genere}");
            }
            
            if (!string.IsNullOrWhiteSpace(serie.Sottogenere))
            {
                sb.AppendLine($"**Sottogenere:** {serie.Sottogenere}");
            }
            
            if (!string.IsNullOrWhiteSpace(serie.PeriodoNarrativo))
            {
                sb.AppendLine($"**Periodo Narrativo:** {serie.PeriodoNarrativo}");
            }
            
            if (!string.IsNullOrWhiteSpace(serie.TonoBase))
            {
                sb.AppendLine($"**Tono:** {serie.TonoBase}");
            }
            
            if (!string.IsNullOrWhiteSpace(serie.Target))
            {
                sb.AppendLine($"**Target:** {serie.Target}");
            }
            
            if (!string.IsNullOrWhiteSpace(serie.Lingua))
            {
                sb.AppendLine($"**Lingua:** {serie.Lingua}");
            }

            sb.AppendLine();
            sb.AppendLine("## Ambientazione");
            if (!string.IsNullOrWhiteSpace(serie.AmbientazioneBase))
            {
                sb.AppendLine(serie.AmbientazioneBase);
            }
            else
            {
                sb.AppendLine("(Non specificata)");
            }

            sb.AppendLine();
            sb.AppendLine("## Premessa della Serie");
            if (!string.IsNullOrWhiteSpace(serie.PremessaSerie))
            {
                sb.AppendLine(serie.PremessaSerie);
            }
            else
            {
                sb.AppendLine("(Non specificata)");
            }

            sb.AppendLine();
            sb.AppendLine("## Arco Narrativo della Serie");
            if (!string.IsNullOrWhiteSpace(serie.ArcoNarrativoSerie))
            {
                sb.AppendLine(serie.ArcoNarrativoSerie);
            }
            else
            {
                sb.AppendLine("(Non specificato)");
            }

            sb.AppendLine();
            sb.AppendLine("## Stile di Scrittura");
            if (!string.IsNullOrWhiteSpace(serie.StileScrittura))
            {
                sb.AppendLine(serie.StileScrittura);
            }
            else
            {
                sb.AppendLine("(Non specificato)");
            }

            sb.AppendLine();
            sb.AppendLine("## Regole Narrative");
            if (!string.IsNullOrWhiteSpace(serie.RegoleNarrative))
            {
                sb.AppendLine(serie.RegoleNarrative);
            }
            else
            {
                sb.AppendLine("(Non specificate)");
            }

            if (!string.IsNullOrWhiteSpace(serie.NoteAI))
            {
                sb.AppendLine();
                sb.AppendLine("## Note per l'AI");
                sb.AppendLine(serie.NoteAI);
            }

            // Numero episodio
            sb.AppendLine();
            sb.AppendLine($"**Numero Episodio:** {serie.EpisodiGenerati + 1}");

            // Custom prompt se presente
            if (!string.IsNullOrWhiteSpace(_customPrompt))
            {
                sb.AppendLine();
                sb.AppendLine("## Istruzioni Specifiche per Questo Episodio");
                sb.AppendLine(_customPrompt);
            }

            return sb.ToString();
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
