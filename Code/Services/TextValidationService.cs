using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
        "chiude", "urla", "spinge", "colpisce", "trascina", "afferra",
        "avvicina", "avvicinava", "muove", "volta", "spegne", "accende", "alza",
        "tira", "scatta", "scivola", "sposta", "spostÚ", "protende", "getta",
        "lancia", "inclina", "attraversa", "ringhia", "emette", "riversa",
        "lampeggia", "brilla", "trema", "brucia", "risuona", "fissa", "mostra",
        "attiva", "disattiva",
        "avanza", "arretra", "assale", "attacca", "aziona", "blocca", "bracca",
        "carica", "catapulta", "cattura", "cede", "crolla", "decolla", "devia",
        "distrugge", "divora", "esplode", "evade", "falcia", "ferisce", "fende",
        "forza", "frena", "frantuma", "fruga", "fugge", "gira", "graffia",
        "impatta", "implode", "insegue", "irrompe", "lacera", "libera", "manovra",
        "massacra", "mena", "mira", "molla", "monta", "morde", "naviga",
        "neutralizza", "oscilla", "penetra", "perfora", "picchia", "piomba", "plana",
        "precipita", "preme", "punta", "reagisce", "recupera", "respinge", "retrocede",
        "rovescia", "rovina", "ruggisce", "salta", "sbanda", "sbatte", "sbuca",
        "scaglia", "scappa", "scardina", "scarta", "scavalca", "scende", "scontra",
        "scopre", "scoppia", "scuote", "sferra", "sfila", "sfonda", "sfugge",
        "sgancia", "slitta", "smonta", "smorza", "sorpassa", "sorregge", "spara",
        "spezza", "spalanca", "sprofonda", "stabilizza", "stacca", "sterza",
        "stordisce", "strappa", "stringe", "svanisce", "taglia", "tampona",
        "travolge", "trafigge", "urta", "vola"
    };

    private static readonly HashSet<string> ActionVerbKeys = ActionVerbs
        .Select(NormalizeActionVerbKey)
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);


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
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (Match token in Regex.Matches(text.ToLowerInvariant(), @"\p{L}+"))
        {
            var key = NormalizeActionVerbKey(token.Value);
            if (!string.IsNullOrWhiteSpace(key) && ActionVerbKeys.Contains(key))
            {
                return true;
            }
        }

        return false;
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
        return HasAnyActionVerb(sentence);
    }

    private static string NormalizeActionVerbKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var token = RemoveDiacritics(value.ToLowerInvariant().Trim());
        token = Regex.Replace(token, @"[^a-z]", string.Empty);
        if (token.Length == 0)
        {
            return string.Empty;
        }

        token = StripEncliticPronouns(token);
        token = StemItalianVerbToken(token);
        return token;
    }

    private static string StripEncliticPronouns(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 5)
        {
            return token;
        }

        string[] suffixes =
        {
            "gliele", "glieli", "gliela", "glielo", "gliene",
            "mene", "tene", "sene", "cene", "vene",
            "meli", "teli", "seli", "celi", "veli",
            "mela", "tela", "sela", "cela", "vela",
            "gli", "glie", "melo", "telo", "selo", "celo", "velo",
            "mi", "ti", "si", "ci", "vi", "lo", "la", "li", "le", "ne"
        };

        foreach (var suffix in suffixes)
        {
            if (token.Length > suffix.Length + 2 && token.EndsWith(suffix, StringComparison.Ordinal))
            {
                return token[..^suffix.Length];
            }
        }

        return token;
    }

    private static string StemItalianVerbToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 4)
        {
            return token;
        }

        string[] endings =
        {
            "erebbero", "irebbero", "assero", "essero", "issero",
            "arono", "erono", "irono", "avano", "evano", "ivano",
            "ando", "endo", "ammo", "emmo", "immo",
            "asti", "esti", "isti", "asse", "esse", "isse",
            "ato", "uto", "ito", "ata", "uta", "ita", "ati", "uti", "iti", "ate", "ute", "ite",
            "are", "ere", "ire", "ava", "eva", "iva", "ano", "ono",
            "era", "ira", "ara",
            "ai", "ei", "ii", "o", "a", "e", "i"
        };

        foreach (var ending in endings)
        {
            if (token.Length > ending.Length + 2 && token.EndsWith(ending, StringComparison.Ordinal))
            {
                return token[..^ending.Length];
            }
        }

        return token;
    }

    private static string RemoveDiacritics(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
    private static bool ContainsWord(string text, string word)
    {
        return Regex.IsMatch(text ?? string.Empty, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
    }
}

