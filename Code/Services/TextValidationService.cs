using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TinyGenerator.Services;

public sealed record TextValidationResult(bool IsValid, string? Reason = null)
{
    public static readonly TextValidationResult Valid = new(true, null);
    public static TextValidationResult Invalid(string reason) => new(false, reason);
}

public sealed class TextValidationService
{
    private readonly IOptionsMonitor<StateDrivenStoryGenerationOptions> _options;

    private static readonly Regex[] DegenerativePunctuationPatterns =
    {
        new(@"[ó-]{1,3}[?!]{1,3}", RegexOptions.Compiled),
        new(@"\.{3,}", RegexOptions.Compiled),
        new(@"!{2,}", RegexOptions.Compiled),
        new(@"\?{2,}", RegexOptions.Compiled)
    };

    private static readonly Regex[] DialogueQuotePatterns =
    {
        new(@"‚Äú([^‚Äù]+)‚Äù", RegexOptions.Compiled),
        new("\"([^\"]+)\"", RegexOptions.Compiled)
    };

    private static readonly string[] EmotiveKeywords =
    {
        "cuore", "respiro", "tremare", "paura", "ansia", "tensione", "silenzio",
        "sentiva", "pensava", "ricordava", "guardava", "fissava"
    };

    private static readonly string[] ActionVerbs =
    {
        "entra", "esce", "corre", "prende", "lascia", "rompe", "cade", "apre",
        "chiude", "urla", "spinge", "colpisce", "trascina", "afferra"
    };

