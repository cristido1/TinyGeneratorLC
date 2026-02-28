using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

public sealed class GenerateNrePromptSuggestionCommand : ICommand
{
    private readonly DatabaseService _database;
    private readonly ICallCenter _callCenter;
    private readonly ICustomLogger? _logger;
    private readonly NarrativeRuntimeEngineOptions _options;
    private readonly string? _themeHint;
    private readonly string? _settingHint;
    private readonly string? _genreHint;
    private readonly string? _toneHint;
    private readonly string? _constraintsHint;

    public GenerateNrePromptSuggestionCommand(
        DatabaseService database,
        ICallCenter callCenter,
        IOptions<NarrativeRuntimeEngineOptions>? options = null,
        ICustomLogger? logger = null,
        string? themeHint = null,
        string? settingHint = null,
        string? genreHint = null,
        string? toneHint = null,
        string? constraintsHint = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _callCenter = callCenter ?? throw new ArgumentNullException(nameof(callCenter));
        _logger = logger;
        _options = options?.Value ?? new NarrativeRuntimeEngineOptions();
        _themeHint = themeHint;
        _settingHint = settingHint;
        _genreHint = genreHint;
        _toneHint = toneHint;
        _constraintsHint = constraintsHint;
    }

    public bool Batch => true;

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        var effectiveRunId = string.IsNullOrWhiteSpace(runId)
            ? $"nre_suggest_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : runId.Trim();

        _logger?.Start(effectiveRunId);
        _logger?.Append(effectiveRunId, "💡 Richiesta proposta NRE accodata (batch).");

        var writerAgent = ResolveWriterAgent();
        if (writerAgent == null)
        {
            return new CommandResult(false, $"Agente NRE writer non trovato: '{_options.WriterAgentName}'.");
        }

        _logger?.Append(effectiveRunId, $"Uso agente: {writerAgent.Name}");

        var userInput = BuildUserInput();
        var history = new ChatHistory();
        history.AddUser(userInput);

        var callResult = await _callCenter.CallAgentAsync(
            storyId: 0,
            threadId: 0,
            agent: writerAgent,
            history: history,
            options: new CallOptions
            {
                Operation = "nre_prompt_suggestion",
                Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.WriterCallTimeoutSeconds)),
                MaxRetries = Math.Max(0, _options.CallCenterMaxRetries),
                UseResponseChecker = false,
                AllowFallback = _options.AllowFallback,
                AskFailExplanation = false,
                SystemPromptOverride = BuildSystemPromptOverride()
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (!callResult.Success)
        {
            var msg = $"nre_writer failed: {callResult.FailureReason ?? "unknown"}";
            _logger?.Append(effectiveRunId, $"❌ {msg}", "error");
            if (_logger != null) await _logger.MarkCompletedAsync(effectiveRunId, msg);
            return new CommandResult(false, msg);
        }

        if (!TryParseSuggestion(callResult.ResponseText, out var suggestion, out var parseError))
        {
            var msg = $"Risposta nre_writer non parseabile: {parseError}";
            _logger?.Append(effectiveRunId, $"❌ {msg}", "error");
            _logger?.Append(effectiveRunId, callResult.ResponseText ?? string.Empty, "warning");
            if (_logger != null) await _logger.MarkCompletedAsync(effectiveRunId, msg);
            return new CommandResult(false, msg);
        }

        suggestion.Prompt = BuildCompositePrompt(
            suggestion.Theme,
            suggestion.Setting,
            suggestion.Genre,
            suggestion.Tone,
            suggestion.Constraints,
            suggestion.ResourceHints);

