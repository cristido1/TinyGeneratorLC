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

            var seriesContext = BuildSeriesContext(serie, episode, characters, plannerMethod, effectiveTipoPlanning);

            string? configOverrides = null;
            try
            {
                var cfg = new Dictionary<string, object>
                {
                    ["serie_id"] = _serieId,
                    ["serie_episode"] = episode.Number,
                    ["title"] = string.IsNullOrWhiteSpace(episode.Title)
                        ? $"{serie.Titolo} - Episodio {episode.Number}"
                        : episode.Title
                };

                if (!string.IsNullOrWhiteSpace(episode.InitialPhase)) cfg["initial_phase"] = episode.InitialPhase;
                if (!string.IsNullOrWhiteSpace(episode.StartSituation)) cfg["start_situation"] = episode.StartSituation;
                if (!string.IsNullOrWhiteSpace(episode.EpisodeGoal)) cfg["episode_goal"] = episode.EpisodeGoal;

                if (plannerMethod != null)
                {
                    cfg["planner_method_id"] = plannerMethod.Id;
                    cfg["planner_method_code"] = plannerMethod.Code;
                }

                if (effectiveTipoPlanning != null)
                {
                    cfg["tipo_planning_id"] = effectiveTipoPlanning.Id;
                    cfg["tipo_planning_code"] = effectiveTipoPlanning.Codice;
                    cfg["tipo_planning_successione_stati"] = effectiveTipoPlanning.SuccessioneStati;
                }

                if (episodeTipoPlanning != null)
                {
                    cfg["tipo_planning_source"] = "episode";
                }
                else if (seriesTipoPlanning != null)
                {
                    cfg["tipo_planning_source"] = "series";
                }
                if (template.CharactersStep.HasValue)
                {
                    cfg["characters_step"] = template.CharactersStep.Value;
                }
                configOverrides = JsonSerializer.Serialize(cfg);
            }
            catch (Exception ex)
            {
                _logger.Log("Warning", "SeriesEpisode", $"Config serialization failed: {ex.Message}");
            }

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

            var executeRunId = $"{_generationId}_exec";
            var executeCommand = new DelegateCommand(
                "ExecuteMultiStepTask",
                async ctx =>
                {
                    var executeCmd = new ExecuteMultiStepTaskCommand(
                        executionId,
                        _generationId,
                        _orchestrator,
                        _database,
                        _logger,
                        dispatcher: _dispatcher
                    );
                    await executeCmd.ExecuteAsync(ctx.CancellationToken);
                    return new CommandResult(true, "Series episode generation completed");
                });

            _dispatcher.Enqueue(
                executeCommand,
                runId: executeRunId,
                metadata: new Dictionary<string, string>
                {
                    ["agentName"] = agent.Name ?? string.Empty,
                    ["modelName"] = agent.ModelName ?? string.Empty,
                    ["operation"] = "series_episode",
                    ["serieTitle"] = serie.Titolo,
                    ["episodeNumber"] = episode.Number.ToString()
                }
            );
        }

        private static string BuildSeriesContext(
            Series serie,
            SeriesEpisode episode,
            List<SeriesCharacter> characters,
            PlannerMethod? plannerMethod,
            TipoPlanning? tipoPlanning)
        {
            var sb = new StringBuilder();

            sb.AppendLine("# CONTESTO SERIE");
            sb.AppendLine();
            sb.AppendLine($"**Titolo Serie:** {serie.Titolo}");
            if (!string.IsNullOrWhiteSpace(serie.Genere)) sb.AppendLine($"**Genere:** {serie.Genere}");
            if (!string.IsNullOrWhiteSpace(serie.Sottogenere)) sb.AppendLine($"**Sottogenere:** {serie.Sottogenere}");
            if (!string.IsNullOrWhiteSpace(serie.PeriodoNarrativo)) sb.AppendLine($"**Periodo Narrativo:** {serie.PeriodoNarrativo}");
            if (!string.IsNullOrWhiteSpace(serie.TonoBase)) sb.AppendLine($"**Tono:** {serie.TonoBase}");
            if (!string.IsNullOrWhiteSpace(serie.Target)) sb.AppendLine($"**Target:** {serie.Target}");
            if (!string.IsNullOrWhiteSpace(serie.Lingua)) sb.AppendLine($"**Lingua:** {serie.Lingua}");

            sb.AppendLine();
            sb.AppendLine("## Ambientazione");
            sb.AppendLine(!string.IsNullOrWhiteSpace(serie.AmbientazioneBase) ? serie.AmbientazioneBase : "(Non specificata)");

            sb.AppendLine();
            sb.AppendLine("## Premessa della Serie");
            sb.AppendLine(!string.IsNullOrWhiteSpace(serie.PremessaSerie) ? serie.PremessaSerie : "(Non specificata)");

            sb.AppendLine();
            sb.AppendLine("## Arco Narrativo della Serie");
            sb.AppendLine(!string.IsNullOrWhiteSpace(serie.ArcoNarrativoSerie) ? serie.ArcoNarrativoSerie : "(Non specificato)");

            sb.AppendLine();
            sb.AppendLine("## Stile di Scrittura");
            sb.AppendLine(!string.IsNullOrWhiteSpace(serie.StileScrittura) ? serie.StileScrittura : "(Non specificato)");

            sb.AppendLine();
            sb.AppendLine("## Regole Narrative");
            sb.AppendLine(!string.IsNullOrWhiteSpace(serie.RegoleNarrative) ? serie.RegoleNarrative : "(Non specificate)");

            if (!string.IsNullOrWhiteSpace(serie.SerieFinalGoal))
            {
                sb.AppendLine();
                sb.AppendLine("## Final Goal della Serie");
                sb.AppendLine(serie.SerieFinalGoal);
            }

            if (!string.IsNullOrWhiteSpace(serie.NoteAI))
            {
                sb.AppendLine();
                sb.AppendLine("## Note per l'AI");
                sb.AppendLine(serie.NoteAI);
            }

            sb.AppendLine();
            sb.AppendLine("## Pianificazione");
            if (plannerMethod != null)
            {
                var descr = string.IsNullOrWhiteSpace(plannerMethod.Description) ? string.Empty : $" - {plannerMethod.Description}";
                sb.AppendLine($"**Planner method (strategico):** {plannerMethod.Code}{descr}");
            }
            else
            {
                sb.AppendLine("**Planner method (strategico):** (Non assegnato)");
            }

            if (tipoPlanning != null)
            {
                sb.AppendLine($"**Tipo planning (tattico):** {tipoPlanning.Nome} ({tipoPlanning.Codice})");
                sb.AppendLine($"**Successione stati:** {tipoPlanning.SuccessioneStati}");
                sb.AppendLine("**Stati ammessi:** AZIONE, STASI, ERRORE, EFFETTO");
            }
            else
            {
                sb.AppendLine("**Tipo planning (tattico):** (Non assegnato)");
            }

            sb.AppendLine();
            sb.AppendLine("## Personaggi Ricorrenti");
            if (characters.Count == 0)
            {
                sb.AppendLine("(Nessun personaggio specificato)");
            }
            else
            {
                foreach (var c in characters)
                {
                    sb.AppendLine($"- {c.Name} ({c.Gender})");
                    if (!string.IsNullOrWhiteSpace(c.Description)) sb.AppendLine($"  Descrizione: {c.Description}");
                    if (!string.IsNullOrWhiteSpace(c.Eta)) sb.AppendLine($"  Eta: {c.Eta}");
                    if (!string.IsNullOrWhiteSpace(c.Formazione)) sb.AppendLine($"  Formazione: {c.Formazione}");
                    if (!string.IsNullOrWhiteSpace(c.Specializzazione)) sb.AppendLine($"  Specializzazione: {c.Specializzazione}");
                    if (!string.IsNullOrWhiteSpace(c.Profilo)) sb.AppendLine($"  Profilo: {c.Profilo}");
                    if (!string.IsNullOrWhiteSpace(c.ConflittoInterno)) sb.AppendLine($"  Conflitto Interno: {c.ConflittoInterno}");
                    if (c.EpisodeIn.HasValue || c.EpisodeOut.HasValue)
                    {
                        sb.AppendLine($"  Presenza: {c.EpisodeIn?.ToString() ?? "?"} - {c.EpisodeOut?.ToString() ?? "?"}");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("## Episodio da scrivere");
            sb.AppendLine($"**Numero:** {episode.Number}");
            if (!string.IsNullOrWhiteSpace(episode.Title))
            {
                sb.AppendLine($"**Titolo:** {episode.Title}");
            }

            if (!string.IsNullOrWhiteSpace(episode.InitialPhase))
            {
                sb.AppendLine($"**Initial phase:** {episode.InitialPhase}");
            }

            if (!string.IsNullOrWhiteSpace(episode.StartSituation))
            {
                sb.AppendLine();
                sb.AppendLine("### Start situation");
                sb.AppendLine(episode.StartSituation);
            }

            if (!string.IsNullOrWhiteSpace(episode.EpisodeGoal))
            {
                sb.AppendLine();
                sb.AppendLine("### Episode goal");
                sb.AppendLine(episode.EpisodeGoal);
            }
            sb.AppendLine();
            sb.AppendLine("### Trama");
            sb.AppendLine(!string.IsNullOrWhiteSpace(episode.Trama) ? episode.Trama : "(Non specificata)");

            return sb.ToString();
        }
    }
}


