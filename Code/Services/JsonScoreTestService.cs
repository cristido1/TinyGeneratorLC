using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

/// <summary>
/// Esegue un benchmark rapido sulla capacitÃƒÂ  dei modelli di rispettare uno schema JSON
/// tramite l'opzione response_format con json_schema. Aggiorna il campo JsonScore nella
/// tabella models (0-10).
/// </summary>
public sealed class JsonScoreTestService
{
    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICommandDispatcher _dispatcher;
    private readonly ICustomLogger? _logger;

    private const string SystemPrompt = """
Sei un validatore di TODO. Devi rispondere SOLO con un oggetto JSON che rispetta
lo schema richiesto: { "titolo": string, "priorita": "bassa"|"media"|"alta", "completato": boolean }.
Non aggiungere testo fuori dal JSON, niente spiegazioni, niente codice Markdown.
Rispetta esattamente la priorita richiesta dal prompt utente e i vincoli sul titolo.
""";

    private static readonly object ResponseFormatSchemaOpenAi = new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "task",
            schema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["titolo"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["priorita"] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "bassa", "media", "alta" }
                    },
                    ["completato"] = new Dictionary<string, object> { ["type"] = "boolean" }
                },
                required = new[] { "titolo", "priorita", "completato" }
            },
            strict = true
        }
    };

    private static readonly object ResponseFormatSchemaOllama = new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["titolo"] = new Dictionary<string, object> { ["type"] = "string" },
            ["priorita"] = new Dictionary<string, object>
            {
                ["type"] = "string",
                ["enum"] = new[] { "bassa", "media", "alta" }
            },
            ["completato"] = new Dictionary<string, object> { ["type"] = "boolean" }
        },
        required = new[] { "titolo", "priorita", "completato" }
    };

    private sealed record JsonTestCase(
        string Prompt,
        string ExpectedPriorita,
        bool ExpectedCompletato,
        Func<string, bool>? TitleValidator = null,
        string? ValidationHint = null);

    private readonly IReadOnlyList<JsonTestCase> _tests = new[]
    {
        new JsonTestCase(
            "Crea un'attivitÃƒÂ  semplice. priorita=\"bassa\" e completato=false.",
            "bassa",
            false,
            title => !string.IsNullOrWhiteSpace(title),
            "titolo non deve essere vuoto"),
        new JsonTestCase(
            "Genera un TODO con titolo esatto \"Scrivere i test unitari\", priorita=\"media\", completato=false.",
            "media",
            false,
            title => title.Trim().Equals("Scrivere i test unitari", StringComparison.OrdinalIgnoreCase),
            "titolo deve essere esattamente \"Scrivere i test unitari\""),
        new JsonTestCase(
            "Restituisci un JSON con la parola \"backup\" nel titolo, priorita=\"alta\", completato=false.",
            "alta",
            false,
            title => title.Contains("backup", StringComparison.OrdinalIgnoreCase),
            "titolo deve contenere \"backup\""),
        new JsonTestCase(
            "Task completato true, priorita=\"alta\". Il titolo deve iniziare con \"Verifica\".",
            "alta",
            true,
            title => title.StartsWith("Verifica", StringComparison.OrdinalIgnoreCase),
            "titolo deve iniziare con \"Verifica\""),
        new JsonTestCase(
            "Crea un task priorita=\"media\" non completato con titolo di almeno 4 parole.",
            "media",
            false,
            title => WordCount(title) >= 4,
            "titolo deve avere almeno 4 parole"),
        new JsonTestCase(
            "Fornisci un task priorita=\"bassa\" non completato. Il titolo deve contenere la parola \"JSON\".",
            "bassa",
            false,
            title => title.Contains("json", StringComparison.OrdinalIgnoreCase),
            "titolo deve contenere \"JSON\""),
        new JsonTestCase(
            "Crea un task priorita=\"alta\" non completato con titolo in camelCase (senza spazi).",
            "alta",
            false,
            IsCamelCase,
            "titolo deve essere camelCase senza spazi"),
        new JsonTestCase(
            "Dammi un task priorita=\"media\" completato true con titolo che termina con un punto esclamativo.",
            "media",
            true,
            title => title.TrimEnd().EndsWith("!", StringComparison.Ordinal),
            "titolo deve terminare con '!'"),
        new JsonTestCase(
            "Restituisci un task priorita=\"bassa\" non completato con almeno un numero nel titolo.",
            "bassa",
            false,
            ContainsDigit,
            "titolo deve contenere un numero"),
        new JsonTestCase(
            "Genera il task finale: priorita=\"alta\", completato=false. Se sei incerto usa il titolo \"taskFinale\". Nessun testo fuori dal JSON.",
            "alta",
            false,
            title => !string.IsNullOrWhiteSpace(title),
            "titolo non deve essere vuoto")
    };

    public JsonScoreTestService(
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        ICommandDispatcher dispatcher,
        ICustomLogger? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger;
    }

    public CommandHandle EnqueueJsonScoreForMissingModels()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = "json_score"
        };

        return _dispatcher.Enqueue(
            "json_score",
            RunAsync,
            threadScope: "json_score",
            metadata: metadata,
            priority: 3);
    }

    public CommandHandle EnqueueJsonScoreForModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentNullException(nameof(modelName));

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = "json_score",
            ["modelName"] = modelName.Trim()
        };

        return _dispatcher.Enqueue(
            $"json_score_{Sanitize(modelName)}",
            RunAsync,
            threadScope: $"json_score/{Sanitize(modelName)}",
            metadata: metadata,
            priority: 2);
    }

    private static string Sanitize(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "model" : sanitized;
    }

    public CommandHandle? EnqueueJsonScoreForModel(int modelId)
    {
        if (modelId <= 0) return null;

        var model = _database.ListModels().FirstOrDefault(m => m.Id.HasValue && m.Id.Value == modelId);
        if (model == null || !model.Id.HasValue || !model.Enabled) return null;

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = "json_score",
            // Command panel uses these keys for display
            ["modelName"] = model.Name,
            ["stepCurrent"] = "0",
            ["stepMax"] = _tests.Count.ToString()
        };

        return _dispatcher.Enqueue(
            "json_score",
            ctx => RunSingleModelAsync(ctx, model),
            threadScope: "json_score",
            metadata: metadata,
            priority: 3);
    }

    private async Task<CommandResult> RunAsync(CommandContext ctx)
    {
        List<ModelInfo> targets;
        if (ctx.Metadata != null && ctx.Metadata.TryGetValue("modelName", out var modelNameOverride))
        {
            targets = _database.ListModels()
                .Where(m => m.Enabled && string.Equals(m.Name, modelNameOverride, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (targets.Count == 0)
            {
                return new CommandResult(false, $"Modello '{modelNameOverride}' non trovato o disabilitato");
            }
        }
        else
        {
            targets = _database.ListModelsWithoutJsonScore();
            if (targets.Count == 0)
            {
                return new CommandResult(true, "Nessun modello da testare: jsonScore già presente.");
            }

            targets = OrderModelsForScoreTests(targets).ToList();
        }

        int processed = 0;
        foreach (var model in targets)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (!model.Id.HasValue)
            {
                continue;
            }

            var (score, details) = await TestSingleModelAsync(model, ctx);
            _database.UpdateModelJsonScore(model.Id.Value, score, details);
            processed++;

            var summary = $"[{model.Name}] jsonScore={score}/10";
            _logger?.Log("Info", "JsonScoreTest", summary, details);
        }

        return new CommandResult(true, $"jsonScore calcolato per {processed} modelli");
    }

    private static IEnumerable<ModelInfo> OrderModelsForScoreTests(IEnumerable<ModelInfo> models)
    {
        return models
            .OrderBy(m => string.Equals(m.Provider?.Trim(), "ollama", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
    }
    private async Task<CommandResult> RunSingleModelAsync(CommandContext ctx, ModelInfo model)
    {
        if (!model.Id.HasValue)
        {
            return new CommandResult(false, "Modello non valido (Id mancante)");
        }

        var (score, details) = await TestSingleModelAsync(model, ctx);
        _database.UpdateModelJsonScore(model.Id.Value, score, details);

        var summary = $"[{model.Name}] jsonScore={score}/10";
        _logger?.Log("Info", "JsonScoreTest", summary, details);
        return new CommandResult(true, summary);
    }

    private async Task<(int Score, string Details)> TestSingleModelAsync(ModelInfo model, CommandContext ctx)
    {
        var bridge = _kernelFactory.CreateChatBridge(model.Name, temperature: 0.1, topP: 1.0, useMaxTokens: true);
        var provider = model.Provider?.Trim();
        bridge.ResponseFormat = string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase)
            ? ResponseFormatSchemaOllama
            : ResponseFormatSchemaOpenAi;
        bridge.MaxResponseTokens = bridge.MaxResponseTokens ?? 256;

        int score = 0;
        var errors = new List<string>();

        _dispatcher.UpdateOperationName(ctx.RunId, $"jsonScore:{model.Name}");

        for (int i = 0; i < _tests.Count; i++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var test = _tests[i];
            var messages = new List<ConversationMessage>
            {
                new() { Role = "system", Content = SystemPrompt },
                new() { Role = "user", Content = test.Prompt }
            };

            try
            {
                var response = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), ctx.CancellationToken);
                var (content, _) = LangChainChatBridge.ParseChatResponse(response);
                var text = string.IsNullOrWhiteSpace(content) ? response : content!;

                var validation = ValidateResponse(text, test);
                if (validation.IsValid)
                {
                    score++;
                }
                else
                {
                    errors.Add($"Q{i + 1}: {validation.Error}");
                    _logger?.Append(ctx.RunId, $"[FAIL Q{i + 1}] {validation.Error} | risposta: {text}", "error");
                    _logger?.MarkLatestModelResponseResult("FAILED", validation.Error, examined: true);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Q{i + 1}: errore chiamata modello - {ex.Message}");
                _logger?.Append(ctx.RunId, $"[FAIL Q{i + 1}] Errore chiamata modello: {ex.Message}", "error");
            }

            _dispatcher.UpdateStep(ctx.RunId, i + 1, _tests.Count, $"[{model.Name}] Domanda {i + 1}/{_tests.Count} \u2022 parziale {score}");
        }

        var detail = errors.Count == 0 ? "tutte le risposte valide" : string.Join("; ", errors);

        // Scala 1..10 proporzionale al numero di risposte corrette (10 domande)
        var scaled = (int)Math.Round((double)score / _tests.Count * 10);
        var storedScore = Math.Clamp(scaled, 1, 10);

        return (storedScore, detail);
    }

    private static (bool IsValid, string Error) ValidateResponse(string text, JsonTestCase test)
    {
        if (!TryExtractJson(text, out var obj, out var parseError))
        {
            return (false, parseError);
        }

        var titoloNode = obj["titolo"];
        var prioritaNode = obj["priorita"];
        var completatoNode = obj["completato"];

        if (titoloNode == null || prioritaNode == null || completatoNode == null)
        {
            return (false, "mancano uno o piÃƒÂ¹ campi obbligatori");
        }

        string titolo;
        string priorita;
        bool? completato = null;

        try
        {
            titolo = titoloNode.GetValue<string>() ?? string.Empty;
            priorita = prioritaNode.GetValue<string>() ?? string.Empty;
            completato = completatoNode.GetValue<bool>();
        }
        catch (Exception ex)
        {
            return (false, $"tipi non corretti: {ex.Message}");
        }

        if (!string.Equals(priorita, test.ExpectedPriorita, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"priorita attesa '{test.ExpectedPriorita}', ottenuta '{priorita}'");
        }

        if (completato != test.ExpectedCompletato)
        {
            return (false, $"completato atteso {test.ExpectedCompletato.ToString().ToLowerInvariant()}, ottenuto {completato?.ToString().ToLowerInvariant() ?? "null"}");
        }

        if (test.TitleValidator != null && !test.TitleValidator(titolo))
        {
            var hint = string.IsNullOrWhiteSpace(test.ValidationHint) ? "vincolo sul titolo non rispettato" : test.ValidationHint;
            return (false, hint);
        }

        return (true, string.Empty);
    }

    private static bool TryExtractJson(string raw, out JsonObject obj, out string error)
    {
        error = string.Empty;
        obj = new JsonObject();

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "risposta vuota";
            return false;
        }

        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var secondFence = text.IndexOf("```", 3, StringComparison.Ordinal);
            if (secondFence > 0)
            {
                text = text.Substring(3, secondFence - 3);
            }
        }

        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            text = text.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        try
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject jsonObj)
            {
                obj = jsonObj;
                return true;
            }

            error = "il payload non ÃƒÂ¨ un oggetto JSON";
            return false;
        }
        catch (Exception ex)
        {
            error = $"JSON non valido: {ex.Message}";
            return false;
        }
    }

    private static int WordCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static bool IsCamelCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text, "^[a-z][A-Za-z0-9]*$");
    }

    private static bool ContainsDigit(string text)
    {
        return !string.IsNullOrWhiteSpace(text) && text.Any(char.IsDigit);
    }
}
