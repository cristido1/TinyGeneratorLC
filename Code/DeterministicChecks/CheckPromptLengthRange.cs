using System.Text.RegularExpressions;

namespace TinyGenerator.Services;

public sealed class CheckPromptLengthRange : CheckBase
{
    public override string Rule => "Prompt normalizzato con range di lunghezza valido.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var normalized = NormalizePrompt(textToCheck);
        var minLength = Math.Max(0, GetOption("MinLength", 0));
        var maxLength = Math.Max(minLength, GetOption("MaxLength", int.MaxValue));

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Build(false, "Risposta vuota", started);
        }

        if (normalized.Length < minLength)
        {
            return Build(false, "Prompt troppo corto", started);
        }

        if (normalized.Length > maxLength)
        {
            return Build(false, "Prompt troppo lungo", started);
        }

        return Build(true, "ok", started);
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

    private static DeterministicResult Build(bool ok, string message, DateTime started)
        => new()
        {
            Successed = ok,
            Message = message,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
}
