using TinyGenerator.Models;
using System.Text.Json;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands
{
    /// <summary>
    /// Comando che esegue la generazione di una storia basata su pianificazione narrativa (Planner Agent).
    /// 
    /// WORKFLOW:
    /// 1. Il planner genera una struttura JSON con 15 beat (Save the Cat)
    /// 2. Per ogni beat, il writer genera il testo seguendo le specifiche del beat
    /// 3. Validazione di ogni beat (lunghezza minima + coerenza)
    /// 4. Retry automatico del singolo beat se fallisce
    /// 5. Concatenazione finale di tutti i beat in story_raw
    /// 6. Prosecuzione con il flow normale (formatter → experts → TTS)
    /// </summary>
    public class PlannedStoryCommand
    {
        private readonly CommandTuningOptions _tuning;

        private readonly int _plannerAgentId;
        private readonly int _writerAgentId;
        private readonly string _theme;
        private readonly string? _seriesName;
        private readonly int? _episodeNumber;
        private readonly Guid _generationId;
        private readonly DatabaseService _database;
        private readonly ILangChainKernelFactory _kernelFactory;
        private readonly MultiStepOrchestrationService _orchestrator;
        private readonly StoriesService _storiesService;
        private readonly ICommandDispatcher _dispatcher;
        private readonly ICustomLogger _logger;

        public PlannedStoryCommand(
            int plannerAgentId,
            int writerAgentId,
            string theme,
            string? seriesName,
            int? episodeNumber,
            Guid generationId,
            DatabaseService database,
            ILangChainKernelFactory kernelFactory,
            MultiStepOrchestrationService orchestrator,
            StoriesService storiesService,
            ICommandDispatcher dispatcher,
            ICustomLogger logger,
            CommandTuningOptions? tuning = null)
        {
            _plannerAgentId = plannerAgentId;
            _writerAgentId = writerAgentId;
            _theme = theme;
            _seriesName = seriesName;
            _episodeNumber = episodeNumber;
            _generationId = generationId;
            _database = database;
            _kernelFactory = kernelFactory;
            _orchestrator = orchestrator;
            _storiesService = storiesService;
            _dispatcher = dispatcher;
            _logger = logger;
            _tuning = tuning ?? new CommandTuningOptions();
        }

        public async Task ExecuteAsync(CancellationToken ct = default)
        {
            var runId = _generationId.ToString();
            var threadId = _generationId.GetHashCode();

            try
            {
                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 1: Valida planner e writer agents                       ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPhaseAsync("Fase 1/5: Validazione agenti...");
                await LogAndNotifyAsync("🎬 Avvio generazione storia pianificata...");

                var plannerAgent = _database.GetAgentById(_plannerAgentId);
                if (plannerAgent == null || !plannerAgent.IsActive)
                {
                    await LogAndNotifyAsync($"❌ Planner agent {_plannerAgentId} non trovato o non attivo", "error");
                    return;
                }

                if (!string.Equals(plannerAgent.Role, "planner", StringComparison.OrdinalIgnoreCase))
                {
                    await LogAndNotifyAsync($"❌ L'agente {plannerAgent.Name} non ha ruolo 'planner'", "error");
                    return;
                }

                var writerAgent = _database.GetAgentById(_writerAgentId);
                if (writerAgent == null || !writerAgent.IsActive)
                {
                    await LogAndNotifyAsync($"❌ Writer agent {_writerAgentId} non trovato o non attivo", "error");
                    return;
                }

                await LogAndNotifyAsync($"📝 Planner: {plannerAgent.Name}");
                await LogAndNotifyAsync($"✍️ Writer: {writerAgent.Name}");

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 2: Genera struttura narrativa con planner               ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPhaseAsync("Fase 2/5: Pianificazione narrativa...");
                await LogAndNotifyAsync("🧠 Generazione struttura narrativa (15 beat)...");

                var structurePrompt = BuildPlannerPrompt(_theme, _seriesName, _episodeNumber);
                var structureJson = await CallPlannerAsync(plannerAgent, structurePrompt, threadId, ct);

                if (string.IsNullOrWhiteSpace(structureJson))
                {
                    await LogAndNotifyAsync("❌ Il planner non ha prodotto una struttura valida", "error");
                    return;
                }

                // Parse and validate structure
                var beatStructure = ParseBeatStructure(structureJson);
                if (beatStructure == null || beatStructure.Count == 0)
                {
                    await LogAndNotifyAsync("❌ Struttura JSON non valida o vuota", "error");
                    return;
                }

                await LogAndNotifyAsync($"✅ Struttura generata: {beatStructure.Count} beat", "success");

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 3: Genera testo per ogni beat                           ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPhaseAsync($"Fase 3/5: Generazione {beatStructure.Count} beat...");
                await LogAndNotifyAsync($"✍️ Inizio scrittura {beatStructure.Count} beat...");

                var generatedBeats = new List<string>();
                int currentBeat = 0;

                foreach (var beat in beatStructure)
                {
                    currentBeat++;
                    ct.ThrowIfCancellationRequested();

                    await BroadcastStepAsync(currentBeat, beatStructure.Count, $"Beat {currentBeat}: {beat.BeatName}");
                    await LogAndNotifyAsync($"📝 Beat {currentBeat}/{beatStructure.Count}: {beat.BeatName}");

                    var beatText = await GenerateBeatWithRetriesAsync(
                        writerAgent,
                        beat,
                        currentBeat,
                        beatStructure.Count,
                        generatedBeats,
                        threadId,
                        ct
                    );

                    if (string.IsNullOrWhiteSpace(beatText))
                    {
                        await LogAndNotifyAsync($"❌ Beat {currentBeat} fallito dopo {_tuning.PlannedStory.BeatMaxRetries} tentativi", "error");
                        return;
                    }

                    generatedBeats.Add(beatText);
                    await LogAndNotifyAsync($"✅ Beat {currentBeat} completato ({beatText.Length} caratteri)", "success");
                }

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 4: Crea storia nel database                             ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPhaseAsync("Fase 4/5: Salvataggio storia...");
                await LogAndNotifyAsync("💾 Creazione storia nel database...");

                var fullStoryText = string.Join("\n\n", generatedBeats);
                var storyId = await CreateStoryInDatabaseAsync(fullStoryText, structureJson, ct);

                if (storyId == 0)
                {
                    await LogAndNotifyAsync("❌ Creazione storia fallita", "error");
                    return;
                }

                await LogAndNotifyAsync($"✅ Storia {storyId} creata con successo", "success");

                // ╔══════════════════════════════════════════════════════════════╗
                // ║ FASE 5: Accodamento comandi post-generazione                 ║
                // ╚══════════════════════════════════════════════════════════════╝
                await BroadcastPhaseAsync("Fase 5/5: Accodamento comandi post-generazione...");
                await LogAndNotifyAsync("📋 Accodamento comandi formatter/experts...");

                // Enqueue TransformStoryRawToTaggedCommand (formatter + experts)
                EnqueueTransformCommand(storyId);

                await LogAndNotifyAsync("🎉 Generazione storia pianificata completata!", "success");
                await _logger.BroadcastTaskComplete(_generationId, "completed");
            }
            catch (OperationCanceledException)
            {
                await LogAndNotifyAsync("⚠️ Generazione cancellata", "warning");
                await _logger.BroadcastTaskComplete(_generationId, "cancelled");
            }
            catch (Exception ex)
            {
                await LogAndNotifyAsync($"❌ Errore: {ex.Message}", "error");
                _logger.Log("Error", "PlannedStory", $"Error: {ex.Message}", ex.ToString());
                await _logger.BroadcastTaskComplete(_generationId, "failed");
            }
        }

        private string BuildPlannerPrompt(string theme, string? seriesName, int? episodeNumber)
        {
            var prompt = $@"Genera una struttura narrativa completa basata su questo tema:

TEMA: {theme}";

            if (!string.IsNullOrWhiteSpace(seriesName))
            {
                prompt += $"\nSERIE: {seriesName}";
                if (episodeNumber.HasValue)
                {
                    prompt += $" - Episodio {episodeNumber.Value}";
                }
            }

            prompt += @"

ISTRUZIONI:
1. Usa la struttura ""Save the Cat"" a 15 beat
2. Ritorna un JSON array con esattamente 15 beat
3. Ogni beat deve contenere:
   - beat_name: nome del beat (es. ""Opening Image"", ""Catalyst"", etc.)
   - summary: cosa deve accadere in questo beat (2-3 frasi)
   - protagonist_goal: obiettivo del protagonista in questo beat
   - conflict: conflitto o ostacolo presente
   - stakes: posta in gioco
   - tension_level: livello di tensione da 1 a 10

FORMATO JSON RICHIESTO:
[
  {
    ""beat_name"": ""Opening Image"",
    ""summary"": ""Presentazione del mondo e del protagonista..."",
    ""protagonist_goal"": ""....."",
    ""conflict"": ""....."",
    ""stakes"": ""....."",
    ""tension_level"": 3
  },
  // ... altri 14 beat
]

Genera SOLO il JSON, senza commenti o testo aggiuntivo.";

            return prompt;
        }

        private async Task<string> CallPlannerAsync(Agent plannerAgent, string prompt, int threadId, CancellationToken ct)
        {
            try
            {
                var orchestrator = _kernelFactory.GetOrchestratorForAgent(plannerAgent.Id);
                if (orchestrator == null)
                {
                    _logger.Log("Warning", "PlannedStory", $"No orchestrator found for planner {plannerAgent.Name}");
                    return string.Empty;
                }

                // Create chat bridge for direct model call
                var bridge = _kernelFactory.CreateChatBridge(
                    plannerAgent.ModelName ?? "qwen2.5:7b-instruct",
                    plannerAgent.Temperature,
                    plannerAgent.TopP,
                    plannerAgent.RepeatPenalty,
                    plannerAgent.TopK,
                    plannerAgent.RepeatLastN,
                    plannerAgent.NumPredict);

                var systemMessage = plannerAgent.Prompt ?? "Sei un planner narrativo esperto.";
                var messages = new List<ConversationMessage>
                {
                    new ConversationMessage { Role = "system", Content = systemMessage },
                    new ConversationMessage { Role = "user", Content = prompt }
                };

                var response = await bridge.CallModelWithToolsAsync(
                    messages,
                    new List<Dictionary<string, object>>(), // No tools needed for planner
                    ct
                );

                return response ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "PlannedStory", $"Planner call failed: {ex.Message}");
                return string.Empty;
            }
        }

        private List<BeatDefinition>? ParseBeatStructure(string json)
        {
            try
            {
                // Remove markdown code fences if present
                json = json.Trim();
                if (json.StartsWith("```json"))
                {
                    json = json.Substring(7);
                }
                if (json.StartsWith("```"))
                {
                    json = json.Substring(3);
                }
                if (json.EndsWith("```"))
                {
                    json = json.Substring(0, json.Length - 3);
                }
                json = json.Trim();

                var beats = JsonSerializer.Deserialize<List<BeatDefinition>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return beats;
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "PlannedStory", $"Failed to parse beat structure: {ex.Message}");
                return null;
            }
        }

        private async Task<string> GenerateBeatWithRetriesAsync(
            Agent writerAgent,
            BeatDefinition beat,
            int beatNumber,
            int totalBeats,
            List<string> previousBeats,
            int threadId,
            CancellationToken ct)
        {
            var maxRetries = Math.Max(1, _tuning.PlannedStory.BeatMaxRetries);
            var minBeatLength = Math.Max(0, _tuning.PlannedStory.MinBeatLengthChars);
            var retryDelayMs = Math.Max(0, _tuning.PlannedStory.RetryDelayMilliseconds);

            string? lastError = null;
            var hadCorrections = false;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (attempt > 1)
                {
                    hadCorrections = true;
                }
                try
                {
                    var beatPrompt = BuildBeatPrompt(beat, beatNumber, totalBeats, previousBeats, lastError, attempt);
                    var beatText = await CallWriterAsync(writerAgent, beatPrompt, threadId, ct);

                    if (string.IsNullOrWhiteSpace(beatText))
                    {
                        lastError = "Il writer ha prodotto un output vuoto.";
                        _logger.MarkLatestModelResponseResult("FAILED", lastError);
                        await LogAndNotifyAsync($"⚠️ Tentativo {attempt}/{maxRetries}: output vuoto", "warning");
                        continue;
                    }

                    // VALIDAZIONE 1: Lunghezza minima
                    if (beatText.Length < minBeatLength)
                    {
                        lastError = $"Il testo è troppo corto ({beatText.Length} caratteri). Devi scrivere ALMENO {minBeatLength} caratteri per sviluppare adeguatamente il beat.";
                        _logger.MarkLatestModelResponseResult("FAILED", lastError);
                        await LogAndNotifyAsync($"⚠️ Tentativo {attempt}/{maxRetries}: testo troppo corto ({beatText.Length} < {minBeatLength})", "warning");
                        continue;
                    }

                    // VALIDAZIONE 2: Verifica che non ci siano anticipazioni (keyword check semplice)
                    if (ContainsAnticipations(beatText, beatNumber, totalBeats))
                    {
                        lastError = "Il testo contiene anticipazioni di eventi futuri. Scrivi SOLO il contenuto del beat corrente, non anticipare beat successivi.";
                        _logger.MarkLatestModelResponseResult("FAILED", lastError);
                        await LogAndNotifyAsync($"⚠️ Tentativo {attempt}/{maxRetries}: rilevate anticipazioni", "warning");
                        continue;
                    }

                    // Beat valido
                    _logger.MarkLatestModelResponseResult(
                        hadCorrections ? "FAILED" : "SUCCESS",
                        hadCorrections ? "Risposta corretta dopo retry" : null);
                    return beatText;
                }
                catch (Exception ex)
                {
                    lastError = $"Errore tecnico: {ex.Message}";
                    _logger.MarkLatestModelResponseResult("FAILED", lastError);
                    _logger.Log("Error", "PlannedStory", $"Beat generation error (attempt {attempt}): {ex.Message}");
                }

                if (attempt < maxRetries)
                {
                    await Task.Delay(retryDelayMs, ct);
                }
            }

            // Tutti i tentativi falliti
            await LogAndNotifyAsync($"❌ Beat {beatNumber} fallito dopo {maxRetries} tentativi: {lastError}", "error");
            return string.Empty;
        }

        private string BuildBeatPrompt(
            BeatDefinition beat,
            int beatNumber,
            int totalBeats,
            List<string> previousBeats,
            string? errorFeedback,
            int attemptNumber)
        {
            var prompt = $@"Scrivi il beat {beatNumber} di {totalBeats} seguendo esattamente queste specifiche:

BEAT: {beat.BeatName}
DESCRIZIONE: {beat.Summary}
OBIETTIVO PROTAGONISTA: {beat.ProtagonistGoal}
CONFLITTO: {beat.Conflict}
POSTA IN GIOCO: {beat.Stakes}
TENSIONE: {beat.TensionLevel}/10

REGOLE FONDAMENTALI:
1. Scrivi SOLO il contenuto di questo beat, NON anticipare eventi futuri
2. NON risolvere conflitti che non sono previsti in questo beat
3. Mantieni il livello di tensione richiesto ({beat.TensionLevel}/10)
4. Scrivi ALMENO {_tuning.PlannedStory.MinBeatLengthChars} caratteri per sviluppare adeguatamente la scena
5. Mantieni coerenza con i beat precedenti";

            if (previousBeats.Count > 0)
            {
                prompt += $"\n\nCONTESTO DAI BEAT PRECEDENTI:\n{string.Join("\n---\n", previousBeats.TakeLast(2))}";
            }

            if (!string.IsNullOrWhiteSpace(errorFeedback) && attemptNumber > 1)
            {
                prompt += $"\n\n⚠️ CORREZIONE RICHIESTA (tentativo {attemptNumber}/{_tuning.PlannedStory.BeatMaxRetries}):\n{errorFeedback}";
            }

            prompt += "\n\nScrivi il testo del beat:";

            return prompt;
        }

        private async Task<string> CallWriterAsync(Agent writerAgent, string prompt, int threadId, CancellationToken ct)
        {
            try
            {
                var orchestrator = _kernelFactory.GetOrchestratorForAgent(writerAgent.Id);
                if (orchestrator == null)
                {
                    _logger.Log("Warning", "PlannedStory", $"No orchestrator found for writer {writerAgent.Name}");
                    return string.Empty;
                }

                // Create chat bridge for direct model call
                var bridge = _kernelFactory.CreateChatBridge(
                    writerAgent.ModelName ?? "qwen2.5:7b-instruct",
                    writerAgent.Temperature,
                    writerAgent.TopP,
                    writerAgent.RepeatPenalty,
                    writerAgent.TopK,
                    writerAgent.RepeatLastN,
                    writerAgent.NumPredict);

                var systemMessage = writerAgent.Instructions ?? writerAgent.Prompt ?? "Sei uno scrittore esperto.";
                var messages = new List<ConversationMessage>
                {
                    new ConversationMessage { Role = "system", Content = systemMessage },
                    new ConversationMessage { Role = "user", Content = prompt }
                };

                var response = await bridge.CallModelWithToolsAsync(
                    messages,
                    new List<Dictionary<string, object>>(), // No tools needed for beat writing
                    ct
                );

                return response ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "PlannedStory", $"Writer call failed: {ex.Message}");
                return string.Empty;
            }
        }

        private bool ContainsAnticipations(string text, int currentBeat, int totalBeats)
        {
            // Semplice euristica: cerca keyword che indicano anticipazioni
            var anticipationKeywords = new[]
            {
                "alla fine", "finalmente", "dopo tutto", "infine",
                "nel finale", "nell'epilogo", "conclusione",
                "tutto si risolve", "happy ending", "vittoria finale"
            };

            // Cerca anticipazioni solo se siamo nei primi 2/3 della storia
            if (currentBeat > (totalBeats * 2 / 3))
            {
                return false; // Negli ultimi beat le anticipazioni sono accettabili
            }

            var lowerText = text.ToLowerInvariant();
            return anticipationKeywords.Any(keyword => lowerText.Contains(keyword));
        }

        private async Task<long> CreateStoryInDatabaseAsync(string fullText, string structureJson, CancellationToken ct)
        {
            try
            {
                // Extract title from theme or generate one
                var title = ExtractTitleFromTheme(_theme);

                // Get writer agent for reference
                var writerAgent = _database.GetAgentById(_writerAgentId);

                // Determine serie_id if seriesName is provided
                int? serieId = null;
                if (!string.IsNullOrWhiteSpace(_seriesName))
                {
                    // TODO: Look up serie_id from series table if it exists
                    // For now, leave as null
                }

                var storyId = _database.InsertSingleStory(
                    prompt: _theme,
                    story: fullText,
                    modelId: writerAgent?.ModelId,
                    agentId: _writerAgentId,
                    title: title,
                    serieId: serieId,
                    serieEpisode: _episodeNumber
                );

                // Update story_structure separately since InsertSingleStory doesn't support it yet
                if (storyId > 0 && !string.IsNullOrWhiteSpace(structureJson))
                {
                    // Use Dapper to update story_structure field
                    await Task.Run(() => _database.UpdateStoryStructure(storyId, structureJson));
                }

                return storyId;
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "PlannedStory", $"Failed to create story: {ex.Message}");
                return 0;
            }
        }

        private string ExtractTitleFromTheme(string theme)
        {
            // Simple title extraction: take first 50 chars or first sentence
            if (string.IsNullOrWhiteSpace(theme))
                return "Untitled Story";

            var firstSentence = theme.Split(new[] { '.', '!', '?' }, 2)[0].Trim();
            if (firstSentence.Length > 50)
                return firstSentence.Substring(0, 50) + "...";
            return firstSentence;
        }

        private void EnqueueTransformCommand(long storyId)
        {
            try
            {
                var transformRunId = $"{_generationId}_transform";
                _dispatcher.Enqueue(
                    "TransformStoryRawToTagged",
                    async ctx =>
                    {
                        var cmd = new TransformStoryRawToTaggedCommand(
                            storyId,
                            _database,
                            _kernelFactory,
                            _storiesService,
                            _logger,
                            _dispatcher,
                            _tuning
                        );
                        await cmd.ExecuteAsync(ctx.CancellationToken, transformRunId);
                        return new CommandResult(true, "Transform command completed");
                    },
                    runId: transformRunId,
                    priority: 2
                );

                _logger.Log("Information", "PlannedStory", $"Enqueued TransformStoryRawToTaggedCommand for story {storyId}");
            }
            catch (Exception ex)
            {
                _logger.Log("Error", "PlannedStory", $"Failed to enqueue transform command: {ex.Message}");
            }
        }

        private async Task LogAndNotifyAsync(string message, string extraClass = "")
        {
            var runId = _generationId.ToString();
            _logger.Append(runId, message);
            _logger.Log("Information", "PlannedStory", message);

            try
            {
                await _logger.NotifyGroupAsync(runId, "ProgressAppended", message, extraClass);
            }
            catch
            {
                // Ignore notification errors
            }
        }

        private async Task BroadcastPhaseAsync(string phaseDescription)
        {
            try
            {
                await _logger.BroadcastStepProgress(_generationId, 0, 5, phaseDescription);
                _dispatcher?.UpdateStep(_generationId.ToString(), 0, 5, phaseDescription);
            }
            catch
            {
                // Ignore broadcast errors
            }
        }

        private async Task BroadcastStepAsync(int currentStep, int totalSteps, string stepDescription)
        {
            try
            {
                await _logger.BroadcastStepProgress(_generationId, currentStep, totalSteps, stepDescription);
                _dispatcher?.UpdateStep(_generationId.ToString(), currentStep, totalSteps, stepDescription);
            }
            catch
            {
                // Ignore broadcast errors
            }
        }

        // DTO per beat definition
        private class BeatDefinition
        {
            public string BeatName { get; set; } = string.Empty;
            public string Summary { get; set; } = string.Empty;
            public string ProtagonistGoal { get; set; } = string.Empty;
            public string Conflict { get; set; } = string.Empty;
            public string Stakes { get; set; } = string.Empty;
            public int TensionLevel { get; set; }
        }
    }
}