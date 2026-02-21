using System.Text.Json;

namespace TinyGenerator.Services;

public sealed class CheckJsonArray : CheckBase
{
    public override string Rule => "Output deve essere un array JSON valido.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var normalized = NormalizePotentialJson(textToCheck);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Build(false, "Planner output vuoto", started);
        }

        try
        {
            using var doc = JsonDocument.Parse(normalized);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? Build(true, "ok", started)
                : Build(false, "Planner output non e' un array JSON", started);
        }
        catch
        {
            return Build(false, "Planner output non parseabile come JSON", started);
        }
    }

    private static string NormalizePotentialJson(string? json)
    {
        var text = (json ?? string.Empty).Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring(7).Trim();
        }
        else if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text.Substring(3).Trim();
        }

        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text.Substring(0, text.Length - 3).Trim();
        }

        return text;
    }

    private static DeterministicResult Build(bool ok, string message, DateTime started)
        => new()
        {
            Successed = ok,
            Message = message,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
}
