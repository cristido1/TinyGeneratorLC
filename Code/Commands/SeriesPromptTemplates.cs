using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

internal static class SeriesPromptTemplates
{
    private static readonly Dictionary<string, string> DefaultTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        [CommandRoleCodes.SerieBibleAgent] = """
Genera la bibbia della serie. Usa SOLO TAG.

PROMPT:
{{USER_PROMPT}}
""",
        [CommandRoleCodes.SerieCharacterAgent] = """
Genera personaggi e relazioni. Usa SOLO TAG.

SERIE_BIBLE:
{{SERIE_BIBLE}}
""",
        [CommandRoleCodes.SerieSeasonAgent] = """
Genera la struttura base degli episodi. Usa SOLO TAG.

SERIE_BIBLE:
{{SERIE_BIBLE}}

CHARACTERS:
{{CHARACTERS}}
""",
        [CommandRoleCodes.SerieEpisodeAgent] = """
Genera la struttura dettagliata dell'episodio. Usa SOLO TAG.

EPISODE_BASE:
{{EPISODE_BASE}}

SERIE_BIBLE:
{{SERIE_BIBLE}}

CHARACTERS:
{{CHARACTERS}}
""",
        [CommandRoleCodes.SerieValidatorAgent] = """
Valida i tag della serie. Usa SOLO TAG. Output [VALIDATION_OK] o [VALIDATION_ERROR].

SERIE_BIBLE:
{{SERIE_BIBLE}}

CHARACTERS:
{{CHARACTERS}}

EPISODES_BASE:
{{EPISODES_BASE}}

EPISODES_STRUCT:
{{EPISODES_STRUCT}}
"""
    };

    public static readonly IReadOnlyDictionary<string, string[]> RequiredPlaceholdersByRole =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [CommandRoleCodes.SerieBibleAgent] = new[] { "USER_PROMPT" },
            [CommandRoleCodes.SerieCharacterAgent] = new[] { "SERIE_BIBLE" },
            [CommandRoleCodes.SerieSeasonAgent] = new[] { "SERIE_BIBLE", "CHARACTERS" },
            [CommandRoleCodes.SerieEpisodeAgent] = new[] { "EPISODE_BASE", "SERIE_BIBLE", "CHARACTERS" },
            [CommandRoleCodes.SerieValidatorAgent] = new[] { "SERIE_BIBLE", "CHARACTERS", "EPISODES_BASE", "EPISODES_STRUCT" }
        };

    private static readonly Regex PlaceholderRegex = new(@"\{\{\s*([A-Z0-9_]+)\s*\}\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string ComposePrompt(Agent agent, string roleCode, IReadOnlyDictionary<string, string> placeholders)
    {
        var template = string.IsNullOrWhiteSpace(agent.Prompt)
            ? GetDefaultTemplate(roleCode)
            : agent.Prompt!;

        var replaced = ReplacePlaceholders(template, placeholders);
        if (!string.IsNullOrWhiteSpace(replaced))
        {
            return replaced.Trim();
        }

        // Last-resort fallback for malformed templates.
        var fallback = GetDefaultTemplate(roleCode);
        foreach (var pair in placeholders)
        {
            fallback = fallback.Replace($"{{{{{pair.Key}}}}}", pair.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return fallback.Trim();
    }

    public static string GetDefaultTemplate(string roleCode)
    {
        if (DefaultTemplates.TryGetValue(roleCode, out var template))
        {
            return template;
        }

        return "{{USER_PROMPT}}";
    }

    private static string ReplacePlaceholders(string template, IReadOnlyDictionary<string, string> placeholders)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        return PlaceholderRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            return placeholders.TryGetValue(key, out var value)
                ? value ?? string.Empty
                : string.Empty;
        });
    }
}