        var json = JsonSerializer.Serialize(suggestion, new JsonSerializerOptions { WriteIndented = false });
        _logger?.Append(effectiveRunId, $"✅ Proposta NRE pronta: {suggestion.Title}", "success");
        if (_logger != null) await _logger.MarkCompletedAsync(effectiveRunId, json);
        return new CommandResult(true, json);
    }

    private Agent? ResolveWriterAgent()
    {
        var agents = _database.ListAgents().Where(a => a.IsActive).ToList();
        var preferred = _options.WriterAgentName;

        return agents.FirstOrDefault(a => string.Equals(a.Name, preferred, StringComparison.OrdinalIgnoreCase))
               ?? agents.FirstOrDefault(a => string.Equals(a.Role, preferred, StringComparison.OrdinalIgnoreCase))
               ?? agents.FirstOrDefault(a =>
                   a.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase) ||
                   a.Role.Contains(preferred, StringComparison.OrdinalIgnoreCase));
    }

    private string BuildUserInput()
    {
        var varietySeed = Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.AppendLine("Genera una proposta originale per avvio storia.");
        sb.AppendLine("Compila titolo e campi prompt strutturati.");
        sb.AppendLine($"Variety seed: {varietySeed}");
        sb.AppendLine();
        sb.AppendLine("Hint opzionali (se presenti):");
        sb.AppendLine($"Tema: {NormalizeHint(_themeHint)}");
        sb.AppendLine($"Ambientazione: {NormalizeHint(_settingHint)}");
        sb.AppendLine($"Genere: {NormalizeHint(_genreHint)}");
        sb.AppendLine($"Tono desiderato: {NormalizeHint(_toneHint)}");
        sb.AppendLine($"Vincoli: {NormalizeHint(_constraintsHint)}");
        sb.AppendLine();
        sb.AppendLine("Se un hint è vuoto, inventa tu.");
        return sb.ToString().Trim();
    }

    private static string BuildSystemPromptOverride()
    {
        return
@"Sei un ideatore di concept narrativi.

Genera una proposta per avvio storia con campi strutturati.
Non scrivere racconto completo.
Non aggiungere spiegazioni.
Non aggiungere testo fuori JSON.
Nessun filtro di genere: qualunque genere è valido (commedia, comico, romantico, slice of life, giallo, fantasy, fantascienza, horror, storico, satira, ecc.).
Evita bias fisso su militare/guerra: usa militare solo se richiesto dagli hint.
La proposta deve essere originale ma plausibile e concreta.
Preferisci situazioni realistiche e credibili nel proprio genere.
Evita elementi gratuiti, casuali o assurdi non giustificati dal tema.
Se il genere è comico/fantasy/fantascienza/horror, mantieni comunque coerenza interna e motivazioni chiare.
Compila SEMPRE anche le risorse iniziali coerenti con l'idea.

Rispondi SOLO in JSON valido con questa struttura:
{
  ""title"": ""string"",
  ""theme"": ""string"",
  ""setting"": ""string"",
  ""genre"": ""string"",
  ""tone"": ""string"",
  ""constraints"": ""string"",
  ""resource_hints"": ""string""
}";
    }

    private static bool TryParseSuggestion(string? raw, out NrePromptSuggestionDto suggestion, out string? error)
    {
        suggestion = new NrePromptSuggestionDto();
        error = null;
        try
        {
            var json = ExtractJson(raw);
            var parsed = JsonSerializer.Deserialize<NrePromptSuggestionDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (parsed == null)
            {
                error = "json nullo";
                return false;
            }

            parsed.Title = (parsed.Title ?? string.Empty).Trim();
            parsed.Theme = (parsed.Theme ?? string.Empty).Trim();
            parsed.Setting = (parsed.Setting ?? string.Empty).Trim();
            parsed.Genre = (parsed.Genre ?? string.Empty).Trim();
            parsed.Tone = (parsed.Tone ?? string.Empty).Trim();
            parsed.Constraints = (parsed.Constraints ?? string.Empty).Trim();
            parsed.ResourceHints = (parsed.ResourceHints ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(parsed.Title))
            {
                error = "title vuoto";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parsed.Theme))
            {
                error = "theme vuoto";
                return false;
            }

            suggestion = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ExtractJson(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = text.IndexOf('\n');
            if (firstNl >= 0)
            {
                text = text[(firstNl + 1)..];
            }
            if (text.EndsWith("```", StringComparison.Ordinal))
            {
                text = text[..^3].Trim();
            }
        }
        return text;
    }

    private static string NormalizeHint(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(vuoto)" : value.Trim();
    }

    private static string BuildCompositePrompt(string? theme, string? setting, string? genre, string? tone, string? constraints, string? resourceHints)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(theme)) parts.Add($"Tema:{Environment.NewLine}{theme!.Trim()}");
        if (!string.IsNullOrWhiteSpace(setting)) parts.Add($"Ambientazione:{Environment.NewLine}{setting!.Trim()}");
        if (!string.IsNullOrWhiteSpace(genre)) parts.Add($"Genere:{Environment.NewLine}{genre!.Trim()}");
        if (!string.IsNullOrWhiteSpace(tone)) parts.Add($"Tono desiderato:{Environment.NewLine}{tone!.Trim()}");
        if (!string.IsNullOrWhiteSpace(constraints)) parts.Add($"Vincoli:{Environment.NewLine}{constraints!.Trim()}");
        if (!string.IsNullOrWhiteSpace(resourceHints)) parts.Add($"Risorse iniziali opzionali:{Environment.NewLine}{resourceHints!.Trim()}");
        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    public sealed class NrePromptSuggestionDto
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("theme")]
        public string? Theme { get; set; }

        [JsonPropertyName("setting")]
        public string? Setting { get; set; }

        [JsonPropertyName("genre")]
        public string? Genre { get; set; }

        [JsonPropertyName("tone")]
        public string? Tone { get; set; }

        [JsonPropertyName("constraints")]
        public string? Constraints { get; set; }

        [JsonPropertyName("resource_hints")]
        public string? ResourceHints { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }
    }
}
