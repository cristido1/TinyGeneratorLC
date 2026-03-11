using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Comando che esegue l'intero pipeline di produzione storia:
    /// 1. Genera storie da tutti gli agenti writer attivi (multi-step)
    /// 2. Avvia generazione storie in parallelo
    /// 3. Aspetta completamento tutte le generazioni
    /// 4. Raccogli storie generate con successo
    /// 5. Valuta le storie con gli agenti evaluator
    /// 6. Seleziona la storia con il punteggio più alto
    /// 7. Esegui il pipeline completo (TTS, voci, audio) sulla storia vincitrice
    /// </summary>
    public class FullStoryPipelineCommand : ICommand
    {
        private readonly CommandTuningOptions _tuning;

        private readonly string _theme;
        private readonly Guid _generationId;
        private readonly DatabaseService _database;
        private readonly MultiStepOrchestrationService _orchestrator;
        private readonly StoriesService _storiesService;
        private readonly ICommandEnqueuer _dispatcher;
        private readonly ICustomLogger _logger;

        public FullStoryPipelineCommand(
            string theme,
            Guid generationId,
            DatabaseService database,
            MultiStepOrchestrationService orchestrator,
            StoriesService storiesService,
            ICommandEnqueuer dispatcher,
            ICustomLogger logger,
            CommandTuningOptions? tuning = null)
        {
            _theme = theme;
            _generationId = generationId;
            _database = database;
            _orchestrator = orchestrator;
            _storiesService = storiesService;
            _dispatcher = dispatcher;
            _logger = logger;
            _tuning = tuning ?? new CommandTuningOptions();
        }

        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var runId = _generationId.ToString();
            var threadId = _generationId.GetHashCode();

            try
            {
                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 1: Ottieni tutti gli agenti writer attivi               ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPipelinePhaseAsync("Fase 1/7: Ricerca agenti writer...");
                await LogAndNotifyAsync("🎬 Avvio pipeline completo...");

                var writerAgents = _database.ListAgents()
                    .Where(a => a.IsActive &&
                                a.Role.Contains("writer", StringComparison.OrdinalIgnoreCase) &&
                                a.MultiStepTemplateId.HasValue)
                    .ToList();

                if (writerAgents.Count == 0)
                {
                    await LogAndNotifyAsync("❌ Nessun agente writer attivo con template multi-step trovato", "error");
                    return;
                }

                await LogAndNotifyAsync($"📝 Trovati {writerAgents.Count} agenti writer attivi");

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 2: Avvia generazione storie in parallelo                ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPipelinePhaseAsync($"Fase 2/7: Generazione {writerAgents.Count} storie in parallelo...");
                var generationTasks = new List<(Agent agent, long executionId, Task task)>();

                foreach (var agent in writerAgents)
                {
                    ct.ThrowIfCancellationRequested();

                    var template = _database.GetStepTemplateById(agent.MultiStepTemplateId!.Value);
                    if (template == null)
                    {
                        await LogAndNotifyAsync($"⚠️ Template {agent.MultiStepTemplateId} non trovato per agente {agent.Description}, skip", "warning");
                        continue;
                    }

                    await LogAndNotifyAsync($"🚀 Avvio generazione con agente: {agent.Description}");

                    // Build config with characters_step if defined in template
                    string? configOverrides = null;
                    if (template.CharactersStep.HasValue)
                    {
                        configOverrides = System.Text.Json.JsonSerializer.Serialize(new { characters_step = template.CharactersStep.Value });
                    }

                    // Start task execution without entity (story will be created on success)
                    var executionId = await _orchestrator.StartTaskExecutionAsync(
                        taskType: "story",
                        entityId: null,
                        stepPrompt: template.StepPrompt,
                        executorAgentId: agent.Id,
                        checkerAgentId: null,
                        configOverrides: configOverrides,
                        initialContext: _theme,
                        threadId: threadId + agent.Id, // unique thread per agent
                        templateInstructions: string.IsNullOrWhiteSpace(template.Instructions) ? null : template.Instructions
                    );

                    // Execute async but don't await yet
                    var task = ExecuteWriterGenerationAsync(executionId, agent, threadId + agent.Id, ct);
                    generationTasks.Add((agent, executionId, task));
                }

                if (generationTasks.Count == 0)
                {
                    await LogAndNotifyAsync("❌ Nessuna generazione avviata", "error");
                    return;
                }

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 3: Aspetta completamento tutte le generazioni           ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPipelinePhaseAsync($"Fase 3/7: Attendendo {generationTasks.Count} generazioni...");
                await LogAndNotifyAsync($"⏳ Attendendo il completamento di {generationTasks.Count} generazioni...");

                await Task.WhenAll(generationTasks.Select(t => t.task));

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 4: Raccogli storie generate con successo                ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPipelinePhaseAsync("Fase 4/7: Raccolta storie generate...");
                var successfulStories = new List<(Agent agent, long storyId, TaskExecution execution)>();

                foreach (var (agent, executionId, _) in generationTasks)
                {
                    var execution = _database.GetTaskExecutionById(executionId);
                    if (execution == null)
                    {
                        await LogAndNotifyAsync($"⚠️ Execution {executionId} non trovata per {agent.Description}", "warning");
                        continue;
                    }

                    if (execution.Status == "completed" && execution.EntityId.HasValue)
                    {
                        successfulStories.Add((agent, execution.EntityId.Value, execution));
                        await LogAndNotifyAsync($"✅ {agent.Description}: storia {execution.EntityId.Value} generata con successo", "success");
                    }
                    else
                    {
                        await LogAndNotifyAsync($"❌ {agent.Description}: generazione fallita (status: {execution.Status})", "error");
                    }
                }

                if (successfulStories.Count == 0)
                {
                    await LogAndNotifyAsync("❌ Nessuna storia generata con successo", "error");
                    return;
                }

                await LogAndNotifyAsync($"📚 {successfulStories.Count} storie generate con successo");

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 5: Valuta le storie con gli agenti evaluator            ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPipelinePhaseAsync("Fase 5/7: Ricerca agenti evaluator...");
                var evaluatorAgents = _database.ListAgents()
                    .Where(a => a.IsActive &&
                                (a.Role.Equals("story_evaluator", StringComparison.OrdinalIgnoreCase) ||
                                 a.Role.Equals("evaluator", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (evaluatorAgents.Count == 0)
                {
                    await LogAndNotifyAsync("⚠️ Nessun agente evaluator attivo trovato, selezione della prima storia", "warning");
                    // Se non ci sono valutatori, prendi la prima storia
                    var (agent, storyId, _) = successfulStories[0];
                    await BroadcastPipelinePhaseAsync("Fase 7/7: Pipeline audio...");
                    var pipelineOk = await EnqueueFullAudioPipelineForStoryAsync(storyId, agent.Description, ct);
                    if (pipelineOk)
                    {
                        await LogAndNotifyAsync("🎉 Pipeline completo terminato con successo!", "success");
                        await _logger.BroadcastTaskComplete(_generationId, "completed");
                    }
                    else
                    {
                        await LogAndNotifyAsync("❌ Pipeline audio fallito", "error");
                        await _logger.BroadcastTaskComplete(_generationId, "failed");
                    }
                    return;
                }

                await BroadcastPipelinePhaseAsync($"Fase 5/7: Valutazione {successfulStories.Count} storie con {evaluatorAgents.Count} evaluator...");
                await LogAndNotifyAsync($"🔍 Valutazione con {evaluatorAgents.Count} agenti evaluator...");

                var storyScores = new Dictionary<long, List<(int evaluatorId, double score)>>();

                foreach (var (agent, storyId, _) in successfulStories)
                {
                    storyScores[storyId] = new List<(int, double)>();

                    foreach (var evaluator in evaluatorAgents)
                    {
                        ct.ThrowIfCancellationRequested();

                        await LogAndNotifyAsync($"📊 Valutazione storia {storyId} con {evaluator.Description}...");

                        try
                        {
                            var (success, score, error) = await _storiesService.EvaluateStoryWithAgentAsync(storyId, evaluator.Id);
                            
                            if (success && score > 0)
                            {
                                storyScores[storyId].Add((evaluator.Id, score));
                                await LogAndNotifyAsync($"   {evaluator.Description} → punteggio: {score:F1}");
                            }
                            else
                            {
                                await LogAndNotifyAsync($"   ⚠️ {evaluator.Description}: valutazione fallita - {error}", "warning");
                            }
                        }
                        catch (Exception ex)
                        {
                            await LogAndNotifyAsync($"   ❌ {evaluator.Description}: errore - {ex.Message}", "error");
                        }
                    }
                }

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 6: Seleziona la storia con il punteggio più alto        ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPipelinePhaseAsync("Fase 6/7: Selezione storia vincitrice...");
                long bestStoryId = 0;
                double bestAvgScore = -1;
                string bestWriterName = "";

                foreach (var (agent, storyId, _) in successfulStories)
                {
                    var scores = storyScores[storyId];
                    if (scores.Count == 0)
                    {
                        await LogAndNotifyAsync($"⚠️ Storia {storyId} ({agent.Description}): nessun punteggio valido", "warning");
                        continue;
                    }

                    var avgScore = scores.Average(s => s.score);
                    await LogAndNotifyAsync($"📈 Storia {storyId} ({agent.Description}): punteggio medio = {avgScore:F2}");

                    if (avgScore > bestAvgScore)
                    {
                        bestAvgScore = avgScore;
                        bestStoryId = storyId;
                        bestWriterName = agent.Description;
                    }
                }

                if (bestStoryId == 0)
                {
                    // Fallback: prendi la prima storia se non ci sono punteggi
                    var (agent, storyId, _) = successfulStories[0];
                    bestStoryId = storyId;
                    bestWriterName = agent.Description;
                    await LogAndNotifyAsync($"⚠️ Nessun punteggio valido, selezionata prima storia: {bestStoryId}", "warning");
                }
                else
                {
                    await LogAndNotifyAsync($"🏆 Storia vincitrice: {bestStoryId} di {bestWriterName} con punteggio {bestAvgScore:F2}", "success");
                }

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 7: Esegui il pipeline completo sulla storia vincitrice  ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPipelinePhaseAsync($"Fase 7/7: Pipeline audio su storia {bestStoryId}...");
                var pipelineSuccess = await EnqueueFullAudioPipelineForStoryAsync(bestStoryId, bestWriterName, ct);

                if (pipelineSuccess)
                {
                    await LogAndNotifyAsync("🎉 Pipeline completo terminato con successo!", "success");
                    await _logger.BroadcastTaskComplete(_generationId, "completed");
                }
                else
                {
                    await LogAndNotifyAsync("❌ Pipeline audio fallito", "error");
                    await _logger.BroadcastTaskComplete(_generationId, "failed");
                }
            }
            catch (OperationCanceledException)
            {
                await LogAndNotifyAsync("⚠️ Pipeline cancellato", "warning");
                await _logger.BroadcastTaskComplete(_generationId, "cancelled");
            }
            catch (Exception ex)
            {
                await LogAndNotifyAsync($"❌ Errore pipeline: {ex.Message}", "error");
                _logger.Log("Error", "FullPipeline", $"Pipeline error: {ex.Message}", ex.ToString());
                await _logger.BroadcastTaskComplete(_generationId, "failed");
            }
        }

        private async Task ExecuteWriterGenerationAsync(long executionId, Agent agent, int threadId, CancellationToken ct)
        {
            try
            {
                using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                executionCts.CancelAfter(TimeSpan.FromHours(2));

                await _orchestrator.ExecuteAllStepsAsync(
                    executionId,
                    threadId,
                    (desc, current, max, stepDesc) =>
                    {
                        _logger.Log("Information", "FullPipeline", $"[{agent.Description}] Step {current}/{max}: {stepDesc}");
                    },
                    executionCts.Token
                );
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "FullPipeline", $"Writer {agent.Description} execution error: {ex.Message}");
                // Don't rethrow - we want to continue with other writers
            }
        }

        private async Task LogAndNotifyAsync(string message, string extraClass = "")
        {
            var runId = _generationId.ToString();
            _logger.Append(runId, message);
            _logger.Log("Information", "FullPipeline", message);

            try
            {
                await _logger.NotifyGroupAsync(runId, "ProgressAppended", message, extraClass);
            }
            catch
            {
                // Ignore notification errors
            }
        }

        private async Task BroadcastAudioPipelineStepAsync(int currentStep, int totalSteps, string stepDescription)
        {
            try
            {
                await _logger.BroadcastStepProgress(_generationId, currentStep, totalSteps, stepDescription);
            }
            catch
            {
                // Ignore broadcast errors
            }
        }

        private async Task BroadcastPipelinePhaseAsync(string phaseDescription)
        {
            try
            {
                // Usa BroadcastStepProgress con step 0 per indicare una fase macro (non un sub-step)
                await _logger.BroadcastStepProgress(_generationId, 0, 7, phaseDescription);
            }
            catch
            {
                // Ignore broadcast errors
            }
        }

        private async Task<bool> EnqueueFullAudioPipelineForStoryAsync(long storyId, string writerName, CancellationToken ct)
        {
            await BroadcastPipelinePhaseAsync($"Fase 7/7: Pipeline audio su storia {storyId} ({writerName})...");

            if (ct.IsCancellationRequested)
            {
                await LogAndNotifyAsync("Pipeline audio cancellato prima di accodare la catena stati", "warning");
                return false;
            }

            var chainId = _storiesService.EnqueueAllNextStatusEnqueuer(storyId, trigger: "full_pipeline", priority: 3);
            if (string.IsNullOrWhiteSpace(chainId))
            {
                await LogAndNotifyAsync("❌ Impossibile accodare la catena completa di stati", "error");
                return false;
            }

            await LogAndNotifyAsync($"✅ Catena stati audio accodata (id {chainId}).", "success");
            return true;
        }

    }
}

