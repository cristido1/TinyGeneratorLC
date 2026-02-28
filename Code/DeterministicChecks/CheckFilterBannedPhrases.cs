using System.Text.RegularExpressions;

namespace TinyGenerator.Services;

public sealed class CheckFilterBannedPhrases : CheckBase
{
    public override string Rule => "Filtro frasi bannate (contains normalizzato).";
    public override string GenericErrorDescription => "Frase bannata rilevata";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var text = Normalize(textToCheck);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Build(true, "ok", started);
        }

        var bannedCsv = GetOption("BannedPhrasesCsv", string.Empty);
        var phrases = (bannedCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var banned in phrases)
        {
            if (text.Contains(banned, StringComparison.Ordinal))
            {
                return Build(false, $"Frase bannata rilevata: '{banned}'", started);
            }
        }

        return Build(true, "ok", started);
    }

    private static string Normalize(string? value)
    {
        var s = (value ?? string.Empty).ToLowerInvariant().Trim();
        s = Regex.Replace(s, @"\s+", " ");
        return s;
    }

    private static DeterministicResult Build(bool ok, string message, DateTime started)
        => new()
        {
            Successed = ok,
            Message = message,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
}
