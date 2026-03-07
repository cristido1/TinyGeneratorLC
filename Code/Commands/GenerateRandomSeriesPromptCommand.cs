using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Services.Commands;

public sealed class GenerateRandomSeriesPromptCommand : ICommand
{
    public enum PromptStyle
    {
        Standard = 0,
        Crazy = 1,
        SpaceMilitaryItalian = 2
    }

    private readonly DatabaseService _database;
    private readonly ICustomLogger? _logger;
    private readonly ICallCenter? _callCenter;
    private readonly PromptStyle _style;

    public string? GeneratedPrompt { get; private set; }
    public string? SelectedWriterName { get; private set; }
    public string? SelectedWriterRole { get; private set; }
    public string? SelectedModelName { get; private set; }

    public GenerateRandomSeriesPromptCommand(
        DatabaseService database,
        PromptStyle style = PromptStyle.Standard,
        ICustomLogger? logger = null,
        ICallCenter? callCenter = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _style = style;
        _logger = logger;
        _callCenter = callCenter;
    }

    // Backward-compatible overload for call sites still passing IAgentCallService.
    public GenerateRandomSeriesPromptCommand(
        DatabaseService database,
        PromptStyle style,
        ICustomLogger? logger,
        IAgentCallService? modelExecution)
        : this(
            database,
            style,
            logger,
            callCenter: ServiceLocator.Services?.GetService(typeof(ICallCenter)) as ICallCenter)
    {
        _ = modelExecution;
    }

