using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class GenerateNextChunkCommand : ICommand
{
    private readonly CommandTuningOptions _tuning;

    public sealed record GenerateChunkOptions(
        bool RequireCliffhanger = true,
        bool IsFinalChunk = false,
        int? TargetWords = null);

    private readonly long _storyId;
    private readonly int _writerAgentId;
    private readonly DatabaseService _database;
    private readonly ICustomLogger? _logger;
    private readonly TextValidationService _textValidationService;
    private readonly IAgentCallService? _modelExecution;
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
        IServiceScopeFactory? scopeFactory = null,
        IAgentCallService? modelExecution = null)
    {
        _storyId = storyId;
        _writerAgentId = writerAgentId;
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _ = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _logger = logger;
        _options = options ?? new GenerateChunkOptions();
        _tuning = tuning ?? new CommandTuningOptions();
        _scopeFactory = scopeFactory;
        _modelExecution = modelExecution;
        _textValidationService = textValidationService ?? throw new ArgumentNullException(nameof(textValidationService));
    }

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        ct.ThrowIfCancellationRequested();

        var effectiveRunId = string.IsNullOrWhiteSpace(runId)
            ? $"generate_next_chunk_{_storyId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : runId.Trim();
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
        var writerResult = await CallWriterWithStandardPatternAsync(writer, prompt, phase, effectiveRunId, ct).ConfigureAwait(false);
        var output = (writerResult.Text ?? string.Empty).Trim();
        var failureDelta = 0;

        // If the only blocker is degenerative punctuation, try an automatic cleanup
        // and continue the pipeline instead of hard-failing the step.
        if (!writerResult.Success && !string.IsNullOrWhiteSpace(output) && IsDegenerativePunctuationError(writerResult.Error))
        {
            var fixedOutput = FixDegenerativePunctuation(output);
            if (!string.Equals(output, fixedOutput, StringComparison.Ordinal))
            {
                _logger?.Append(effectiveRunId, "Applicata autocorrezione punteggiatura degenerativa sul chunk.");
                output = fixedOutput;
            }
        }

        var canPersistWithValidatorFailure = CanPersistWithValidatorFailure(writerResult);

        if (canPersistWithValidatorFailure)
        {
            failureDelta = 1;
            _logger?.MarkLatestModelResponseResult("FAILED", writerResult.Error);
        }
        else if (!writerResult.Success || string.IsNullOrWhiteSpace(output))
        {
            var reason = string.IsNullOrWhiteSpace(writerResult.Error)
                ? "Writer returned empty output; chunk not persisted."
                : writerResult.Error!;
            _logger?.MarkLatestModelResponseResult("FAILED", reason);
            return new CommandResult(false, reason);
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

        var message = failureDelta == 0
            ? $"Chunk {snap.CurrentChunkIndex + 1} saved (phase={phase}, pov={pov})"
            : $"Chunk {snap.CurrentChunkIndex + 1} saved with validator failure (phase={phase}, pov={pov})";
        return new CommandResult(true, message);
    }

    private static bool CanPersistWithValidatorFailure(CommandModelExecutionService.Result writerResult)
    {
        if (writerResult.Success || string.IsNullOrWhiteSpace(writerResult.Text) || string.IsNullOrWhiteSpace(writerResult.Error))
        {
            return false;
        }

        var error = writerResult.Error;
        if (error.StartsWith("Cliffhanger validation", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Keep generation flowing when pacing is slower than expected:
        // this remains a tracked validator failure but no longer hard-blocks chunk persistence.
        if (error.Contains("troppi paragrafi senza azione", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsDegenerativePunctuationError(error))
        {
            return true;
        }

        return false;
    }

    private static bool IsDegenerativePunctuationError(string? error)
        => !string.IsNullOrWhiteSpace(error) &&
           error.Contains("punteggiatura degenerativa", StringComparison.OrdinalIgnoreCase);

    private static bool IsRelaxableForCurrentPhase(string? reason, string phase)
    {
        _ = reason;
        _ = phase;
        return false;
    }

    private static bool RequiresModelBasedValidation(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.Contains("assenza di eventi reali", StringComparison.OrdinalIgnoreCase);
    }

    private static string FixDegenerativePunctuation(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var fixedText = text;
        fixedText = Regex.Replace(fixedText, @"([!?])\1{1,}", "$1");
        fixedText = Regex.Replace(fixedText, @"\.{4,}", "...");
        fixedText = Regex.Replace(fixedText, @"[-—]{3,}", "—");
        fixedText = Regex.Replace(fixedText, @"\s{2,}", " ");
        fixedText = Regex.Replace(fixedText, @"[ \t]+(\r?\n)", "$1");
        return fixedText.Trim();
    }

    private async Task<CommandModelExecutionService.Result> CallWriterWithStandardPatternAsync(
        Agent writerAgent,
        string prompt,
        string phase,
        string runId,
        CancellationToken ct)
    {
        var execution = _modelExecution;
        if (execution == null && _scopeFactory != null)
        {
            using var scope = _scopeFactory.CreateScope();
            execution = scope.ServiceProvider.GetService<IAgentCallService>();
        }

        if (execution == null)
        {
            return new CommandModelExecutionService.Result
            {
                Success = false,
                Error = "CommandModelExecutionService non disponibile"
            };
        }

        var roleCode = string.IsNullOrWhiteSpace(writerAgent.Role) ? "writer" : writerAgent.Role.Trim();
        var commandKey = ResolveCommandKey();
        var storyHistory = BuildStoryHistorySnapshot();
        var agentIdentity = BuildAgentIdentity(writerAgent);

        return await execution.ExecuteAsync(
            new CommandModelExecutionService.Request
            {
                CommandKey = commandKey,
                Agent = writerAgent,
                RoleCode = roleCode,
                Prompt = prompt,
                SystemPrompt = writerAgent.Instructions ?? writerAgent.Prompt ?? "Sei uno scrittore esperto.",
                MaxAttempts = Math.Max(1, _tuning.GenerateNextChunk.MaxAttempts),
                UseResponseChecker = true,
                EnableFallback = true,
                DiagnoseOnFinalFailure = true,
                RunId = runId,
                DeterministicValidator = output =>
                {
                    var checks = new List<IDeterministicCheck>
                    {
                        new CheckEmpty
                        {
                            Options = Options.Create<object>(new Dictionary<string, object>
                            {
                                ["ErrorMessage"] = $"Risposta vuota ({agentIdentity})"
                            })
                        },
                        new CheckTextValidation
                        {
                            Options = Options.Create<object>(new Dictionary<string, object>
                            {
                                ["TextValidationService"] = _textValidationService,
                                ["StoryHistory"] = storyHistory,
                                ["AgentIdentity"] = agentIdentity,
                                ["RunId"] = runId,
                                ["Phase"] = phase
                            })
                        }
                    };

                    if (_logger != null &&
                        checks[1] is CheckTextValidation validationCheck &&
                        validationCheck.Options?.Value is Dictionary<string, object> validationOptions)
                    {
                        validationOptions["Logger"] = _logger;
                    }

                    if (_options.RequireCliffhanger)
                    {
                        checks.Add(new CheckCliffhangerEnding
                        {
                            Options = Options.Create<object>(new Dictionary<string, object>
                            {
                                ["AgentIdentity"] = agentIdentity
                            })
                        });
                    }

                    return CheckRunner.Execute(output, checks.ToArray());
                },
                RetryPromptFactory = BuildWriterRetryPrompt
            },
            ct).ConfigureAwait(false);
    }

    private static string BuildWriterRetryPrompt(string originalPrompt, string reason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ATTENZIONE: il tuo output precedente NON era valido.");
        sb.AppendLine("Motivo: " + reason);
        sb.AppendLine("Rigenera la risposta COMPLETA rispettando tutti i vincoli.");
        sb.AppendLine();
        sb.AppendLine("PROMPT ORIGINALE:");
        sb.AppendLine(originalPrompt.Trim());
        return sb.ToString();
    }

    private static string ResolveCommandKey()
    {
        var scope = LogScope.Current;
        if (!string.IsNullOrWhiteSpace(scope))
        {
            var normalized = CommandOperationNameResolver.Normalize(scope);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return "generate_next_chunk";
    }

    private string BuildAgentIdentity(Agent agent)
    {
        var name = string.IsNullOrWhiteSpace(agent.Name) ? $"id={agent.Id}" : agent.Name.Trim();
        var role = string.IsNullOrWhiteSpace(agent.Role) ? "writer" : agent.Role.Trim();
        var model = !string.IsNullOrWhiteSpace(agent.ModelName)
            ? agent.ModelName!.Trim()
            : (agent.ModelId.HasValue ? (_database.GetModelInfoById(agent.ModelId.Value)?.Name ?? $"modelId={agent.ModelId.Value}") : "model=n/a");
        return $"{name}; role={role}; model={model}";
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
            .OfType<string>()
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

        var isFirstChunk = snap.CurrentChunkIndex <= 0;
        if (isFirstChunk)
        {
            sb.AppendLine();
            sb.AppendLine("APERTURA EPISODIO (OBBLIGATORIA PER QUESTO CHUNK):");
            sb.AppendLine("- Inizia descrivendo chiaramente la situazione iniziale dell'episodio.");
            sb.AppendLine("- Definisci contesto, luogo e condizione iniziale dei personaggi principali.");
            sb.AppendLine("- Fai emergere subito la tensione o il problema di partenza.");
            sb.AppendLine("- Evita di partire in medias res con eventi confusi senza setup minimo.");
        }

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
