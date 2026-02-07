using System;
using System.Collections.Generic;
using System.Text;

namespace TinyGenerator.Services;

internal static class CommandOperationNameResolver
{
    private static readonly Dictionary<string, string[]> CanonicalAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["generate_series_episode"] = new[] { "series_episode" },
        ["state_driven_episode_auto"] = new[] { "state_driven_series_episode_auto" }
    };

    private static readonly Dictionary<string, string> AliasToCanonical = BuildAliasToCanonical();

    public static IEnumerable<string> GetLookupKeys(string? raw)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return keys;
        }

        var trimmed = raw.Trim();
        keys.Add(trimmed);

        var normalized = Normalize(trimmed);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            keys.Add(normalized);

            // Support scoped operation names like "instruction_score/<model>" by
            // also exposing the base command key ("instruction_score") for policy lookup.
            var slashIdx = normalized.IndexOf('/');
            if (slashIdx > 0)
            {
                var baseKey = normalized.Substring(0, slashIdx);
                if (!string.IsNullOrWhiteSpace(baseKey))
                {
                    keys.Add(baseKey);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(normalized) && AliasToCanonical.TryGetValue(normalized, out var canonical))
        {
            keys.Add(canonical);
        }

        if (!string.IsNullOrWhiteSpace(normalized) && CanonicalAliases.TryGetValue(normalized, out var aliases))
        {
            foreach (var alias in aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    keys.Add(alias);
                }
            }
        }

        return keys;
    }

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim();
        if (value.IndexOf('/') >= 0)
        {
            return value.ToLowerInvariant();
        }

        var sb = new StringBuilder(value.Length + 8);
        char prev = '\0';
        foreach (var c in value)
        {
            if (char.IsUpper(c))
            {
                if (sb.Length > 0 && prev != '_' && (char.IsLower(prev) || char.IsDigit(prev)))
                {
                    sb.Append('_');
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                if (sb.Length > 0 && prev != '_')
                {
                    sb.Append('_');
                }
            }

            prev = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
        }

        return sb.ToString().Trim('_');
    }

    private static Dictionary<string, string> BuildAliasToCanonical()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in CanonicalAliases)
        {
            foreach (var alias in pair.Value)
            {
                if (!string.IsNullOrWhiteSpace(alias) && !map.ContainsKey(alias))
                {
                    map[alias] = pair.Key;
                }
            }
        }

        return map;
    }
}