    public async Task<CommandResult> ExecuteAsync(string? runId = null, CancellationToken ct = default)
    {
        var effectiveRunId = string.IsNullOrWhiteSpace(runId)
            ? $"generate_random_series_prompt_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : runId.Trim();
        _logger?.Start(effectiveRunId);

        try
        {
            var writerAgents = _database.ListAgents()
                .Where(IsEligibleWriter)
                .ToList();
            if (writerAgents.Count == 0)
            {
                return new CommandResult(false, "Nessun agente writer attivo disponibile.");
            }

            var writer = writerAgents[Random.Shared.Next(writerAgents.Count)];
            SelectedWriterName = writer.Name;
            SelectedWriterRole = writer.Role;

            var modelName = ResolveModelName(writer);
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return new CommandResult(false, $"L'agente writer '{writer.Name}' non ha un modello valido.");
            }

            SelectedModelName = modelName;
            _logger?.Append(effectiveRunId, $"Writer selezionato: {writer.Name} ({writer.Role}) | model={modelName}");

            var rawText = await GeneratePromptTextAsync(writer, modelName, effectiveRunId, ct).ConfigureAwait(false);
            var normalizedPrompt = NormalizePrompt(rawText);
            if (string.IsNullOrWhiteSpace(normalizedPrompt))
            {
                return new CommandResult(false, "Il writer non ha restituito un prompt valido.");
            }

            GeneratedPrompt = normalizedPrompt;
            _logger?.Append(effectiveRunId, "Prompt casuale generato con successo.");
            return new CommandResult(true, normalizedPrompt);
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(false, "Generazione prompt annullata (timeout o cancel).");
        }
        catch (Exception ex)
        {
            _logger?.Append(effectiveRunId, $"Errore GenerateRandomSeriesPrompt: {ex.Message}", "error");
            return new CommandResult(false, $"Errore GenerateRandomSeriesPrompt: {ex.Message}");
        }
        finally
        {
            _logger?.MarkCompleted(effectiveRunId);
        }
    }

    private async Task<string> GeneratePromptTextAsync(Agent writer, string modelName, string runId, CancellationToken ct)
    {
        var callCenter = _callCenter
            ?? ServiceLocator.Services?.GetService(typeof(ICallCenter)) as ICallCenter;
        if (callCenter != null)
        {
            var history = new ChatHistory();
            history.AddSystem(BuildSystemPrompt(writer, _style));
            history.AddUser(BuildUserPrompt(_style));

            var options = new CallOptions
            {
                Operation = ResolveCommandKey(_style),
                Timeout = TimeSpan.FromSeconds(45),
                MaxRetries = 1,
                UseResponseChecker = false,
                AllowFallback = true,
                AskFailExplanation = true,
                SystemPromptOverride = BuildSystemPrompt(writer, _style)
            };
            options.DeterministicChecks.Add(new CheckEmpty
            {
                Options = Options.Create<object>(new Dictionary<string, object>
                {
                    ["ErrorMessage"] = "Risposta vuota"
                })
            });
            options.DeterministicChecks.Add(new CheckPromptLengthRange
            {
                Options = Options.Create<object>(new Dictionary<string, object>
                {
                    ["MinLength"] = 40,
                    ["MaxLength"] = 1800
                })
            });

            var result = await callCenter.CallAgentAsync(
                storyId: 0,
                threadId: ResolveCommandKey(_style).GetHashCode(StringComparison.Ordinal),
                agent: writer,
                history: history,
                options: options,
                cancellationToken: ct).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
            {
                throw new InvalidOperationException(result.FailureReason ?? "Risposta writer vuota");
            }

            if (!string.IsNullOrWhiteSpace(result.ModelUsed))
            {
                SelectedModelName = result.ModelUsed;
            }

            return result.ResponseText;
        }

        throw new InvalidOperationException("ICallCenter non disponibile: chiamata centralizzata disabilitata.");
    }

    private static bool IsEligibleWriter(Agent agent)
    {
        if (!agent.IsActive || string.IsNullOrWhiteSpace(agent.Role))
        {
            return false;
        }

        return agent.Role.StartsWith("writer_", StringComparison.OrdinalIgnoreCase)
            || agent.Role.Equals("writer", StringComparison.OrdinalIgnoreCase)
            || agent.Role.Equals("story_writer", StringComparison.OrdinalIgnoreCase)
            || agent.Role.Equals("text_writer", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveModelName(Agent agent)
    {
        if (agent.ModelId.HasValue && agent.ModelId.Value > 0)
        {
            var byId = _database.ResolveModelCallNameById(agent.ModelId.Value);
            if (!string.IsNullOrWhiteSpace(byId))
            {
                return byId.Trim();
            }
        }

        if (!string.IsNullOrWhiteSpace(agent.ModelName))
        {
            return _database.ResolveModelCallName(agent.ModelName) ?? agent.ModelName.Trim();
        }

        return null;
    }

    private static string BuildUserPrompt(PromptStyle style)
    {
        if (style == PromptStyle.SpaceMilitaryItalian)
        {
            return "Genera un prompt per una serie militare spaziale violenta, NON storica. " +
                   "Deve coinvolgere umani e alieni in guerra e includere chiaramente unita spaziali italiane operative in prima linea. " +
                   "Includi concept, mondo, conflitto centrale, tono emotivo e temi umani. " +
                   "Output in italiano, un solo prompt pronto da incollare, massimo 140 parole.";
        }

        if (style == PromptStyle.Crazy)
        {
            return "Genera un prompt originale per una nuova serie NON storica, molto strana e fuori schema ma coerente. " +
                   "Richiedi un mondo assurdo, regole inaspettate, conflitto centrale forte, tono emotivo chiaro e temi umani. " +
                   "Niente citazioni storiche. Output in italiano, un solo prompt pronto da incollare, massimo 140 parole.";
        }

        return "Genera un prompt originale per una nuova serie NON storica. " +
               "Includi concept, mondo, conflitto centrale, tono emotivo e temi umani. " +
               "Output in italiano, un solo prompt pronto da incollare, massimo 120 parole.";
    }

    private static string BuildSystemPrompt(Agent writer, PromptStyle style)
    {
        var sb = new StringBuilder();
        sb.Append("Sei un autore creativo di serie TV.");
        if (style == PromptStyle.Crazy)
        {
            sb.Append(" Spingi su idee folli ma comprensibili, evita nonsense puro.");
        }
        else if (style == PromptStyle.SpaceMilitaryItalian)
        {
            sb.Append(" Mantieni tono militare duro, realistico e ad alto impatto emotivo.");
        }
        sb.Append(" Rispondi solo con un prompt finale, senza markdown, senza titoli aggiuntivi.");

        if (!string.IsNullOrWhiteSpace(writer.Prompt))
        {
            sb.AppendLine();
            sb.Append(writer.Prompt.Trim());
        }

        if (!string.IsNullOrWhiteSpace(writer.Instructions))
        {
            sb.AppendLine();
            sb.Append(writer.Instructions.Trim());
        }

        return sb.ToString();
    }

    private static string ResolveCommandKey(PromptStyle style)
    {
        return style switch
        {
            PromptStyle.Crazy => "generate_random_series_prompt_crazy",
            PromptStyle.SpaceMilitaryItalian => "generate_random_series_prompt_space_military_italian",
            _ => "generate_random_series_prompt"
        };
    }

    private static string NormalizePrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text.Trim();
        cleaned = Regex.Replace(cleaned, @"^```[a-zA-Z]*\s*", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s*```$", string.Empty);
        cleaned = cleaned.Trim();

        if (cleaned.StartsWith("\"", StringComparison.Ordinal) && cleaned.EndsWith("\"", StringComparison.Ordinal) && cleaned.Length > 1)
        {
            cleaned = cleaned[1..^1].Trim();
        }

        return cleaned;
    }
}
