using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

/// <summary>
/// Esegue benchmark logico-aritmetico su 20 test e aggiorna intelliScore (1-10)
/// e intelliTime (secondi totali di risposta del modello).
/// </summary>
public sealed class IntelligenceScoreTestService
{
    private const string CommandKey = "intelligence_test";

    private sealed record IntelligenceTestCase(int Number, string Prompt, Func<string, bool> Validator, string ExpectedHint);

    private readonly DatabaseService _database;
    private readonly ILangChainKernelFactory _kernelFactory;
    private readonly ICommandDispatcher _dispatcher;
    private readonly ICustomLogger? _logger;
    private readonly int _timeoutSec;
    private readonly double _topP;

    private const string SystemPrompt = """
Sei in modalita benchmark intelligence.
Rispondi esclusivamente con la risposta richiesta dal test.
Nessuna spiegazione, nessun markdown, nessun testo extra.
""";

    private readonly IReadOnlyList<IntelligenceTestCase> _tests;

    public IntelligenceScoreTestService(
        DatabaseService database,
        ILangChainKernelFactory kernelFactory,
        ICommandDispatcher dispatcher,
        IConfiguration configuration,
        ICustomLogger? logger = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _kernelFactory = kernelFactory ?? throw new ArgumentNullException(nameof(kernelFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        var configuredTimeout = configuration?.GetValue<int?>($"Commands:ByCommand:{CommandKey}:timeoutSec")
            ?? configuration?.GetValue<int?>($"Commands:ByCommand:{CommandKey}:Tuning:QuestionTimeoutSeconds")
            ?? configuration?.GetValue<int?>("Commands:ByCommand:intelligence_score:Tuning:QuestionTimeoutSeconds");
        _timeoutSec = Math.Clamp(configuredTimeout ?? 10, 1, 600);

        var configuredTopP = configuration?.GetValue<double?>($"Commands:ByCommand:{CommandKey}:Tuning:TopP")
            ?? configuration?.GetValue<double?>("Commands:ByCommand:intelligence_score:Tuning:TopP");
        _topP = Math.Clamp(configuredTopP ?? 0.1, 0.0, 1.0);

        _logger = logger;
        _tests = BuildTests();
    }

    public CommandHandle EnqueueIntelligenceScoreForMissingModels()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = CommandKey
        };

        return _dispatcher.Enqueue(
            CommandKey,
            RunAsync,
            threadScope: CommandKey,
            metadata: metadata,
            priority: 3);
    }

    public CommandHandle EnqueueIntelligenceScoreForModel(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            throw new ArgumentNullException(nameof(modelName));

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = CommandKey,
            ["modelName"] = modelName.Trim()
        };

