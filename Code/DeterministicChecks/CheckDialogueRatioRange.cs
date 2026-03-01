using System.Text.RegularExpressions;

namespace TinyGenerator.Services;

public sealed class CheckDialogueRatioRange : CheckBase
{
    public override string Rule => "Percentuale dialoghi nel range target.";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var text = (textToCheck ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Build(false, "testo vuoto", started);
        }

        var minChars = Math.Max(80, GetOption("MinChars", 120));
        var targetPercent = Math.Clamp(GetOption("TargetPercent", 40), 0, 100);
        var legacyTolerance = Math.Clamp(GetOption("TolerancePercent", 5), 0, 50);
        var toleranceMinusPercent = Math.Clamp(GetOption("TolerancePercentMinus", legacyTolerance), 0, 50);
        var tolerancePlusPercent = Math.Clamp(GetOption("TolerancePercentPlus", legacyTolerance), 0, 50);
        var minPercent = Math.Max(0, targetPercent - toleranceMinusPercent);
        var maxPercent = Math.Min(100, targetPercent + tolerancePlusPercent);

        var totalChars = CountMeaningfulChars(text);
        if (totalChars < minChars)
        {
            return Build(true, $"skip: testo breve ({totalChars} char)", started);
        }

        var dialogueChars = CountDialogueChars(text);
        var dialoguePercent = totalChars <= 0
            ? 0d
            : (dialogueChars * 100d) / totalChars;

        var ok = dialoguePercent >= minPercent && dialoguePercent <= maxPercent;
        var msg = ok
            ? $"ok: dialoghi={dialoguePercent:F1}% (target={targetPercent}% -{toleranceMinusPercent}%/+{tolerancePlusPercent}%)"
            : $"dialoghi fuori range: {dialoguePercent:F1}% (atteso {minPercent}%..{maxPercent}%)";

        return Build(ok, msg, started);
    }

    private static int CountMeaningfulChars(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                count++;
            }
        }
        return count;
    }

    private static int CountDialogueChars(string text)
    {
        var total = 0;

        // Dialoghi tra caporali.
        total += SumMatches(text, "«[^»\\r\\n]{2,}»");
        // Dialoghi tra virgolette tipografiche.
        total += SumMatches(text, "“[^”\\r\\n]{2,}”");
        // Dialoghi tra doppi apici sulla stessa riga.
        total += SumMatches(text, "\"[^\"\\r\\n]{2,}\"");

        // Battute con trattino iniziale.
        var lines = text.Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("- ") || line.StartsWith("— ") || line.StartsWith("– "))
            {
                total += Math.Max(0, line.Length - 2);
            }
        }

        return total;
    }

    private static int SumMatches(string text, string pattern)
    {
        var sum = 0;
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.CultureInvariant))
        {
            if (match.Success)
            {
                sum += match.Length;
            }
        }
        return sum;
    }

    private static DeterministicResult Build(bool ok, string message, DateTime started)
        => new()
        {
            Successed = ok,
            Message = message,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
}
