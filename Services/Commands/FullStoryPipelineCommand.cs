using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Comando che esegue l'intero pipeline di produzione storia:
    /// 1. Genera storie da tutti gli agenti writer attivi (multi-step)
    /// 2. Aspetta il completamento di tutte le generazioni
    /// 3. Valuta le storie generate con successo tramite gli agenti evaluator
    /// 4. Seleziona la storia con il punteggio più alto
    /// 5. Esegue il pipeline completo: tts_schema, normalize characters, normalize sentiments, TTS, ambience, FX, final mix
    /// </summary>
    public class FullStoryPipelineCommand
    {
        private readonly string _theme;
        private readonly Guid _generationId;
        private readonly DatabaseService _database;
        private readonly MultiStepOrchestrationService _orchestrator;
        private readonly StoriesService _storiesService;
        private readonly ICommandDispatcher _dispatcher;
        private readonly ICustomLogger _logger;

        public FullStoryPipelineCommand(
            string theme,
            Guid generationId,
            DatabaseService database,
            MultiStepOrchestrationService orchestrator,
            StoriesService storiesService,
            ICommandDispatcher dispatcher,
            ICustomLogger logger)
        {
            _theme = theme;
            _generationId = generationId;
            _database = database;
            _orchestrator = orchestrator;
            _storiesService = storiesService;
            _dispatcher = dispatcher;
            _logger = logger;
        }

        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            var runId = _generationId.ToString();
            var threadId = _generationId.GetHashCode();

            try
            {
                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 1: Ottieni tutti gli agenti writer attivi               ║
                // ╚══════════════════════════════════════════════════════════════╝
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
                var generationTasks = new List<(Agent agent, long executionId, Task task)>();

                foreach (var agent in writerAgents)
                {
                    ct.ThrowIfCancellationRequested();

                    var template = _database.GetStepTemplateById(agent.MultiStepTemplateId!.Value);
                    if (template == null)
                    {
                        await LogAndNotifyAsync($"⚠️ Template {agent.MultiStepTemplateId} non trovato per agente {agent.Name}, skip", "warning");
                        continue;
                    }

                    await LogAndNotifyAsync($"🚀 Avvio generazione con agente: {agent.Name}");

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
                await LogAndNotifyAsync($"⏳ Attendendo il completamento di {generationTasks.Count} generazioni...");

                await Task.WhenAll(generationTasks.Select(t => t.task));

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 4: Raccogli storie generate con successo                ║
                // ╚══════════════════════════════════════════════════════════════╝
                var successfulStories = new List<(Agent agent, long storyId, TaskExecution execution)>();

                foreach (var (agent, executionId, _) in generationTasks)
                {
                    var execution = _database.GetTaskExecutionById(executionId);
                    if (execution == null)
                    {
                        await LogAndNotifyAsync($"⚠️ Execution {executionId} non trovata per {agent.Name}", "warning");
                        continue;
                    }

                    if (execution.Status == "completed" && execution.EntityId.HasValue)
                    {
                        successfulStories.Add((agent, execution.EntityId.Value, execution));
                        await LogAndNotifyAsync($"✅ {agent.Name}: storia {execution.EntityId.Value} generata con successo", "success");
                    }
                    else
                    {
                        await LogAndNotifyAsync($"❌ {agent.Name}: generazione fallita (status: {execution.Status})", "error");
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
                    await RunFullPipelineOnStoryAsync(storyId, agent.Name, ct);
                    return;
                }

                await LogAndNotifyAsync($"🔍 Valutazione con {evaluatorAgents.Count} agenti evaluator...");

                var storyScores = new Dictionary<long, List<(int evaluatorId, double score)>>();

                foreach (var (agent, storyId, _) in successfulStories)
                {
                    storyScores[storyId] = new List<(int, double)>();

                    foreach (var evaluator in evaluatorAgents)
                    {
                        ct.ThrowIfCancellationRequested();

                        await LogAndNotifyAsync($"📊 Valutazione storia {storyId} con {evaluator.Name}...");

                        try
                        {
                            var (success, score, error) = await _storiesService.EvaluateStoryWithAgentAsync(storyId, evaluator.Id);
                            
                            if (success && score > 0)
                            {
                                storyScores[storyId].Add((evaluator.Id, score));
                                await LogAndNotifyAsync($"   {evaluator.Name} → punteggio: {score:F1}");
                            }
                            else
                            {
                                await LogAndNotifyAsync($"   ⚠️ {evaluator.Name}: valutazione fallita - {error}", "warning");
                            }
                        }
                        catch (Exception ex)
                        {
                            await LogAndNotifyAsync($"   ❌ {evaluator.Name}: errore - {ex.Message}", "error");
                        }
                    }
                }

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 6: Seleziona la storia con il punteggio più alto        ║
                // ╚══════════════════════════════════════════════════════════════╝
                long bestStoryId = 0;
                double bestAvgScore = -1;
                string bestWriterName = "";

                foreach (var (agent, storyId, _) in successfulStories)
                {
                    var scores = storyScores[storyId];
                    if (scores.Count == 0)
                    {
                        await LogAndNotifyAsync($"⚠️ Storia {storyId} ({agent.Name}): nessun punteggio valido", "warning");
                        continue;
                    }

                    var avgScore = scores.Average(s => s.score);
                    await LogAndNotifyAsync($"📈 Storia {storyId} ({agent.Name}): punteggio medio = {avgScore:F2}");

                    if (avgScore > bestAvgScore)
                    {
                        bestAvgScore = avgScore;
                        bestStoryId = storyId;
                        bestWriterName = agent.Name;
                    }
                }

                if (bestStoryId == 0)
                {
                    // Fallback: prendi la prima storia se non ci sono punteggi
                    var (agent, storyId, _) = successfulStories[0];
                    bestStoryId = storyId;
                    bestWriterName = agent.Name;
                    await LogAndNotifyAsync($"⚠️ Nessun punteggio valido, selezionata prima storia: {bestStoryId}", "warning");
                }
                else
                {
                    await LogAndNotifyAsync($"🏆 Storia vincitrice: {bestStoryId} di {bestWriterName} con punteggio {bestAvgScore:F2}", "success");
                }

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 7: Esegui il pipeline completo sulla storia vincitrice  ║
                // ╚══════════════════════════════════════════════════════════════╝
                await RunFullPipelineOnStoryAsync(bestStoryId, bestWriterName, ct);

                await LogAndNotifyAsync("🎉 Pipeline completo terminato con successo!", "success");
            }
            catch (OperationCanceledException)
            {
                await LogAndNotifyAsync("⚠️ Pipeline cancellato", "warning");
                _ = _logger.BroadcastTaskComplete(_generationId, "cancelled");
            }
            catch (Exception ex)
            {
                await LogAndNotifyAsync($"❌ Errore pipeline: {ex.Message}", "error");
                _logger.Log("Error", "FullPipeline", $"Pipeline error: {ex.Message}", ex.ToString());
                _ = _logger.BroadcastTaskComplete(_generationId, "failed");
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
                        _logger.Log("Information", "FullPipeline", $"[{agent.Name}] Step {current}/{max}: {stepDesc}");
                    },
                    executionCts.Token
                );
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "FullPipeline", $"Writer {agent.Name} execution error: {ex.Message}");
                // Don't rethrow - we want to continue with other writers
            }
        }

        private async Task RunFullPipelineOnStoryAsync(long storyId, string writerName, CancellationToken ct)
        {
            await LogAndNotifyAsync($"🎯 Esecuzione pipeline audio sulla storia {storyId} ({writerName})...");

            // Get story folder
            var story = _database.GetStoryById(storyId);
            if (story == null)
            {
                await LogAndNotifyAsync($"❌ Storia {storyId} non trovata", "error");
                return;
            }

            // Build folder name for audio files
            var storyFolderName = story.Folder;
            if (string.IsNullOrEmpty(storyFolderName))
            {
                storyFolderName = $"story_{storyId}";
            }
            
            // Ensure the folder exists
            var storyFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "stories_folder", storyFolderName);
            Directory.CreateDirectory(storyFolderPath);

            // ═══════════════════════════════════════════════════════════════
            // STEP 1: Genera TTS Schema
            // ═══════════════════════════════════════════════════════════════
            ct.ThrowIfCancellationRequested();
            await LogAndNotifyAsync("📄 Generazione TTS schema...");
            var (ttsSchemaOk, ttsSchemaMsg) = await _storiesService.GenerateTtsSchemaJsonAsync(storyId);
            if (!ttsSchemaOk)
            {
                await LogAndNotifyAsync($"❌ TTS Schema fallito: {ttsSchemaMsg}", "error");
                return;
            }
            await LogAndNotifyAsync("✅ TTS Schema generato", "success");

            // ═══════════════════════════════════════════════════════════════
            // STEP 2: Normalizza nomi personaggi
            // ═══════════════════════════════════════════════════════════════
            ct.ThrowIfCancellationRequested();
            await LogAndNotifyAsync("👤 Normalizzazione nomi personaggi...");
            var (normCharOk, normCharMsg) = await _storiesService.NormalizeCharacterNamesAsync(storyId);
            if (!normCharOk)
            {
                await LogAndNotifyAsync($"⚠️ Normalizzazione personaggi: {normCharMsg}", "warning");
                // Continue anyway
            }
            else
            {
                await LogAndNotifyAsync("✅ Nomi personaggi normalizzati", "success");
            }

            // ═══════════════════════════════════════════════════════════════
            // STEP 3: Normalizza sentimenti
            // ═══════════════════════════════════════════════════════════════
            ct.ThrowIfCancellationRequested();
            await LogAndNotifyAsync("💭 Normalizzazione sentimenti...");
            var (normSentOk, normSentMsg) = await _storiesService.NormalizeSentimentsAsync(storyId);
            if (!normSentOk)
            {
                await LogAndNotifyAsync($"⚠️ Normalizzazione sentimenti: {normSentMsg}", "warning");
                // Continue anyway
            }
            else
            {
                await LogAndNotifyAsync("✅ Sentimenti normalizzati", "success");
            }

            // ═══════════════════════════════════════════════════════════════
            // STEP 4: Genera TTS audio
            // ═══════════════════════════════════════════════════════════════
            ct.ThrowIfCancellationRequested();
            await LogAndNotifyAsync("🔊 Generazione audio TTS...");
            var ttsResult = await _storiesService.GenerateTtsForStoryAsync(storyId, storyFolderName, _generationId.ToString());
            if (!ttsResult.success)
            {
                await LogAndNotifyAsync($"❌ TTS fallito: {ttsResult.error}", "error");
                return;
            }
            await LogAndNotifyAsync("✅ Audio TTS generato", "success");

            // ═══════════════════════════════════════════════════════════════
            // STEP 5: Genera suoni ambientali
            // ═══════════════════════════════════════════════════════════════
            ct.ThrowIfCancellationRequested();
            await LogAndNotifyAsync("🌲 Generazione suoni ambientali...");
            var ambienceResult = await _storiesService.GenerateAmbienceForStoryAsync(storyId, storyFolderName, _generationId.ToString());
            if (!ambienceResult.success)
            {
                await LogAndNotifyAsync($"⚠️ Ambience: {ambienceResult.error}", "warning");
                // Continue anyway
            }
            else
            {
                await LogAndNotifyAsync("✅ Suoni ambientali generati", "success");
            }

            // ═══════════════════════════════════════════════════════════════
            // STEP 6: Genera effetti sonori (FX)
            // ═══════════════════════════════════════════════════════════════
            ct.ThrowIfCancellationRequested();
            await LogAndNotifyAsync("💥 Generazione effetti sonori (FX)...");
            var fxResult = await _storiesService.GenerateFxForStoryAsync(storyId, storyFolderName, _generationId.ToString());
            if (!fxResult.success)
            {
                await LogAndNotifyAsync($"⚠️ FX: {fxResult.error}", "warning");
                // Continue anyway
            }
            else
            {
                await LogAndNotifyAsync("✅ Effetti sonori generati", "success");
            }

            // ═══════════════════════════════════════════════════════════════
            // STEP 7: Mix audio finale
            // ═══════════════════════════════════════════════════════════════
            ct.ThrowIfCancellationRequested();
            await LogAndNotifyAsync("🎚️ Mixaggio audio finale...");
            var mixResult = await _storiesService.MixFinalAudioForStoryAsync(storyId, storyFolderName, _generationId.ToString());
            if (!mixResult.success)
            {
                await LogAndNotifyAsync($"❌ Mix finale fallito: {mixResult.error}", "error");
                return;
            }
            await LogAndNotifyAsync("✅ Mix audio finale completato", "success");

            _ = _logger.BroadcastTaskComplete(_generationId, "completed");
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
    }
}
