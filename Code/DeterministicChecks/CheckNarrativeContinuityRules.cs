using System.Text.Json;
using System.Text.RegularExpressions;

namespace TinyGenerator.Services;

public sealed class CheckNarrativeContinuityRules : CheckBase
{
    public override string Rule => "Controlli deterministici continuity narrativa (7 regole).";
    public override string GenericErrorDescription => "Violazione continuity narrativa";

    public override IDeterministicResult Execute(string textToCheck)
    {
        var started = DateTime.UtcNow;
        var text = (textToCheck ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Build(false, "output narrativo vuoto", started);
        }

        var storyHistory = GetOption("StoryHistory", string.Empty);
        var continuityStateJson = GetOption("ContinuityStateJson", "{}");
        var currentPov = GetOption("CurrentPOV", string.Empty);

        var state = ParseState(continuityStateJson);

        // 1) no_dead_character_speaks
        var deadSpeaks = FindDeadCharacterSpeaking(text, state.DeadCharacters);
        if (!string.IsNullOrWhiteSpace(deadSpeaks))
        {
            return Build(false, $"no_dead_character_speaks: {deadSpeaks}", started);
        }

        // 2) no_location_jump_without_transition (euristica)
        if (LooksLikeLocationJumpWithoutTransition(text, state.LocationCurrent))
        {
            return Build(false, "no_location_jump_without_transition: cambio luogo improvviso senza marker", started);
        }

        // 3) no_object_appears_without_introduction (euristica soft: solo oggetti esplicitamente marcati)
        var objectViolation = FindObjectAppearsWithoutIntroduction(text, state.ObjectsInScene);
        if (!string.IsNullOrWhiteSpace(objectViolation))
        {
            return Build(false, $"no_object_appears_without_introduction: {objectViolation}", started);
        }

        // 4) no_event_repetition_last_3_paragraphs
        if (HasParagraphRepetitionAgainstRecentHistory(text, storyHistory))
        {
            return Build(false, "no_event_repetition_last_3_paragraphs: paragrafi troppo simili al contesto recente", started);
        }

        // 5) no_pov_shift_without_marker (euristica)
        if (HasPovShiftWithoutMarker(text, currentPov))
        {
            return Build(false, "no_pov_shift_without_marker: shift POV implicito senza marker", started);
        }

        // 6) environment_consistency (euristica minima)
        if (HasEnvironmentInconsistency(text, state.EnvironmentTokens))
        {
            return Build(false, "environment_consistency: dettagli ambientali in conflitto con stato", started);
        }

        // 7) no_duplicate_dialogue_lines
        if (HasDuplicateDialogueLines(text))
        {
            return Build(false, "no_duplicate_dialogue_lines: battuta duplicata nello stesso blocco", started);
        }

        return Build(true, "ok", started);
    }

    private static DeterministicResult Build(bool ok, string message, DateTime started) => new()
    {
        Successed = ok,
        Message = message,
        CheckDurationMs = Math.Max(0, (long)(DateTime.UtcNow - started).TotalMilliseconds)
    };

    private sealed record ParsedContinuityState(
        string? LocationCurrent,
        HashSet<string> DeadCharacters,
        HashSet<string> ObjectsInScene,
        HashSet<string> EnvironmentTokens);

    private static ParsedContinuityState ParseState(string? json)
    {
        var dead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var env = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? location = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return new ParsedContinuityState(location, dead, objects, env);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ParsedContinuityState(location, dead, objects, env);
            }

            if (doc.RootElement.TryGetProperty("location_current", out var loc) && loc.ValueKind == JsonValueKind.String)
            {
                location = loc.GetString();
            }

