using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    private readonly IReadOnlyList<string> _lookupHintTypes;

    private static readonly string[] DefaultLookupHintTypes =
    {
        "THEME_CORE",
        "GENRE",
        "SETTING",
        "CONFLICT",
        "ANTAGONIST",
        "PROTAGONIST",
        "TWIST"
    };

    public GenerateNrePromptSuggestionCommand(
        DatabaseService database,
        ICallCenter callCenter,
        IOptions<NarrativeRuntimeEngineOptions>? options = null,
        ICustomLogger? logger = null,
        string? themeHint = null,
        string? settingHint = null,
        string? genreHint = null,
        string? toneHint = null,
        string? constraintsHint = null,
        IEnumerable<string>? lookupHintTypes = null)
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
        _lookupHintTypes = NormalizeLookupHintTypes(lookupHintTypes);
    }

    public bool Batch => true;

    public async Task<CommandResult> ExecuteAsync(CancellationToken ct = default, string? runId = null)
    {
        var effectiveRunId = string.IsNullOrWhiteSpace(runId)
            ? $"nre_suggest_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
            : runId.Trim();

        _logger?.Start(effectiveRunId);
        _logger?.Append(effectiveRunId, "💡 Richiesta proposta NRE accodata (batch).");

        var proposerAgent = ResolvePromptProposerAgent();
        if (proposerAgent == null)
        {
            return new CommandResult(false, $"Nessun agente attivo con ruolo '{_options.PromptSuggestionAgentRole}'.");
        }

        _logger?.Append(effectiveRunId, $"Uso agente prompt proposer: {proposerAgent.Name} (role={proposerAgent.Role})");

        var userInput = BuildUserInput();
        var history = new ChatHistory();
        history.AddUser(userInput);

        var callResult = await _callCenter.CallAgentAsync(
            storyId: 0,
            threadId: 0,
            agent: proposerAgent,
            history: history,
            options: new CallOptions
            {
                Operation = "nre_prompt_suggestion",
                Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.PromptSuggestionTimeoutSeconds)),
                MaxRetries = Math.Max(0, _options.PromptSuggestionMaxRetries),
                UseResponseChecker = false,
                AllowFallback = _options.PromptSuggestionAllowFallback,
                AskFailExplanation = false,
                SystemPromptOverride = BuildSystemPromptOverride()
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (!callResult.Success)
        {
            var msg = $"nre_prompt_proposer failed: {callResult.FailureReason ?? "unknown"}";
            _logger?.Append(effectiveRunId, $"❌ {msg}", "error");
            if (_logger != null) await _logger.MarkCompletedAsync(effectiveRunId, msg);
            return new CommandResult(false, msg);
        }

        if (!TryParseSuggestion(callResult.ResponseText, out var suggestion, out var parseError))
        {
            var msg = $"Risposta nre_prompt_proposer non parseabile: {parseError}";
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

    private Agent? ResolvePromptProposerAgent()
    {
        var targetRole = string.IsNullOrWhiteSpace(_options.PromptSuggestionAgentRole)
            ? "nre_prompt_proposer"
            : _options.PromptSuggestionAgentRole.Trim();

        var agents = _database.ListAgents()
            .Where(a =>
                a.IsActive &&
                !string.IsNullOrWhiteSpace(a.Role) &&
                string.Equals(a.Role.Trim(), targetRole, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (agents.Count == 0)
        {
            return null;
        }

        var modelsById = _database.ListModels()
            .Where(m => m.Id.HasValue && m.Id.Value > 0)
            .ToDictionary(m => m.Id!.Value, m => m);

        var enabledCandidates = agents
            .Where(a =>
                !a.ModelId.HasValue ||
                !modelsById.TryGetValue(a.ModelId.Value, out var model) ||
                model.Enabled)
            .ToList();

        var pool = enabledCandidates.Count > 0 ? enabledCandidates : agents;
        return pool[Random.Shared.Next(pool.Count)];
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
        AppendLookupSuggestions(sb);
        AppendForbiddenThemes(sb);
        sb.AppendLine();
        sb.AppendLine("Se un hint è vuoto, inventa tu.");
        return sb.ToString().Trim();
    }

    private void AppendLookupSuggestions(StringBuilder sb)
    {
        var suggestions = new List<(string Type, string Value)>();
        foreach (var type in _lookupHintTypes)
        {
            var picked = _database.PickRandomGenericLookupValueByTypeWeighted(type);
            if (string.IsNullOrWhiteSpace(picked))
            {
                continue;
            }

            suggestions.Add((type, picked.Trim()));
        }

        if (suggestions.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Suggerimenti variabilita da libreria (vincolanti):");
        sb.AppendLine("Usa obbligatoriamente THEME CORE se presente e integra in modo coerente anche gli altri consigli disponibili.");
        foreach (var (type, value) in suggestions)
        {
            sb.AppendLine($"- {MapLookupTypeLabel(type)}: {value}");
        }
    }

    private void AppendForbiddenThemes(StringBuilder sb)
    {
        var forbiddenThemes = _database
            .ListGenericLookupEntries(type: "forbidden_theme")
            .Where(x => x.IsActive && !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => x.Value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (forbiddenThemes.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Temi vietati (vincolo hard): NON proporre storie basate su questi temi.");
        foreach (var forbidden in forbiddenThemes)
        {
            sb.AppendLine($"- {forbidden}");
        }
    }

    private static IReadOnlyList<string> NormalizeLookupHintTypes(IEnumerable<string>? hintTypes)
    {
        var source = hintTypes ?? DefaultLookupHintTypes;
        var normalized = source
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            normalized.AddRange(DefaultLookupHintTypes);
        }

        return normalized;
    }

    private static string MapLookupTypeLabel(string type)
    {
        return type.ToUpperInvariant() switch
        {
            "THEME_CORE" => "Theme Core",
            "GENRE" => "Genere",
            "SETTING" => "Ambientazione",
            "CONFLICT" => "Conflitto",
            "ANTAGONIST" => "Antagonista",
            "PROTAGONIST" => "Protagonista",
            "TWIST" => "Twist",
            _ => type
        };
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
Se sono presenti suggerimenti da libreria, sono vincolanti: devi usarli nella proposta.
Se e' presente THEME CORE, usalo come asse principale del concept.
Non ignorare i suggerimenti salvo conflitto esplicito con vincoli hard.
Se nell'input sono presenti ""Temi vietati"", trattali come divieto assoluto: non proporli e non usarli come focus principale.

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
            if (TryParseSuggestionBestEffort(raw, out var bestEffort, out var bestEffortError))
            {
                suggestion = bestEffort;
                error = null;
                return true;
            }

            error = $"{ex.Message} | fallback: {bestEffortError}";
            return false;
        }
    }

    private static bool TryParseSuggestionBestEffort(string? raw, out NrePromptSuggestionDto suggestion, out string? error)
    {
        suggestion = new NrePromptSuggestionDto();
        error = null;
        var text = ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "risposta vuota";
            return false;
        }

        var title = ExtractStringFieldLoose(text, "title");
        var theme = ExtractStringFieldLoose(text, "theme");
        var setting = ExtractStringFieldLoose(text, "setting");
        var genre = ExtractStringFieldLoose(text, "genre");
        var tone = ExtractStringFieldLoose(text, "tone");
        var constraints = ExtractStringFieldLoose(text, "constraints");
        var resourceHints = ExtractStringFieldLoose(text, "resource_hints");

        if (string.IsNullOrWhiteSpace(title))
        {
            error = "title non estraibile";
            return false;
        }
        // JSON troncato: se manca theme ma il titolo è disponibile, usa un fallback coerente
        // invece di interrompere l'intero comando.
        if (string.IsNullOrWhiteSpace(theme))
        {
            theme = title;
        }

        suggestion = new NrePromptSuggestionDto
        {
            Title = title.Trim(),
            Theme = theme.Trim(),
            Setting = (setting ?? string.Empty).Trim(),
            Genre = (genre ?? string.Empty).Trim(),
            Tone = (tone ?? string.Empty).Trim(),
            Constraints = (constraints ?? string.Empty).Trim(),
            ResourceHints = (resourceHints ?? string.Empty).Trim()
        };

        return true;
    }

    private static string? ExtractStringFieldLoose(string text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        var marker = $"\"{fieldName}\"";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var colon = text.IndexOf(':', idx + marker.Length);
        if (colon < 0)
        {
            return null;
        }

        var i = colon + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        if (i >= text.Length || text[i] != '"')
        {
            return null;
        }

        i++; // opening quote
        var sb = new StringBuilder();
        var escaped = false;
        for (; i < text.Length; i++)
        {
            var ch = text[i];
            if (escaped)
            {
                sb.Append(ch switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => ch
                });
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '"')
            {
                return sb.ToString();
            }

            sb.Append(ch);
        }

        // JSON troncato: restituisce quanto accumulato.
        return sb.ToString().Trim();
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