    public TextValidationService(IOptionsMonitor<StateDrivenStoryGenerationOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public TextValidationResult Validate(string chunk, string history)
    {
        if (string.IsNullOrWhiteSpace(chunk))
        {
            return TextValidationResult.Valid;
        }

        var options = _options.CurrentValue;

        if (options.EnableDegenerativePunctuationCheck && !HasAcceptablePunctuation(chunk, options.MaxDegenerativePunctuationMatches))
        {
            return TextValidationResult.Invalid("punteggiatura degenerativa");
        }

        if (options.EnableDialogueLoopCheck && HasRepeatedDialogues(chunk))
        {
            return TextValidationResult.Invalid("loop di battute identiche");
        }

        var sentences = ExtractSentences(chunk);

        if (options.EnableEmotionalLoopCheck && HasEmotionalLoop(sentences))
        {
            return TextValidationResult.Invalid("loop emotivo / stasi");
        }

        if (options.EnableActionPresenceCheck && !HasAnyActionVerb(chunk))
        {
            return TextValidationResult.Invalid("assenza di eventi reali");
        }

        if (options.EnableSimilarSentenceRepetitionCheck &&
            HasSimilarSentenceRepetition(sentences, options.SimilarSentenceSimilarityThreshold, options.SimilarSentenceRepeatLimit))
        {
            return TextValidationResult.Invalid("ripetizioni frasi simili");
        }

        if (options.EnableParagraphActionGapCheck && HasParagraphActionGap(chunk, options.ParagraphActionGapThreshold))
        {
            return TextValidationResult.Invalid("troppi paragrafi senza azione");
        }

        if (options.EnableHistoricalLoopDetection &&
            HasHistoricalLoop(sentences, history, options.HistoricalLoopSimilarityThreshold, options.HistoricalLoopRepeatThreshold, options.HistoricalLoopRepeatRatioThreshold, options.HistoricalLoopHistorySentenceCount))
        {
            return TextValidationResult.Invalid("loop narrativo");
        }

        return TextValidationResult.Valid;
    }

    private static bool HasAcceptablePunctuation(string chunk, int maxMatches)
    {
        var total = 0;
        foreach (var regex in DegenerativePunctuationPatterns)
        {
            total += regex.Matches(chunk).Count;
            if (total > maxMatches) return false;
        }
        return true;
    }

    private static bool HasRepeatedDialogues(string chunk)
    {
        var lines = chunk.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("ó") || trimmed.StartsWith("-"))
            {
                if (RegisterAndCheckLoop(normalized, trimmed.TrimStart('ó', '-', ' ')))
                {
                    return true;
                }
            }

            foreach (var pattern in DialogueQuotePatterns)
            {
                foreach (Match match in pattern.Matches(trimmed))
                {
                    if (RegisterAndCheckLoop(normalized, match.Groups[1].Value))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool RegisterAndCheckLoop(Dictionary<string, int> normalized, string snippet)
    {
        var candidate = NormalizeDialogSnippet(snippet);
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        normalized[candidate] = normalized.TryGetValue(candidate, out var count) ? count + 1 : 1;
        return normalized[candidate] >= 2;
    }

    private static string NormalizeDialogSnippet(string snippet)
    {
        var lower = snippet.ToLowerInvariant();
        lower = Regex.Replace(lower, @"\s+", " ");
        lower = Regex.Replace(lower, @"[\.!\?,;:\-]+$", string.Empty);
        return lower.Trim();
    }

    private static bool HasEmotionalLoop(List<string> sentences)
    {
        if (sentences.Count < 3) return false;
        for (var i = 0; i <= sentences.Count - 3; i++)
        {
            if (!HasEmotiveKeyword(sentences[i]) || HasActionVerb(sentences[i])) continue;
            if (!HasEmotiveKeyword(sentences[i + 1]) || HasActionVerb(sentences[i + 1])) continue;
            if (!HasEmotiveKeyword(sentences[i + 2]) || HasActionVerb(sentences[i + 2])) continue;
            return true;
        }
        return false;
    }

    private static bool HasAnyActionVerb(string text)
    {
        return ActionVerbs.Any(verb => ContainsWord(text, verb));
    }

    private static bool HasSimilarSentenceRepetition(List<string> sentences, double threshold, int limit)
    {
        if (sentences.Count < 2) return false;
        var repeats = 0;
        for (var i = 0; i < sentences.Count; i++)
        {
            var baseSet = NormalizeWords(sentences[i]);
            if (baseSet.Count == 0) continue;
            for (var offset = 1; offset <= 2; offset++)
            {
                if (i + offset >= sentences.Count) break;
                var compareSet = NormalizeWords(sentences[i + offset]);
                if (compareSet.Count == 0) continue;
                var jaccard = ComputeJaccard(baseSet, compareSet);
                if (jaccard > threshold)
                {
                    repeats++;
                    break;
                }
            }
            if (repeats > limit) return true;
        }
        return false;
    }

    private static bool HasParagraphActionGap(string chunk, int threshold)
    {
        var paragraphs = Regex.Split(chunk.Trim(), @"\r?\n\s*\r?\n")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var consecutive = 0;
        foreach (var paragraph in paragraphs)
        {
            if (HasAnyActionVerb(paragraph))
            {
                consecutive = 0;
                continue;
            }

            consecutive++;
            if (consecutive >= threshold)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasHistoricalLoop(List<string> chunkSentences, string history, double similarityThreshold, int repeatThreshold, double repeatRatioThreshold, int historySentenceCount)
    {
        if (chunkSentences.Count == 0) return false;

        var historySentences = ExtractSentences(history);
        if (historySentences.Count == 0) return false;

        var relevantHistory = historySentences
            .Skip(Math.Max(0, historySentences.Count - historySentenceCount))
            .ToList();

        if (relevantHistory.Count == 0) return false;

        var historyVectors = relevantHistory
            .Select(BuildNormalizedTermVector)
            .Where(v => v.Count > 0)
            .ToList();

        if (historyVectors.Count == 0) return false;

        var repeatCount = 0;
        foreach (var sentence in chunkSentences)
        {
            var vector = BuildNormalizedTermVector(sentence);
            if (vector.Count == 0) continue;

            foreach (var prevVector in historyVectors)
            {
                if (ComputeCosineSimilarity(vector, prevVector) >= similarityThreshold)
                {
                    repeatCount++;
                    break;
                }
            }
        }

        if (repeatCount == 0) return false;
        if (repeatCount >= repeatThreshold) return true;
        var ratio = (double)repeatCount / chunkSentences.Count;
        return ratio >= repeatRatioThreshold;
    }

    private static List<string> ExtractSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        var sentences = Regex.Split(text.Trim(), @"(?<=[\.!?])\s+");
        return sentences
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static Dictionary<string, double> BuildNormalizedTermVector(string sentence)
    {
        var tokens = Regex.Matches(sentence.ToLowerInvariant(), @"\p{L}+");
        var counts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (Match token in tokens)
        {
            var term = token.Value;
            if (string.IsNullOrWhiteSpace(term)) continue;
            counts[term] = counts.TryGetValue(term, out var current) ? current + 1 : 1;
        }

        var norm = Math.Sqrt(counts.Values.Sum(v => v * v));
        if (norm > 0)
        {
            var keys = counts.Keys.ToList();
            foreach (var key in keys)
            {
                counts[key] /= norm;
            }
        }

        return counts;
    }

    private static double ComputeCosineSimilarity(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var smaller = a.Count <= b.Count ? a : b;
        var larger = smaller == a ? b : a;
        double dot = 0;
        foreach (var kv in smaller)
        {
            if (larger.TryGetValue(kv.Key, out var value))
            {
                dot += kv.Value * value;
            }
        }
        return dot;
    }

    private static HashSet<string> NormalizeWords(string sentence)
    {
        var tokens = Regex.Matches(sentence.ToLowerInvariant(), @"\p{L}+");
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match token in tokens)
        {
            if (token.Success && token.Value.Length > 0)
            {
                set.Add(token.Value);
            }
        }

        return set;
    }

    private static double ComputeJaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static bool HasEmotiveKeyword(string sentence)
    {
        return EmotiveKeywords.Any(keyword => ContainsWord(sentence, keyword));
    }

    private static bool HasActionVerb(string sentence)
    {
        return ActionVerbs.Any(verb => ContainsWord(sentence, verb));
    }

    private static bool ContainsWord(string text, string word)
    {
        return Regex.IsMatch(text ?? string.Empty, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
    }
}