            if (doc.RootElement.TryGetProperty("dead_characters", out var d) && d.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in d.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        AddNameTokens(dead, item.GetString());
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("objects_in_scene", out var o) && o.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in o.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        AddNameTokens(objects, item.GetString());
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("environment_state", out var e))
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var p in e.EnumerateObject())
                        {
                            env.Add(NormalizeToken(p.Name));
                            if (p.Value.ValueKind == JsonValueKind.String)
                            {
                                foreach (var t in Tokenize(p.Value.GetString()))
                                {
                                    env.Add(t);
                                }
                            }
                        }
                        break;
                    case JsonValueKind.String:
                        foreach (var t in Tokenize(e.GetString()))
                        {
                            env.Add(t);
                        }
                        break;
                }
            }
        }
        catch
        {
            // best-effort parser
        }

        return new ParsedContinuityState(location, dead, objects, env);
    }

    private static void AddNameTokens(HashSet<string> target, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (var t in Tokenize(raw))
        {
            if (t.Length >= 2)
            {
                target.Add(t);
            }
        }
    }

    private static string? FindDeadCharacterSpeaking(string text, HashSet<string> deadCharacters)
    {
        if (deadCharacters.Count == 0) return null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0 || colon > 40) continue;
            var speaker = trimmed[..colon].Trim();
            foreach (var token in Tokenize(speaker))
            {
                if (deadCharacters.Contains(token))
                {
                    return $"personaggio morto parla: '{speaker}'";
                }
            }
        }
        return null;
    }

    private static bool LooksLikeLocationJumpWithoutTransition(string text, string? currentLocation)
    {
        if (string.IsNullOrWhiteSpace(currentLocation)) return false;
        var t = text.ToLowerInvariant();
        var hasTransition = t.Contains("poi ") || t.Contains("dopo ") || t.Contains("nel frattempo")
                            || t.Contains("più tardi") || t.Contains("intanto");
        if (hasTransition) return false;

        // Heuristic: explicit abrupt relocation expressions
        return t.Contains("si ritrovò in ") || t.Contains("improvvisamente era in ")
               || t.Contains("si trovava già in ");
    }

    private static string? FindObjectAppearsWithoutIntroduction(string text, HashSet<string> knownObjects)
    {
        if (knownObjects.Count == 0) return null;
        // Heuristic only on explicit "estrasse/impugnò/raccolse" patterns
        var matches = Regex.Matches(text, @"\b(estrasse|impugnò|raccolse|affer[r]?ò)\s+([A-Za-zÀ-ÿ_]+)", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            if (m.Groups.Count < 3) continue;
            var obj = NormalizeToken(m.Groups[2].Value);
            if (!string.IsNullOrWhiteSpace(obj) && !knownObjects.Contains(obj))
            {
                return $"oggetto '{obj}' usato senza introduzione nello stato";
            }
        }
        return null;
    }

    private static bool HasParagraphRepetitionAgainstRecentHistory(string text, string storyHistory)
    {
        var currentParagraphs = SplitParagraphs(text).TakeLast(3).ToList();
        if (currentParagraphs.Count == 0 || string.IsNullOrWhiteSpace(storyHistory)) return false;

        var historyParagraphs = SplitParagraphs(storyHistory).TakeLast(3).ToList();
        if (historyParagraphs.Count == 0) return false;

        foreach (var cp in currentParagraphs)
        {
            var nc = NormalizeSentence(cp);
            if (nc.Length < 24) continue;
            foreach (var hp in historyParagraphs)
            {
                var nh = NormalizeSentence(hp);
                if (nh.Length < 24) continue;
                if (nc == nh) return true;
                if (Jaccard(nc, nh) >= 0.90) return true;
            }
        }
        return false;
    }

    private static bool HasPovShiftWithoutMarker(string text, string currentPov)
    {
        if (string.IsNullOrWhiteSpace(currentPov)) return false;
        var t = text.ToLowerInvariant();
        var hasPovMarker = t.Contains("[pov") || t.Contains("pov:");
        if (hasPovMarker) return false;

        // Heuristic: strong 1st-person singular signals while POV is explicitly another named character.
        var looksFirstPerson = Regex.IsMatch(t, @"\b(io|mi|mio|mia|me)\b");
        var povLooksNamed = Tokenize(currentPov).Any(tok => tok.Length >= 3 && tok != "io");
        return looksFirstPerson && povLooksNamed;
    }

    private static bool HasEnvironmentInconsistency(string text, HashSet<string> envTokens)
    {
        if (envTokens.Count == 0) return false;
        var t = text.ToLowerInvariant();
        var saysIndoor = envTokens.Contains("indoor") || envTokens.Contains("inside") || envTokens.Contains("interno");
        var saysOutdoor = envTokens.Contains("outdoor") || envTokens.Contains("outside") || envTokens.Contains("esterno");
        if (saysIndoor && (t.Contains("sotto la pioggia") || t.Contains("cielo aperto")))
        {
            return true;
        }
        if (saysOutdoor && (t.Contains("nel corridoio chiuso") || t.Contains("stanza senza finestre")))
        {
            return true;
        }
        return false;
    }

    private static bool HasDuplicateDialogueLines(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0 || colon > 40) continue;
            var dialogue = NormalizeSentence(trimmed[(colon + 1)..]);
            if (dialogue.Length < 8) continue;
            if (!seen.Add(dialogue))
            {
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> SplitParagraphs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var p in Regex.Split(text, @"(?:\r?\n){2,}"))
        {
            var t = p.Trim();
            if (t.Length > 0) yield return t;
        }
    }

    private static string NormalizeSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lower = text.ToLowerInvariant();
        lower = Regex.Replace(lower, @"[^\p{L}\p{N}\s]", " ");
        lower = Regex.Replace(lower, @"\s+", " ").Trim();
        return lower;
    }

    private static IEnumerable<string> Tokenize(string? text)
    {
        var norm = NormalizeSentence(text);
        if (string.IsNullOrWhiteSpace(norm)) return Enumerable.Empty<string>();
        return norm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeToken)
            .Where(t => !string.IsNullOrWhiteSpace(t));
    }

    private static string NormalizeToken(string? token)
    {
        var t = (token ?? string.Empty).Trim().ToLowerInvariant();
        t = t.Replace("_", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.EndsWith("s") && t.Length > 4) t = t[..^1];
        return t;
    }

    private static double Jaccard(string a, string b)
    {
        var sa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sa.Count == 0 || sb.Count == 0) return 0;
        var inter = sa.Intersect(sb, StringComparer.OrdinalIgnoreCase).Count();
        var union = sa.Union(sb, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)inter / union;
    }
}
