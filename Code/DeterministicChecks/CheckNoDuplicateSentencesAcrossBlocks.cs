using System.Text.RegularExpressions;

namespace TinyGenerator.Services;

public sealed class CheckNoDuplicateSentencesAcrossBlocks : CheckBase
{
    public override string Rule => "Nessuna frase duplicata tra blocchi (history vs blocco corrente).";
    public override string GenericErrorDescription => "Frasi duplicate tra blocchi";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var historyText = GetOption("HistoryText", string.Empty);
        var minWords = Math.Max(1, GetOption("MinWords", 6));
        var similarityThreshold = GetOption("SimilarityThreshold", 0.92d);
        var ignoreCsv = GetOption("IgnoreSentencesCsv", string.Empty);

        if (string.IsNullOrWhiteSpace(historyText))
        {
            return Build(true, "ok", started);
        }

        var ignore = SplitSentences(ignoreCsv)
            .Select(NormalizeSentence)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.Ordinal);

        var historySentences = SplitSentences(historyText)
            .Select(NormalizeSentence)
            .Where(s => IsCandidateSentence(s, minWords, ignore))
            .ToList();

        if (historySentences.Count == 0)
        {
            return Build(true, "ok", started);
        }

        var historySet = historySentences.ToHashSet(StringComparer.Ordinal);
        var historyVectors = historySentences
            .Select(s => (Text: s, Tokens: ToTokenSet(s)))
            .Where(x => x.Tokens.Count > 0)
            .ToList();

        foreach (var currentRaw in SplitSentences(textToCheck))
        {
            var current = NormalizeSentence(currentRaw);
            if (!IsCandidateSentence(current, minWords, ignore))
            {
                continue;
            }

            if (historySet.Contains(current))
            {
                return Build(false, $"Frase duplicata (match esatto) gia' presente nella history: '{TrimForMessage(current)}'", started);
            }

            var currentTokens = ToTokenSet(current);
            if (currentTokens.Count == 0) continue;

            foreach (var past in historyVectors)
            {
                var j = Jaccard(currentTokens, past.Tokens);
                if (j >= similarityThreshold)
                {
                    return Build(false, $"Frase troppo simile a una gia' presente (Jaccard={j:0.00}): '{TrimForMessage(current)}'", started);
                }
            }
        }

        return Build(true, "ok", started);
    }

    private static IEnumerable<string> SplitSentences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var s in Regex.Split(text.Trim(), @"(?<=[\.\!\?])\s+|\r?\n+"))
        {
            var trimmed = s.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    private static string NormalizeSentence(string? sentence)
    {
        var s = (sentence ?? string.Empty).ToLowerInvariant().Trim();
        s = Regex.Replace(s, @"[^\p{L}\p{N}\s]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    private static bool IsCandidateSentence(string normalized, int minWords, HashSet<string> ignore)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (ignore.Contains(normalized)) return false;
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= minWords;
    }

    private static HashSet<string> ToTokenSet(string normalized)
    {
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Intersect(b, StringComparer.Ordinal).Count();
        var union = a.Union(b, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (double)inter / union;
    }

    private static string TrimForMessage(string value)
    {
        return value.Length <= 120 ? value : value[..120] + "...";
    }

    private static DeterministicResult Build(bool ok, string message, DateTime started)
        => new()
        {
            Successed = ok,
            Message = message,
            CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
        };
}
