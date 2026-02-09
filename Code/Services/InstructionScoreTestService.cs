using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

/// <summary>
/// Esegue benchmark di instruction-following sui prompt in docs/instructions_test.txt
/// e aggiorna il campo InstructionScore nella tabella models (scala 1-10).
/// </summary>
public sealed class InstructionScoreTestService
{
    private sealed record InstructionTestCase(int Number, string Prompt);

    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICommandDispatcher _dispatcher;
    private readonly ICustomLogger? _logger;
    private readonly IReadOnlyList<InstructionTestCase> _tests;
    private readonly int _questionTimeoutSeconds;
    private readonly double _topP;

    private const string SystemPrompt = """
Sei in modalità benchmark instruction-following.
Rispetta rigorosamente il formato richiesto dal prompt utente.
Non aggiungere testo extra, markdown o spiegazioni non richieste.
""";

    public InstructionScoreTestService(
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        ICommandDispatcher dispatcher,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        ICustomLogger? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        var configuredTimeout = configuration?.GetValue<int?>("Commands:ByCommand:instruction_score:Tuning:QuestionTimeoutSeconds");
        _questionTimeoutSeconds = Math.Clamp(configuredTimeout ?? 45, 1, 600);
        var configuredTopP = configuration?.GetValue<double?>("Commands:ByCommand:instruction_score:Tuning:TopP");
        _topP = Math.Clamp(configuredTopP ?? 0.1, 0.0, 1.0);
        _logger = logger;

        var path = Path.Combine(hostEnvironment.ContentRootPath, "docs", "instructions_test.txt");
        _tests = LoadTestsFromFile(path);
    }

