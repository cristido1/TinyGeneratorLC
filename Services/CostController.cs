using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CallRecord = TinyGenerator.Models.CallRecord;
using ModelRecord = TinyGenerator.Models.ModelInfo;

namespace TinyGenerator.Services;

public sealed class CostController
{
    private readonly DatabaseService _database;

    // per modello costo per 1000 token (esempio). Aggiorna in base al provider usato.
    private readonly Dictionary<string, double> _costPerK = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4"] = 0.06,
        ["gpt-3.5"] = 0.002,
        ["ollama"] = 0.0 // local ollama - zero cost by default
    };

    public long MaxTokensPerRun { get; init; } = 200000;
    public double MaxCostPerMonth { get; init; } = 50.0;

    private readonly ITokenizer? _tokenizer;

    public CostController(DatabaseService database, ITokenizer? tokenizer = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _tokenizer = tokenizer;
    }

    // Database schema handled by DatabaseService

    // Model metadata lives in TinyGenerator.Models namespace

    // Lookup model info by exact name first, then by provider fallback
    public ModelRecord? GetModelInfo(string modelOrProvider) => _database.GetModelInfo(modelOrProvider);

    // Insert or update a model config
    public void UpsertModel(ModelRecord model)
    {
        if (model == null) return;
        _database.UpsertModel(model);
    }

    // List all models
    public List<ModelRecord> ListModels() => _database.ListModels();

    // Delegate discovery to DatabaseService so CostController does not perform DB operations itself.
    // Returns number of newly added models.
    public async Task<int> PopulateLocalOllamaModelsAsync()
    {
        try
        {
            return await _database.AddLocalOllamaModelsAsync();
        }
        catch
        {
            return 0;
        }
    }

    // semplice euristica: 1 token ~ 4 caratteri
    public int EstimateTokensFromText(string text)
    {
        try
        {
            if (_tokenizer != null) return Math.Max(1, _tokenizer.CountTokens(text));
        }
        catch { }
        return Math.Max(1, text?.Length / 4 ?? 1);
    }

    // Estimate cost given explicit input/output token counts. Uses model config if available.
    public double EstimateCost(string model, int inputTokens, int outputTokens)
    {
        // try to read model info (full name or provider)
        var mi = GetModelInfo(model);
        if (mi != null)
        {
            // Interpretiamo i campi CostInPerToken / CostOutPerToken come costo in USD per 1000 token (per-1k tokens).
            // Questo è più leggibile per valori pratici (es. $ per 1k tokens). Per calcolare il costo reale:
            // cost = (tokens / 1000) * costPerThousand
            var inCost = (inputTokens / 1000.0) * mi.CostInPerToken;
            var outCost = (outputTokens / 1000.0) * mi.CostOutPerToken;
            return inCost + outCost;
        }

        // fallback: use provider-level rates (per 1000 tokens stored in _costPerK)
        var provider = model.Split(':')[0];
        var rate = _costPerK.ContainsKey(provider) ? _costPerK[provider] : 0.01;
        var total = inputTokens + outputTokens;
        return (total / 1000.0) * rate;
    }

    // Backward-compatible overload: estimate cost given a single tokens value (assume half/half)
    public double EstimateCost(string model, int tokens)
    {
        var half = tokens / 2;
        return EstimateCost(model, half, tokens - half);
    }

    // verifica e riserva tokens per questa chiamata; ritorna false se non consentito
    // Reserve tokens for a call. Accepts input and output tokens counts.
    public bool TryReserve(string model, int inputTokens, int outputTokens)
    {
        var monthKey = DateTime.UtcNow.ToString("yyyy-MM");
        var estimatedCost = EstimateCost(model, inputTokens, outputTokens);
        var tokens = inputTokens + outputTokens;
        return _database.TryReserveUsage(monthKey, tokens, estimatedCost, MaxTokensPerRun, MaxCostPerMonth);
    }

    public void RecordCall(string model, int inputTokens, int outputTokens, double cost, string request, string response)
    {
        _database.RecordCall(model, inputTokens, outputTokens, cost, request, response);
    }

    public void ResetRunCounters()
    {
        var monthKey = DateTime.UtcNow.ToString("yyyy-MM");
        _database.ResetRunCounters(monthKey);
    }

    // utility: read latest usage summary
    public (long tokensThisMonth, double costThisMonth) GetMonthUsage()
    {
        var monthKey = DateTime.UtcNow.ToString("yyyy-MM");
        return _database.GetMonthUsage(monthKey);
    }

    // Retrieve recent calls (for admin UI)
    public List<CallRecord> GetRecentCalls(int limit = 50)
    {
        return _database.GetRecentCalls(limit);
    }

    public void UpdateModelTestResults(string modelName, int functionCallingScore, IReadOnlyDictionary<string, bool?> skillFlags, double? testDurationSeconds = null)
    {
        _database.UpdateModelTestResults(modelName, functionCallingScore, skillFlags, testDurationSeconds);
    }
}