        return _dispatcher.Enqueue(
            CommandKey,
            RunAsync,
            threadScope: $"{CommandKey}/{Sanitize(modelName)}",
            metadata: metadata,
            priority: 2);
    }

    public CommandHandle? EnqueueIntelligenceScoreForModel(int modelId)
    {
        if (modelId <= 0) return null;

        var model = _database.ListModels().FirstOrDefault(m => m.Id.HasValue && m.Id.Value == modelId);
        if (model == null || !model.Id.HasValue || !model.Enabled) return null;

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = CommandKey,
            ["modelName"] = model.Name,
            ["stepCurrent"] = "0",
            ["stepMax"] = _tests.Count.ToString()
        };

        return _dispatcher.Enqueue(
            CommandKey,
            ctx => RunSingleModelAsync(ctx, model),
            threadScope: CommandKey,
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
            targets = _database.ListModelsWithoutIntelligenceScore();
            if (targets.Count == 0)
            {
                return new CommandResult(true, "Nessun modello da testare: intelliScore gia presente.");
            }

            targets = OrderModelsForIntelligenceTest(targets).ToList();
        }

        var processed = 0;
        foreach (var model in targets)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            if (!model.Id.HasValue) continue;

            var (score, elapsedSec, details) = await TestSingleModelAsync(model, ctx);
            _database.UpdateModelIntelligenceScore(model.Id.Value, score, elapsedSec, details);
            processed++;

            var summary = $"[{model.Name}] intelliScore={score}/10 intelliTime={elapsedSec}s";
            _logger?.Log("Info", "IntelligenceTest", summary, details);
        }

        return new CommandResult(true, $"intelligence_test completato per {processed} modelli");
    }

    private async Task<CommandResult> RunSingleModelAsync(CommandContext ctx, ModelInfo model)
    {
        if (!model.Id.HasValue)
        {
            return new CommandResult(false, "Modello non valido (Id mancante)");
        }

        var (score, elapsedSec, details) = await TestSingleModelAsync(model, ctx);
        _database.UpdateModelIntelligenceScore(model.Id.Value, score, elapsedSec, details);

        var summary = $"[{model.Name}] intelliScore={score}/10 intelliTime={elapsedSec}s";
        _logger?.Log("Info", "IntelligenceTest", summary, details);
        return new CommandResult(true, summary);
    }

    private async Task<(int Score, int ElapsedSeconds, string Details)> TestSingleModelAsync(ModelInfo model, CommandContext ctx)
    {
        var bridge = _kernelFactory.CreateChatBridge(model.Name, temperature: 0.1, topP: _topP, useMaxTokens: true);
        bridge.MaxResponseTokens = bridge.MaxResponseTokens ?? 256;

        var passed = 0;
        long totalElapsedMs = 0;
        var failures = new List<string>();

        _dispatcher.UpdateOperationName(ctx.RunId, $"{CommandKey}:{model.Name}");
        _dispatcher.UpdateStep(
            ctx.RunId,
            0,
            _tests.Count,
            $"[{model.Name}] Domanda 0/{_tests.Count} - score parziale 0/10 (0/0)");

        for (var i = 0; i < _tests.Count; i++)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var test = _tests[i];
            var messages = new List<ConversationMessage>
            {
                new() { Role = "system", Content = SystemPrompt },
                new() { Role = "user", Content = test.Prompt }
            };

            var sw = Stopwatch.StartNew();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSec));

                var response = await bridge.CallModelWithToolsAsync(messages, new List<Dictionary<string, object>>(), timeoutCts.Token);
                var (content, _) = LangChainChatBridge.ParseChatResponse(response);
                var text = NormalizeResponseText(string.IsNullOrWhiteSpace(content) ? response : content!);

                if (test.Validator(text))
                {
                    passed++;
                }
                else
                {
                    var err = $"atteso: {test.ExpectedHint}";
                    failures.Add($"T{test.Number:00}: {err}");
                    _logger?.Append(ctx.RunId, $"[FAIL T{test.Number:00}] {err} | risposta: {text}", "error");
                    _logger?.MarkLatestModelResponseResult("FAILED", err, examined: true);
                }
            }
            catch (OperationCanceledException) when (!ctx.CancellationToken.IsCancellationRequested)
            {
                var timeoutError = $"timeout domanda dopo {_timeoutSec}s";
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
            finally
            {
                sw.Stop();
                totalElapsedMs += sw.ElapsedMilliseconds;
            }

            var partialScore = Math.Clamp((int)Math.Round((double)passed / (i + 1) * 10), 1, 10);
            _dispatcher.UpdateStep(
                ctx.RunId,
                i + 1,
                _tests.Count,
                $"[{model.Name}] Domanda {i + 1}/{_tests.Count} - score parziale {partialScore}/10 ({passed}/{i + 1})");
        }

        var details = failures.Count == 0 ? "tutte le risposte valide" : string.Join("; ", failures);
        var finalScaled = (int)Math.Round((double)passed / _tests.Count * 10);
        var finalScore = Math.Clamp(finalScaled, 1, 10);
        var elapsedSeconds = (int)Math.Round(totalElapsedMs / 1000.0, MidpointRounding.AwayFromZero);
        return (finalScore, elapsedSeconds, details);
    }

    private static IEnumerable<ModelInfo> OrderModelsForIntelligenceTest(IEnumerable<ModelInfo> models)
    {
        return models
            .OrderBy(m => string.Equals(m.Provider?.Trim(), "ollama", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<IntelligenceTestCase> BuildTests()
    {
        return new List<IntelligenceTestCase>
        {
            new(1,  "TEST 1 - Aritmetica base\nCalcola:\n47 + 38 - 19\nOutput atteso: numero intero", IsExpectedInteger(66), "66"),
            new(2,  "TEST 2 - Priorita operatori\nCalcola:\n6 + 4 * 3\nOutput atteso: numero intero", IsExpectedInteger(18), "18"),
            new(3,  "TEST 3 - Frazione\nCalcola:\n(45 / 5) + (18 / 3)\nOutput atteso: numero intero", IsExpectedInteger(15), "15"),
            new(4,  "TEST 4 - Potenze\nCalcola:\n2^5 + 3^3\nOutput atteso: numero intero", IsExpectedInteger(59), "59"),
            new(5,  "TEST 5 - Sequenza numerica\nQual e il numero successivo?\n3, 6, 12, 24, ?\nOutput atteso: numero intero", IsExpectedInteger(48), "48"),
            new(6,  "TEST 6 - Parita\nIl numero 7421 e pari o dispari?\nOutput atteso: PARI oppure DISPARI", IsParity("DISPARI"), "DISPARI"),
            new(7,  "TEST 7 - Conteggio caratteri\nQuanti caratteri (spazi inclusi) ha la stringa:\nAI TEST\nOutput atteso: numero intero", IsExpectedInteger(7), "7"),
            new(8,  "TEST 8 - Reverse string\nInverti la stringa:\nMODEL\nOutput atteso: stringa invertita", IsExpectedToken("LEDOM"), "LEDOM"),
            new(9,  "TEST 9 - Ordinamento alfabetico\nOrdina alfabeticamente queste parole:\npera, mela, banana\nOutput atteso: parole separate da virgola e spazio", IsOrderedWords("banana", "mela", "pera"), "banana, mela, pera"),
            new(10, "TEST 10 - Lettera centrale\nQual e la lettera centrale della parola:\nAGENTE\nOutput atteso: una sola lettera maiuscola", IsOneOfTokens("E", "N"), "E oppure N"),
            new(11, "TEST 11 - Logica booleana\nValuta:\n(VERO AND FALSO) OR VERO\nOutput atteso: VERO o FALSO", IsBoolean("VERO"), "VERO"),
            new(12, "TEST 12 - Conteggio vocali\nQuante vocali contiene la parola:\nISTRUZIONE\nOutput atteso: numero intero", IsExpectedInteger(5), "5"),
            new(13, "TEST 13 - Differenza assoluta\nCalcola la differenza assoluta tra:\n|15 - 42|\nOutput atteso: numero intero", IsExpectedInteger(27), "27"),
            new(14, "TEST 14 - Moltiplicazione mentale\nCalcola:\n125 * 8\nOutput atteso: numero intero", IsExpectedInteger(1000), "1000"),
            new(15, "TEST 15 - Divisione con resto\nQuanto vale il resto di:\n53 / 7\nOutput atteso: numero intero", IsExpectedInteger(4), "4"),
            new(16, "TEST 16 - Sequenza logica\nQual e il prossimo numero?\n1, 4, 9, 16, ?\nOutput atteso: numero intero", IsExpectedInteger(25), "25"),
            new(17, "TEST 17 - Conteggio parole\nQuante parole ci sono nella frase:\nIl modello segue le istruzioni\nOutput atteso: numero intero", IsExpectedInteger(5), "5"),
            new(18, "TEST 18 - Maiuscole\nScrivi in MAIUSCOLO la parola:\npipeline\nOutput atteso: parola in maiuscolo", IsExpectedToken("PIPELINE"), "PIPELINE"),
            new(19, "TEST 19 - Somma cifre\nSomma le cifre del numero:\n9047\nOutput atteso: numero intero", IsExpectedInteger(20), "20"),
            new(20, "TEST 20 - Logica deduttiva\nTutti i robot sono macchine.\nAlcune macchine sono veloci.\nPossiamo concludere che tutti i robot sono veloci?\nOutput atteso: SI o NO", IsYesNo("NO"), "NO")
        };
    }

    private static Func<string, bool> IsExpectedInteger(int expected)
        => text => TryExtractInt(text, out var n) && n == expected;

    private static Func<string, bool> IsExpectedToken(string expected)
        => text => ContainsToken(text, expected);

    private static Func<string, bool> IsOneOfTokens(params string[] options)
        => text => options.Any(option => ContainsToken(text, option));

    private static Func<string, bool> IsParity(string expected)
        => text => expected.Equals("DISPARI", StringComparison.OrdinalIgnoreCase)
            ? ContainsAnyToken(text, "DISPARI", "ODD")
            : ContainsAnyToken(text, "PARI", "EVEN");

    private static Func<string, bool> IsBoolean(string expected)
        => text => expected.Equals("VERO", StringComparison.OrdinalIgnoreCase)
            ? ContainsAnyToken(text, "VERO", "TRUE")
            : ContainsAnyToken(text, "FALSO", "FALSE");

    private static Func<string, bool> IsYesNo(string expected)
        => text => expected.Equals("NO", StringComparison.OrdinalIgnoreCase)
            ? ContainsAnyToken(text, "NO", "FALSE", "FALSO")
            : ContainsAnyToken(text, "SI", "S", "YES", "TRUE", "VERO");

    private static Func<string, bool> IsOrderedWords(params string[] orderedWords)
        => text =>
        {
            var words = TokenizeWords(text);
            var index = 0;
            foreach (var expected in orderedWords)
            {
                var found = false;
                while (index < words.Count)
                {
                    if (string.Equals(words[index], NormalizeToken(expected), StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        index++;
                        break;
                    }
                    index++;
                }

                if (!found) return false;
            }

            return true;
        };

    private static bool TryExtractInt(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = NormalizeResponseText(text);

        var direct = Regex.Match(normalized, @"^\s*[-+]?\d+\s*$");
        if (direct.Success && int.TryParse(direct.Value.Trim(), out value))
        {
            return true;
        }

        var first = Regex.Match(normalized, @"[-+]?\d+");
        return first.Success && int.TryParse(first.Value, out value);
    }

    private static bool ContainsAnyToken(string text, params string[] tokens)
        => tokens.Any(token => ContainsToken(text, token));

    private static bool ContainsToken(string text, string token)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(token)) return false;

        var normalizedToken = NormalizeToken(token);
        var normalizedText = NormalizeToken(text);
        if (normalizedText == normalizedToken) return true;

        var words = TokenizeWords(text);
        return words.Any(w => string.Equals(w, normalizedToken, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> TokenizeWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        return Regex.Matches(NormalizeResponseText(text), @"[A-Za-z0-9]+")
            .Select(m => NormalizeToken(m.Value))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
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
}
