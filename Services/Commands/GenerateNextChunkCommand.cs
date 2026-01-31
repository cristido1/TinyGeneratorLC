using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class GenerateNextChunkCommand
{
    private readonly CommandTuningOptions _tuning;

    public sealed record GenerateChunkOptions(
        bool RequireCliffhanger = true,
        bool IsFinalChunk = false,
        int? TargetWords = null);

    private readonly long _storyId;
    private readonly int _writerAgentId;
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICustomLogger? _logger;
    private readonly TextValidationService _textValidationService;
    private readonly GenerateChunkOptions _options;
    private readonly IServiceScopeFactory? _scopeFactory;

    public GenerateNextChunkCommand(
        long storyId,
        int writerAgentId,
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        TextValidationService textValidationService,
        ICustomLogger? logger = null,
        GenerateChunkOptions? options = null,
        CommandTuningOptions? tuning = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _storyId = storyId;
        _writerAgentId = writerAgentId;
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _logger = logger;
        _options = options ?? new GenerateChunkOptions();
        _tuning = tuning ?? new CommandTuningOptions();
        _scopeFactory = scopeFactory;
        _textValidationService = textValidationService ?? throw new ArgumentNullException(nameof(textValidationService));
    }

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var snap = _database.GetStateDrivenStorySnapshot(_storyId);
        if (snap == null)
        {
            return new CommandResult(false, $"Story {_storyId}: snapshot not found");
        }

        if (!snap.IsActive)
        {
            return new CommandResult(false, $"Story {_storyId}: runtime not active");
        }

        var writer = _database.GetAgentById(_writerAgentId);
        if (writer == null)
        {
            return new CommandResult(false, $"Writer agent {_writerAgentId} not found");
        }

        _database.UpdateStoryById(_storyId, modelId: writer.ModelId, agentId: writer.Id);

        var phase = DecidePhase(snap);
        var pov = DecidePov(snap);

        // Apply base resource consumption per phase (deterministic, no semantic inference)
        var newResources = ApplyBaseConsumption(snap, phase);

        // Apply consequence impacts only in EFFETTO phase
        if (string.Equals(phase, "EFFETTO", StringComparison.OrdinalIgnoreCase))
        {
            ApplyConsequenceImpactsInPlace(snap, newResources);
        }

        var prompt = BuildWriterPrompt(snap, phase, pov, _options);

        string output = string.Empty;
        string? lastValidationError = null;
        var attempts = 0;
        var maxAttempts = Math.Max(1, _tuning.GenerateNextChunk.MaxAttempts);
        var hadCorrections = false;

        while (attempts < maxAttempts)
        {
            attempts++;
            ct.ThrowIfCancellationRequested();

            var attemptPrompt = prompt;
            if (!string.IsNullOrWhiteSpace(lastValidationError) && attempts > 1)
            {
                attemptPrompt += $"\n\n⚠️ CORREZIONE RICHIESTA (tentativo {attempts}/{maxAttempts}):\n{lastValidationError}\n";
            }

            output = await CallWriterAsync(writer, attemptPrompt, ct).ConfigureAwait(false);
            output = ExtractAssistantContent(output);
            output = (output ?? string.Empty).Trim();

            var history = BuildStoryHistorySnapshot();
            var validation = _textValidationService.Validate(output, history);
            if (!validation.IsValid)
            {
                _logger?.MarkLatestModelResponseResult("FAILED", $"Text validation: {validation.Reason}");
                return new CommandResult(false, $"Text validation failed: {validation.Reason}");
            }

            if (_options.RequireCliffhanger)
            {
                if (EndsInTension(output, out var reason))
                {
                    if (hadCorrections)
                    {
                        _logger?.MarkLatestModelResponseResult("FAILED", "Risposta corretta dopo retry");
                    }
                    else
                    {
                        _logger?.MarkLatestModelResponseResult("SUCCESS", null);
                    }
                    lastValidationError = null;
                    break;
                }

                lastValidationError = reason;
                hadCorrections = true;
                _logger?.MarkLatestModelResponseResult("FAILED", lastValidationError);
            }
            else
            {
                // No cliffhanger validation requested (e.g. final chunk).
                if (hadCorrections)
                {
                    _logger?.MarkLatestModelResponseResult("FAILED", "Risposta corretta dopo retry");
                }
                else
                {
                    _logger?.MarkLatestModelResponseResult("SUCCESS", null);
                }
                lastValidationError = null;
                break;
            }
        }

        var failureDelta = 0;
        if (!string.IsNullOrWhiteSpace(lastValidationError))
        {
            // Deterministic validator failed after retries; register one failure.
            failureDelta = 1;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            _logger?.MarkLatestModelResponseResult("FAILED", "Risposta vuota dal writer");
            return new CommandResult(
                false,
                "Writer returned empty output; chunk not persisted. Verify the writer model service (e.g. Ollama/OpenAI endpoint) is running and the selected model is available.");
        }

        var tail = GetTail(output, Math.Max(0, _tuning.GenerateNextChunk.ContextTailChars));

        if (!_database.TryApplyStateDrivenChunk(
                storyId: snap.StoryId,
                expectedChunkIndex: snap.CurrentChunkIndex,
                phase: phase,
                pov: pov,
                chunkText: output,
                failureCountDelta: failureDelta,
                newResourceValues: newResources,
                newLastContextTail: tail,
                out var error))
        {
            return new CommandResult(false, $"Persist failed: {error}");
        }

        var msg = failureDelta == 0
            ? $"Chunk {snap.CurrentChunkIndex + 1} saved (phase={phase}, pov={pov})"
            : $"Chunk {snap.CurrentChunkIndex + 1} saved with validator failure (phase={phase}, pov={pov})";

        return new CommandResult(true, msg);
    }

    private static string DecidePhase(DatabaseService.StateDrivenStorySnapshot snap)
    {
        var allowed = ParseSuccessioneStatiOrDefault(snap.EffectiveTipoPlanningSuccessioneStati);

        // Deterministic overrides (no semantic inference): repeated validator failures or depleted resources force EFFETTO.
        if (snap.FailureCount >= 3 || AnyResourceAtMin(snap))
        {
            return allowed.Contains("EFFETTO", StringComparer.OrdinalIgnoreCase) ? "EFFETTO" : allowed.Last();
        }

        var current = NormalizePhaseToken(snap.CurrentPhase);

        // First chunk: optional per-episode initial phase, otherwise first in grammar.
        if (string.IsNullOrWhiteSpace(current))
        {
            var initial = NormalizePhaseToken(snap.EpisodeInitialPhase);
            if (!string.IsNullOrWhiteSpace(initial) && allowed.Contains(initial, StringComparer.OrdinalIgnoreCase))
            {
                return initial;
            }
            return allowed[0];
        }

        // Next = next in succession (circular).
        var idx = allowed.FindIndex(s => s.Equals(current, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            // If the stored phase was legacy or invalid, restart from grammar.
            return allowed[0];
        }

        return allowed[(idx + 1) % allowed.Count];
    }

    private static bool AnyResourceAtMin(DatabaseService.StateDrivenStorySnapshot snap)
    {
        foreach (var res in snap.ProfileResources)
        {
            if (snap.CurrentResourceValues.TryGetValue(res.Name, out var current))
            {
                if (current <= res.MinValue) return true;
            }
        }
        return false;
    }

    private static List<string> ParseSuccessioneStatiOrDefault(string? csv)
    {
        static bool IsAllowed(string s) =>
            s.Equals("AZIONE", StringComparison.OrdinalIgnoreCase)
            || s.Equals("STASI", StringComparison.OrdinalIgnoreCase)
            || s.Equals("ERRORE", StringComparison.OrdinalIgnoreCase)
            || s.Equals("EFFETTO", StringComparison.OrdinalIgnoreCase);

        var parts = (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePhaseToken)
            .Where(s => !string.IsNullOrWhiteSpace(s) && IsAllowed(s))
            .ToList();

        if (parts.Count == 0)
        {
            return new List<string> { "STASI", "AZIONE", "ERRORE", "EFFETTO" };
        }

        // Keep order and allow repeats, but ensure we have at least one element.
        return parts;
    }

    private static string? NormalizePhaseToken(string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase)) return null;

        var p = phase.Trim();
        // Normalize legacy internal names to the 4-state vocabulary.
        if (p.Equals("Action", StringComparison.OrdinalIgnoreCase)) return "AZIONE";
        if (p.Equals("Stall", StringComparison.OrdinalIgnoreCase)) return "STASI";
        if (p.Equals("Error", StringComparison.OrdinalIgnoreCase)) return "ERRORE";
        if (p.Equals("Consequence", StringComparison.OrdinalIgnoreCase)) return "EFFETTO";

        // Accept already-normalized tokens.
        if (p.Equals("AZIONE", StringComparison.OrdinalIgnoreCase)) return "AZIONE";
        if (p.Equals("STASI", StringComparison.OrdinalIgnoreCase)) return "STASI";
        if (p.Equals("ERRORE", StringComparison.OrdinalIgnoreCase)) return "ERRORE";
        if (p.Equals("EFFETTO", StringComparison.OrdinalIgnoreCase)) return "EFFETTO";

        return p.ToUpperInvariant();
    }

    private static string DecidePov(DatabaseService.StateDrivenStorySnapshot snap)
    {
        var list = ParsePovList(snap.ProfilePovListJson);
        if (list.Count == 0)
        {
            return string.IsNullOrWhiteSpace(snap.CurrentPOV) ? "ThirdPersonLimited" : snap.CurrentPOV!;
        }

        var idx = snap.CurrentChunkIndex % list.Count;
        return list[idx];
    }

    private static List<string> ParsePovList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json);
            return parsed?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static Dictionary<string, int> ApplyBaseConsumption(DatabaseService.StateDrivenStorySnapshot snap, string phase)
    {
        // Deterministic base drain (kept conservative).
        var drain = phase switch
        {
            "STASI" => 1,
            "ERRORE" => 1,
            "EFFETTO" => 2,
            _ => 0
        };

        var next = new Dictionary<string, int>(snap.CurrentResourceValues, StringComparer.OrdinalIgnoreCase);

        foreach (var res in snap.ProfileResources)
        {
            if (!next.TryGetValue(res.Name, out var current))
            {
                current = Math.Min(res.MaxValue, Math.Max(res.MinValue, res.InitialValue));
            }

            var updated = current - drain;
            updated = Math.Min(res.MaxValue, Math.Max(res.MinValue, updated));
            next[res.Name] = updated;
        }

        return next;
    }

    private static void ApplyConsequenceImpactsInPlace(DatabaseService.StateDrivenStorySnapshot snap, Dictionary<string, int> resourceValues)
    {
        if (snap.ConsequenceRules.Count == 0) return;

        var idx = (snap.CurrentChunkIndex + snap.FailureCount) % snap.ConsequenceRules.Count;
        var rule = snap.ConsequenceRules[idx];

        foreach (var impact in rule.Impacts)
        {
            if (!resourceValues.TryGetValue(impact.ResourceName, out var current)) continue;
            var resourceDef = snap.ProfileResources.FirstOrDefault(r => r.Name.Equals(impact.ResourceName, StringComparison.OrdinalIgnoreCase));
            if (resourceDef == null) continue;

            var updated = current + impact.DeltaValue;
            updated = Math.Min(resourceDef.MaxValue, Math.Max(resourceDef.MinValue, updated));
            resourceValues[impact.ResourceName] = updated;
        }
    }

    private static string BuildWriterPrompt(DatabaseService.StateDrivenStorySnapshot snap, string phase, string pov, GenerateChunkOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SCRIVI IL PROSSIMO CHUNK (in italiano).");
        sb.AppendLine();
        sb.AppendLine("VINCOLI NON NEGOZIABILI:");
        sb.AppendLine($"- STATO NARRATIVO (deciso dal Planner/codice): {phase}");
        sb.AppendLine($"- POV (deciso dal codice): {pov}");
        if (options.RequireCliffhanger)
        {
            sb.AppendLine("- Il chunk DEVE terminare con tensione aperta (cliffhanger).\n  Vietato chiudere o concludere la storia.");
        }
        else if (options.IsFinalChunk)
        {
            sb.AppendLine("- QUESTO È L'ULTIMO CHUNK: deve CHIUDERE l'episodio in modo soddisfacente.");
            sb.AppendLine("- Vietato cliffhanger finale: niente '...'? niente domanda aperta come ultima frase.");
        }

        if (options.TargetWords.HasValue && options.TargetWords.Value > 0)
        {
            sb.AppendLine($"- Lunghezza target: circa {options.TargetWords.Value} parole (tolleranza ±20%).");
        }
        sb.AppendLine("- NON aggiungere sezioni meta (es. 'capitolo', 'fine', 'riassunto').");
        sb.AppendLine();
        sb.AppendLine("REGOLA SULLO STATO:");
        sb.AppendLine("- AZIONE: qualcuno fa qualcosa che cambia la situazione. L’azione deve cambiare la situazione in modo visibile o creare nuove conseguenze. Spostarsi o parlare NON basta se non genera un problema, un rischio o una nuova direzione.");
        sb.AppendLine("- STASI: pausa, dialogo, riflessione, attesa.");
        sb.AppendLine("- ERRORE: deve accadere un EVENTO NEGATIVO NUOVO e CONCRETO che peggiora la situazione.");
        sb.AppendLine("- EFFETTO: si vedono le conseguenze dirette di un evento accaduto prima.");
        sb.AppendLine();
        sb.AppendLine("ESEMPI DI STATO AZIONE:");
        sb.AppendLine("- Un personaggio prende una decisione rischiosa e agisce subito.");
        sb.AppendLine("- Qualcuno si sposta, fugge, insegue o cerca qualcosa.");
        sb.AppendLine("- Inizia un conflitto fisico o verbale con conseguenze.");
        sb.AppendLine("- Viene tentato un piano o un’operazione.");
        sb.AppendLine("- Qualcuno entra o esce improvvisamente dalla scena.");
        sb.AppendLine();
        sb.AppendLine("ESEMPI DI STATO STASI:");
        sb.AppendLine("- Personaggi discutono un piano o una scelta difficile.");
        sb.AppendLine("- Osservazione dell’ambiente prima di agire.");
        sb.AppendLine("- Preparazione di strumenti o risorse.");
        sb.AppendLine("- Momento emotivo che precede un’azione.");
        sb.AppendLine("Non valido:");
        sb.AppendLine("- Ripetere atmosfera cupa senza nuove informazioni o preparativi.");
        sb.AppendLine();
        sb.AppendLine("ESEMPI DI STATO EFFETTO:");
        sb.AppendLine("- Ferite, danni o perdite vengono scoperti.");
        sb.AppendLine("- La comunità reagisce a una decisione precedente.");
        sb.AppendLine("- Una nuova difficoltà nasce dalle conseguenze di prima.");
        sb.AppendLine("- Un personaggio cambia atteggiamento dopo l’errore.");
        sb.AppendLine("Non valido:");
        sb.AppendLine("- Introdurre un nuovo evento principale (quello è AZIONE o ERRORE).");
        sb.AppendLine();
        sb.AppendLine("ESEMPI DI STATO ERRORE:");
        sb.AppendLine("- perdita o rottura di una risorsa");
        sb.AppendLine("- minaccia imprevista");
        sb.AppendLine("- piano che fallisce visibilmente");
        sb.AppendLine("- decisione che peggiora la situazione");
        sb.AppendLine("Non valido:");
        sb.AppendLine("- Solo silenzio, tristezza o senso di colpa.");
        sb.AppendLine("- Descrivere fallimento senza conseguenze reali.");
        sb.AppendLine();
        sb.AppendLine("REGOLE DI PROGRESSIONE:");
        sb.AppendLine();
        sb.AppendLine("- Se lo stato è AZIONE o ERRORE:");
        sb.AppendLine("- L’evento deve produrre conseguenze visibili.");
        sb.AppendLine("- Alla fine del chunk la situazione deve essere peggiorata, complicata o resa più incerta.");
        sb.AppendLine("- Muoversi o parlare non basta: deve cambiare la direzione della scena.");
        sb.AppendLine();
        sb.AppendLine("CLIFFHANGER:");
        sb.AppendLine("Il cliffhanger deve derivare direttamente dall’azione appena avvenuta, non da un pensiero o dall’atmosfera.");        
        sb.AppendLine("Deve lasciare in sospeso un evento imminente, una minaccia o una decisione urgente.");
        sb.AppendLine("Non è valido chiudere con silenzio, tristezza o riflessione.");
        sb.AppendLine();
        sb.AppendLine("TEMA/CANONE (input utente):");
        sb.AppendLine(snap.Prompt);

        if (!string.IsNullOrWhiteSpace(snap.LastContext))
        {
            sb.AppendLine();
            sb.AppendLine("CONTESTO RECENTE (coda del chunk precedente):");
            sb.AppendLine(snap.LastContext);
        }

        sb.AppendLine();
        sb.AppendLine("Ora scrivi il prossimo chunk:");
        return sb.ToString();
    }

    private async Task<string> CallWriterAsync(Agent writerAgent, string prompt, CancellationToken ct)
    {
        try
        {
            var orchestrator = _kernelFactory.GetOrchestratorForAgent(writerAgent.Id);
            if (orchestrator == null)
            {
                _logger?.Log("Warning", "StateDriven", $"No orchestrator found for writer {writerAgent.Name}; continuing with direct chat bridge");
            }

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
                new List<Dictionary<string, object>>(),
                ct).ConfigureAwait(false);

            var primary = response ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary;
            }

            // Try fallback models (model_roles) if primary returned empty.
            if (_scopeFactory == null)
            {
                return primary;
            }

            using var scope = _scopeFactory.CreateScope();
            var fallbackService = scope.ServiceProvider.GetService<ModelFallbackService>();
            if (fallbackService == null)
            {
                _logger?.Log("Warning", "StateDriven", "ModelFallbackService not available; cannot fallback.");
                return primary;
            }

            var roleCode = string.IsNullOrWhiteSpace(writerAgent.Role) ? "writer" : writerAgent.Role;
            var (fallbackResult, successfulModelRole) = await fallbackService.ExecuteWithFallbackAsync(
                roleCode,
                writerAgent.ModelId,
                async modelRole =>
                {
                    var modelName = modelRole.Model?.Name;
                    if (string.IsNullOrWhiteSpace(modelName))
                    {
                        throw new InvalidOperationException("Fallback ModelRole has no Model.Name");
                    }

                    var candidateBridge = _kernelFactory.CreateChatBridge(
                        modelName,
                        writerAgent.Temperature,
                        modelRole.TopP ?? writerAgent.TopP,
                        writerAgent.RepeatPenalty,
                        modelRole.TopK ?? writerAgent.TopK,
                        writerAgent.RepeatLastN,
                        writerAgent.NumPredict);

                    var fallbackSystem = !string.IsNullOrWhiteSpace(modelRole.Instructions)
                        ? modelRole.Instructions!
                        : systemMessage;
                    var fallbackMessages = new List<ConversationMessage>
                    {
                        new ConversationMessage { Role = "system", Content = fallbackSystem },
                        new ConversationMessage { Role = "user", Content = prompt }
                    };

                    var fallbackResponse = await candidateBridge.CallModelWithToolsAsync(
                        fallbackMessages,
                        new List<Dictionary<string, object>>(),
                        ct).ConfigureAwait(false);

                    return fallbackResponse ?? string.Empty;
                },
                validateResult: s => !string.IsNullOrWhiteSpace(s));

            if (!string.IsNullOrWhiteSpace(fallbackResult) && successfulModelRole?.Model != null)
            {
                _logger?.Log("Info", "StateDriven", $"Fallback model succeeded: {successfulModelRole.Model.Name} (role={roleCode})");
                return fallbackResult!;
            }

            return primary;
        }
        catch (Exception ex)
        {
            _logger?.Log("Error", "StateDriven", $"Writer call failed: {ex.Message}");
            return string.Empty;
        }
    }

    private static string ExtractAssistantContent(string? response)
    {
        var raw = response ?? string.Empty;
        var trimmed = raw.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return string.Empty;

        // Many backends return a JSON envelope (Ollama chat style / OpenAI style).
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return raw;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            // Ollama chat format: { message: { content: "..." } }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var msg))
            {
                if (msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    return content.GetString() ?? string.Empty;
                if (msg.ValueKind == JsonValueKind.String)
                    return msg.GetString() ?? string.Empty;
            }

            // OpenAI chat format: { choices: [ { message: { content: "..." } } ] }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                var first = choices.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    if (first.TryGetProperty("message", out var choiceMsg) && choiceMsg.ValueKind == JsonValueKind.Object &&
                        choiceMsg.TryGetProperty("content", out var choiceContent) && choiceContent.ValueKind == JsonValueKind.String)
                    {
                        return choiceContent.GetString() ?? string.Empty;
                    }

                    // Streaming-like delta format: { choices: [ { delta: { content: "..." } } ] }
                    if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                        delta.TryGetProperty("content", out var deltaContent) && deltaContent.ValueKind == JsonValueKind.String)
                    {
                        return deltaContent.GetString() ?? string.Empty;
                    }
                }
            }

            // Some backends use { response: "..." } or { content: "..." }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String)
                return resp.GetString() ?? string.Empty;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out var rootContent) && rootContent.ValueKind == JsonValueKind.String)
                return rootContent.GetString() ?? string.Empty;
        }
        catch
        {
            // If it's not valid JSON, keep raw.
        }

        return raw;
    }

    private static bool EndsInTension(string text, out string reason)
    {
        reason = string.Empty;
        var t = (text ?? string.Empty).Trim();
        if (t.Length < 40)
        {
            reason = "Il chunk è troppo corto.";
            return false;
        }

        var lower = t.ToLowerInvariant();
        var forbiddenEndings = new[]
        {
            "fine.", "the end", "e vissero felici", "epilogo", "conclusione"
        };
        if (forbiddenEndings.Any(f => lower.EndsWith(f)))
        {
            reason = "Il chunk sembra una conclusione (vietato).";
            return false;
        }

        if (t.EndsWith("...") || t.EndsWith("…") || t.EndsWith("?") || t.EndsWith("!") || t.EndsWith("—") || t.EndsWith(":") || t.EndsWith("…\"") || t.EndsWith("...\""))
        {
            return true;
        }

        // If it ends with a full stop, assume closed beat.
        if (t.EndsWith("."))
        {
            reason = "Il chunk termina con un punto fermo (serve tensione aperta).";
            return false;
        }

        // Fallback: accept if last line ends with open punctuation.
        var lastLine = t.Split('\n').LastOrDefault()?.Trim() ?? t;
        if (lastLine.EndsWith("...") || lastLine.EndsWith("…") || lastLine.EndsWith("?") || lastLine.EndsWith("!") || lastLine.EndsWith("—") || lastLine.EndsWith(":"))
        {
            return true;
        }

        reason = "Il chunk non termina in tensione aperta (usa ? / ... / … / ! / —).";
        return false;
    }

    private static string GetTail(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (maxChars <= 0) return string.Empty;
        var t = text.Trim();
        return t.Length <= maxChars ? t : t.Substring(t.Length - maxChars);
    }

    private string BuildStoryHistorySnapshot()
    {
        try
        {
            var builder = new StringBuilder();
            var chapters = _database.ListChaptersForStory(_storyId);
            foreach (var chapter in chapters)
            {
                if (string.IsNullOrWhiteSpace(chapter.Content)) continue;
                builder.AppendLine(chapter.Content.Trim());
                builder.AppendLine();
            }
            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

}
