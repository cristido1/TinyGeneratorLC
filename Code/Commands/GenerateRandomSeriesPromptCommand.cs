using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly IAgentCallService? _modelExecution;
    private readonly PromptStyle _style;

    public string? GeneratedPrompt { get; private set; }
    public string? SelectedWriterName { get; private set; }
    public string? SelectedWriterRole { get; private set; }
    public string? SelectedModelName { get; private set; }

    public GenerateRandomSeriesPromptCommand(
        DatabaseService database,
        PromptStyle style = PromptStyle.Standard,
        ICustomLogger? logger = null,
        IAgentCallService? modelExecution = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _style = style;
        _logger = logger;
        _modelExecution = modelExecution;
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
        var execution = _modelExecution ?? ServiceLocator.Services?.GetService(typeof(IAgentCallService)) as IAgentCallService;
        if (execution != null)
        {
            var request = new CommandModelExecutionService.Request
            {
                CommandKey = ResolveCommandKey(_style),
                Agent = writer,
                RoleCode = string.IsNullOrWhiteSpace(writer.Role) ? "writer" : writer.Role,
                Prompt = BuildUserPrompt(_style),
                SystemPrompt = BuildSystemPrompt(writer, _style),
                MaxAttempts = 2,
                RetryDelaySeconds = 1,
                StepTimeoutSec = 45,
                UseResponseChecker = false,
                EnableFallback = true,
                DiagnoseOnFinalFailure = true,
                ExplainAfterAttempt = 2,
                RunId = runId,
                EnableDeterministicValidation = true,
                DeterministicValidator = ValidatePromptOutput,
                RetryPromptFactory = (_, reason) =>
                    $"Correggi la risposta precedente. Problema: {reason}. Restituisci solo il prompt finale in italiano, max 120 parole."
            };

            var result = await execution.ExecuteAsync(request, ct).ConfigureAwait(false);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Text))
            {
                throw new InvalidOperationException(result.Error ?? "Risposta writer vuota");
            }

            if (!string.IsNullOrWhiteSpace(result.ModelName))
            {
                SelectedModelName = result.ModelName;
            }

            return result.Text;
        }

        throw new InvalidOperationException("IAgentCallService non disponibile: chiamata diretta al modello disabilitata.");
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
        if (!string.IsNullOrWhiteSpace(agent.ModelName))
        {
            return agent.ModelName;
        }

        if (!agent.ModelId.HasValue)
        {
            return null;
        }

        return _database.GetModelInfoById(agent.ModelId.Value)?.Name;
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

    private static CommandModelExecutionService.DeterministicValidationResult ValidatePromptOutput(string output)
    {
        var normalized = NormalizePrompt(output);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new CommandModelExecutionService.DeterministicValidationResult(false, "Risposta vuota");
        }

        if (normalized.Length < 40)
        {
            return new CommandModelExecutionService.DeterministicValidationResult(false, "Prompt troppo corto");
        }

        if (normalized.Length > 1800)
        {
            return new CommandModelExecutionService.DeterministicValidationResult(false, "Prompt troppo lungo");
        }

        return new CommandModelExecutionService.DeterministicValidationResult(true, null);
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