    public CommandHandle EnqueueInstructionScoreForMissingModels()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = "instruction_score"
        };

        return _dispatcher.Enqueue(
            "instruction_score",
            RunAsync,
            threadScope: "instruction_score",
            metadata: metadata,
            priority: 3);
    }

    public CommandHandle EnqueueInstructionScoreForModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentNullException(nameof(modelName));

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = "instruction_score",
            ["modelName"] = modelName.Trim()
        };

        return _dispatcher.Enqueue(
            $"instruction_score_{Sanitize(modelName)}",
            RunAsync,
            threadScope: $"instruction_score/{Sanitize(modelName)}",
            metadata: metadata,
            priority: 2);
    }

    public CommandHandle? EnqueueInstructionScoreForModel(int modelId)
    {
        if (modelId <= 0) return null;

        var model = _database.ListModels().FirstOrDefault(m => m.Id.HasValue && m.Id.Value == modelId);
        if (model == null || !model.Id.HasValue || !model.Enabled) return null;

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = "instruction_score",
            ["modelName"] = model.Name,
            ["stepCurrent"] = "0",
            ["stepMax"] = _tests.Count.ToString()
        };

        return _dispatcher.Enqueue(
            "instruction_score",
            ctx => RunSingleModelAsync(ctx, model),
            threadScope: "instruction_score",
            metadata: metadata,
            priority: 3);
    }

    private async Task<CommandResult> RunAsync(CommandContext ctx)
    {
        if (_tests.Count == 0)
        {
            return new CommandResult(false, "Nessun test instruction disponibile (docs/instructions_test.txt vuoto o non valido).");
        }

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
            targets = _database.ListModelsWithoutInstructionScore();
            if (targets.Count == 0)
            {
                return new CommandResult(true, "Nessun modello da testare: instructionScore già presente.");
            }

            targets = OrderModelsForScoreTests(targets).ToList();
        }

        var processed = 0;
        foreach (var model in targets)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (!model.Id.HasValue) continue;

            var (score, details) = await TestSingleModelAsync(model, ctx);
            _database.UpdateModelInstructionScore(model.Id.Value, score, details);
            processed++;

            var summary = $"[{model.Name}] instructionScore={score}/10";
            _logger?.Log("Info", "InstructionScoreTest", summary, details);
        }

        return new CommandResult(true, $"instructionScore calcolato per {processed} modelli");
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

        if (_tests.Count == 0)
        {
            return new CommandResult(false, "Nessun test instruction disponibile (docs/instructions_test.txt vuoto o non valido).");
        }

        var (score, details) = await TestSingleModelAsync(model, ctx);
        _database.UpdateModelInstructionScore(model.Id.Value, score, details);

        var summary = $"[{model.Name}] instructionScore={score}/10";
        _logger?.Log("Info", "InstructionScoreTest", summary, details);
        return new CommandResult(true, summary);
    }

    private async Task<(int Score, string Details)> TestSingleModelAsync(ModelInfo model, CommandContext ctx)
    {
        var bridge = _kernelFactory.CreateChatBridge(model.Name, temperature: 0.1, topP: _topP, useMaxTokens: true);
        bridge.MaxResponseTokens = bridge.MaxResponseTokens ?? 900;

        var passed = 0;
        var failures = new List<string>();
        _dispatcher.UpdateOperationName(ctx.RunId, $"instructionScore:{model.Name}");
        _dispatcher.UpdateStep(
            ctx.RunId,
            0,
            _tests.Count,
            $"[{model.Name}] Test 0/{_tests.Count} • score parziale 0/10 (0/0)");

        for (var i = 0; i < _tests.Count; i++)
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
                using var questionTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
                questionTimeoutCts.CancelAfter(TimeSpan.FromSeconds(_questionTimeoutSeconds));
                var response = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), questionTimeoutCts.Token);
                var (content, _) = LangChainChatBridge.ParseChatResponse(response);
                var text = NormalizeResponseText(string.IsNullOrWhiteSpace(content) ? response : content!);

                var (ok, error) = Validate(test.Number, text);
                if (ok)
                {
                    passed++;
                }
                else
                {
                    failures.Add($"T{test.Number:00}: {error}");
                    _logger?.Append(ctx.RunId, $"[FAIL T{test.Number:00}] {error} | risposta: {text}", "error");
                    _logger?.MarkLatestModelResponseResult("FAILED", error, examined: true);
                }
            }
            catch (OperationCanceledException) when (!ctx.CancellationToken.IsCancellationRequested)
            {
                var timeoutError = $"timeout domanda dopo {_questionTimeoutSeconds}s";
                failures.Add($"T{test.Number:00}: {timeoutError}");
                _logger?.Append(ctx.RunId, $"[FAIL T{test.Number:00}] {timeoutError}", "error");
                _logger?.MarkLatestModelResponseResult("FAILED", timeoutError, examined: true);
            }
            catch (Exception ex)
            {
                failures.Add($"T{test.Number:00}: errore chiamata modello - {ex.Message}");
                _logger?.Append(ctx.RunId, $"[FAIL T{test.Number:00}] Errore chiamata modello: {ex.Message}", "error");
                _logger?.MarkLatestModelResponseResult("FAILED", ex.Message, examined: true);
            }

            var partialScore = Math.Clamp((int)Math.Round((double)passed / (i + 1) * 10), 1, 10);
            _dispatcher.UpdateStep(
                ctx.RunId,
                i + 1,
                _tests.Count,
                $"[{model.Name}] Test {i + 1}/{_tests.Count} • score parziale {partialScore}/10 ({passed}/{i + 1})");
        }

        var detail = failures.Count == 0 ? "tutte le risposte valide" : string.Join("; ", failures);
        var finalScaled = (int)Math.Round((double)passed / _tests.Count * 10);
        var finalScore = Math.Clamp(finalScaled, 1, 10);
        return (finalScore, detail);
    }

    private static IReadOnlyList<InstructionTestCase> LoadTestsFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return Array.Empty<InstructionTestCase>();
        }

        var text = File.ReadAllText(filePath);
        var matches = Regex.Matches(
            text,
            @"TEST\s*(\d{2}).*?REQUEST:\s*(.*?)(?=(?:\r?\n=+\r?\nTEST\s*\d{2})|(?:\r?\nNOTE DI VALUTAZIONE)|\z)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var tests = new List<InstructionTestCase>();
        foreach (Match m in matches)
        {
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out var number)) continue;
            var prompt = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(prompt)) continue;
            tests.Add(new InstructionTestCase(number, prompt));
        }

        return tests.OrderBy(t => t.Number).ToList();
    }

    private static string Sanitize(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .ToArray();
        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "model" : sanitized;
    }

    private static string NormalizeResponseText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count >= 2)
            {
                if (lines[0].StartsWith("```", StringComparison.Ordinal)) lines.RemoveAt(0);
                if (lines.Count > 0 && lines[^1].StartsWith("```", StringComparison.Ordinal)) lines.RemoveAt(lines.Count - 1);
                text = string.Join('\n', lines).Trim();
            }
        }
        return text;
    }

    private static (bool Ok, string Error) Validate(int testNumber, string text)
    {
        return testNumber switch
        {
            1 => Validate01(text),
            2 => Validate02(text),
            3 => Validate03(text),
            4 => Validate04(text),
            5 => Validate05(text),
            6 => Validate06(text),
            7 => Validate07(text),
            8 => Validate08(text),
            9 => Validate09(text),
            10 => Validate10(text),
            11 => Validate11(text),
            12 => Validate12(text),
            13 => Validate13(text),
            14 => Validate14(text),
            15 => Validate15(text),
            16 => Validate16(text),
            17 => Validate17(text),
            18 => Validate18(text),
            19 => Validate19(text),
            20 => Validate20(text),
            _ => (!string.IsNullOrWhiteSpace(text), "risposta vuota")
        };
    }

    private static (bool, string) Validate01(string text)
        => text == "OK-TEST-001" ? (true, string.Empty) : (false, "output diverso da OK-TEST-001");

    private static (bool, string) Validate02(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n');
        if (lines.Length != 3) return (false, "devono essere esattamente 3 righe");
        if (lines.Any(l => l.Length != 12)) return (false, "ogni riga deve essere lunga esattamente 12 caratteri");
        if (!Regex.IsMatch(lines[0], "^[A-Za-z]{12}$")) return (false, "riga 1 non contiene solo lettere");
        if (!Regex.IsMatch(lines[1], @"^\d{12}$")) return (false, "riga 2 non contiene solo numeri");
        if (!Regex.IsMatch(lines[2], @"^[#@!\$%&\*]{12}$")) return (false, "riga 3 non contiene solo simboli consentiti");
        return (true, string.Empty);
    }

    private static (bool, string) Validate03(string text)
    {
        if (!TryParseJsonObject(text, out var root, out var err)) return (false, err);
        var keys = TopLevelKeys(root).ToList();
        if (keys.Count != 3 || keys[0] != "id" || keys[1] != "items" || keys[2] != "sum") return (false, "ordine chiavi non valido");
        if (!root.TryGetPropertyValue("id", out var idNode) || idNode?.GetValue<string>() != "T003") return (false, "id non valido");
        if (!root.TryGetPropertyValue("sum", out var sumNode) || sumNode?.GetValue<int>() != 3) return (false, "sum non valido");
        if (!root.TryGetPropertyValue("items", out var itemsNode) || itemsNode is not JsonArray arr || arr.Count != 2) return (false, "items non valido");
        var okA = arr[0]?["k"]?.GetValue<string>() == "a" && arr[0]?["v"]?.GetValue<int>() == 1;
        var okB = arr[1]?["k"]?.GetValue<string>() == "b" && arr[1]?["v"]?.GetValue<int>() == 2;
        return okA && okB ? (true, string.Empty) : (false, "items non coerente");
    }

    private static (bool, string) Validate04(string text)
        => text == "out:il_gatto_nero_corre_veloce" ? (true, string.Empty) : (false, "trasformazione non corretta");

    private static (bool, string) Validate05(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n');
        if (lines.Length != 5) return (false, "devono essere 5 righe");
        var expected = new[]
        {
            ("FATTO", "Domani piove a Venezia"),
            ("OPINIONE", "Secondo me questo film"),
            ("ISTRUZIONE", "Chiudi la porta"),
            ("FATTO", "Il rame conduce elettricita"),
            ("OPINIONE", "la migliore scelta")
        };

        for (var i = 0; i < 5; i++)
        {
            var line = RemoveDiacritics(lines[i]);
            if (!line.StartsWith($"{i + 1})", StringComparison.Ordinal)) return (false, $"riga {i + 1} senza indice");
            if (!line.Contains(":")) return (false, $"riga {i + 1} senza ':'");
            if (!line.Contains(expected[i].Item1, StringComparison.OrdinalIgnoreCase)) return (false, $"etichetta errata riga {i + 1}");
            if (!line.Contains(expected[i].Item2, StringComparison.OrdinalIgnoreCase)) return (false, $"frase originale non preservata riga {i + 1}");
        }
        return (true, string.Empty);
    }

    private static (bool, string) Validate06(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, "risposta vuota");
        if (!Regex.IsMatch(text.Trim(), @"^\S+$")) return (false, "deve essere una sola parola");
        return (true, string.Empty);
    }

    private static (bool, string) Validate07(string text)
        => text == "RISULTATO=258" ? (true, string.Empty) : (false, "risultato non conforme");

    private static (bool, string) Validate08(string text)
    {
        var keywords = new[] { "schema", "chiave", "versione", "fallback", "sicurezza", "migrazione" };
        var lines = text.Replace("\r", string.Empty).Split('\n');
        if (lines.Length != 6) return (false, "devono essere 6 bullet");
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (!line.StartsWith("- ", StringComparison.Ordinal)) return (false, "bullet non valido");
            if (line.Length > 80) return (false, "una riga supera 80 caratteri");
            var matches = keywords.Where(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) return (false, "ogni riga deve contenere una sola keyword");
            found.Add(matches[0]);
        }
        return found.Count == keywords.Length ? (true, string.Empty) : (false, "keyword mancanti o duplicate");
    }

    private static (bool, string) Validate09(string text)
        => text == "IMPORTANTE" ? (true, string.Empty) : (false, "output atteso IMPORTANTE");

    private static (bool, string) Validate10(string text)
    {
        if (!TryParseJsonObject(text, out var root, out var err)) return (false, err);
        var keys = TopLevelKeys(root).ToList();
        if (keys.Count != 3 || keys[0] != "id" || keys[1] != "steps" || keys[2] != "checksum") return (false, "chiavi top-level non valide");
        if (root["id"]?.GetValue<string>() != "T010") return (false, "id non valido");
        if (root["steps"] is not JsonArray steps || steps.Count != 4) return (false, "steps deve avere 4 elementi");

        var allT = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            if (steps[i] is not JsonObject s) return (false, $"step {i + 1} non oggetto");
            var sk = TopLevelKeys(s).ToList();
            if (sk.Count != 2 || sk[0] != "i" || sk[1] != "t") return (false, $"chiavi step {i + 1} non valide");
            if (s["i"]?.GetValue<int>() != i + 1) return (false, $"indice step {i + 1} non valido");
            var t = s["t"]?.GetValue<string>() ?? string.Empty;
            if (t.Length != 20 || !t.EndsWith(".", StringComparison.Ordinal)) return (false, $"campo t step {i + 1} non valido");
            allT.Add(t);
        }

        var expectedChecksum = string.Concat(allT).Substring(0, 10);
        var checksum = root["checksum"]?.GetValue<string>() ?? string.Empty;
        return checksum == expectedChecksum ? (true, string.Empty) : (false, "checksum non coerente");
    }

    private static (bool, string) Validate11(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n');
        var expected = new[] { "id;nome;eta", "1;Ada;33", "2;Bruno;41", "3;Carla;29" };
        if (lines.Length != expected.Length) return (false, "numero righe CSV non valido");
        for (var i = 0; i < expected.Length; i++)
        {
            if (lines[i] != expected[i]) return (false, $"riga CSV {i + 1} non valida");
        }
        return (true, string.Empty);
    }

    private static (bool, string) Validate12(string text)
        => text == "1, 2, 2, 5, 5, 9" ? (true, string.Empty) : (false, "ordinamento non corretto");

    private static (bool, string) Validate13(string text)
        => text == "uno|due" ? (true, string.Empty) : (false, "estrazione non corretta");

    private static (bool, string) Validate14(string text)
    {
        if (!TryParseJsonObject(text, out var root, out var err)) return (false, err);
        var keys = TopLevelKeys(root).ToList();
        if (keys.Count != 2 || keys[0] != "is_valid" || keys[1] != "fixed") return (false, "chiavi non valide");
        if (root["is_valid"]?.GetValue<bool>() != false) return (false, "is_valid deve essere false");
        var fixedValue = root["fixed"]?.GetValue<string>() ?? string.Empty;
        if (fixedValue != "AB-12-XY") return (false, "fixed non coerente con correzione minima");
        return Regex.IsMatch(fixedValue, "^[A-Z]{2}-\\d{2}-[A-Z]{2}$")
            ? (true, string.Empty)
            : (false, "fixed non rispetta il pattern");
    }

    private static (bool, string) Validate15(string text)
    {
        var cleaned = text.Trim();
        var sentenceCount = Regex.Matches(cleaned, @"[^.!?]+[.!?]").Count;
        if (sentenceCount != 2) return (false, "devono essere esattamente 2 frasi");
        var forbidden = new[] { "server", "database", "cache" };
        if (forbidden.Any(f => cleaned.Contains(f, StringComparison.OrdinalIgnoreCase))) return (false, "contiene parole vietate");
        var words = Regex.Split(cleaned, @"\s+").Where(w => !string.IsNullOrWhiteSpace(w)).Count();
        return words <= 25 ? (true, string.Empty) : (false, "supera 25 parole");
    }

    private static (bool, string) Validate16(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n');
        if (lines.Length != 3) return (false, "devono essere 3 righe");
        if (lines[0] != "Nome|Punti") return (false, "header non valido");
        if (lines[1] != "Ada|10") return (false, "riga Ada non valida");
        if (lines[2] != "Bruno|7") return (false, "riga Bruno non valida");
        if (lines.Any(l => l.Contains(" |", StringComparison.Ordinal) || l.Contains("| ", StringComparison.Ordinal))) return (false, "spazi attorno a '|' non consentiti");
        return (true, string.Empty);
    }

    private static (bool, string) Validate17(string text)
        => text == "VALUE=true" ? (true, string.Empty) : (false, "output atteso VALUE=true");

    private static (bool, string) Validate18(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n');
        var expected = new[] { "alpha", "beta", "gamma" };
        if (lines.Length != 3) return (false, "devono essere 3 righe");
        for (var i = 0; i < 3; i++)
        {
            if (!string.Equals(lines[i].Trim(), expected[i], StringComparison.Ordinal)) return (false, $"riga {i + 1} non valida");
        }
        return (true, string.Empty);
    }

    private static (bool, string) Validate19(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, "risposta vuota");
        try
        {
            var node = JsonNode.Parse(text);
            if (node is not JsonArray arr) return (false, "deve essere un array JSON");
            if (arr.Count != 5) return (false, "array deve contenere 5 numeri");
            var values = arr.Select(n => n?.GetValue<int>() ?? int.MinValue).ToList();
            if (values.Any(v => v < 10 || v > 20)) return (false, "numeri fuori range 10..20");
            if (values.Distinct().Count() != 5) return (false, "numeri non tutti distinti");
            for (var i = 1; i < values.Count; i++)
            {
                if (values[i] >= values[i - 1]) return (false, "ordine non decrescente stretto");
            }
            return values.Sum() == 75 ? (true, string.Empty) : (false, "somma diversa da 75");
        }
        catch (Exception ex)
        {
            return (false, $"JSON non valido: {ex.Message}");
        }
    }

    private static (bool, string) Validate20(string text)
        => text == "tre 7" ? (true, string.Empty) : (false, "output atteso 'tre 7'");

    private static bool TryParseJsonObject(string text, out JsonObject obj, out string error)
    {
        obj = new JsonObject();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "risposta vuota";
            return false;
        }

        try
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject jo)
            {
                obj = jo;
                return true;
            }
            error = "payload non JSON object";
            return false;
        }
        catch (Exception ex)
        {
            error = $"JSON non valido: {ex.Message}";
            return false;
        }
    }

    private static IEnumerable<string> TopLevelKeys(JsonObject obj)
    {
        foreach (var kv in obj)
        {
            yield return kv.Key;
        }
    }

    private static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var normalized = text.Normalize(NormalizationForm.FormD);
        var filtered = normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
        return new string(filtered.ToArray()).Normalize(NormalizationForm.FormC);
    }
}
