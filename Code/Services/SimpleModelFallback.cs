using System.Collections.Generic;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

public record FallbackResult(
    bool RetrySameModel,
    string? NextModelName);

public class SimpleModelFallback
{
    public FallbackResult Decide(
        int currentAttempt,
        int maxAttempts,
        bool fallbackEnabled,
        IReadOnlySet<string> triedModels,
        IEnumerable<ModelRole> availableFallbacks)
    {
        if (currentAttempt < maxAttempts)
        {
            return new FallbackResult(RetrySameModel: true, NextModelName: null);
        }

        if (!fallbackEnabled)
        {
            return new FallbackResult(RetrySameModel: false, NextModelName: null);
        }

        foreach (var fallback in availableFallbacks)
        {
            var modelName = fallback.Model?.Name;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                continue;
            }

            if (!triedModels.Contains(modelName))
            {
                return new FallbackResult(RetrySameModel: false, NextModelName: modelName);
            }
        }

        return new FallbackResult(RetrySameModel: false, NextModelName: null);
    }
}
