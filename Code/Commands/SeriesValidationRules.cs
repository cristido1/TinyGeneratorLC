using System;
using System.Collections.Generic;
using System.Linq;

namespace TinyGenerator.Services.Commands;

internal sealed class SeriesValidationRules
{
    private readonly SeriesTagParser _parser;
    private readonly SeriesGenerationOptions _options;

    public static readonly IReadOnlyCollection<string> RequiredBibleTags = new[]
    {
        "SERIES_TITLE",
        "SERIES_GENRE",
        "SERIES_TONE",
        "SERIES_AUDIENCE",
        "SERIES_RATING",
        "SERIES_PREMISE",
        "SERIES_THEMES",
        "SERIES_FORBIDDEN_TOPICS",
        "SETTING_PLACE",
        "SETTING_TIME",
        "SETTING_TECH_LEVEL",
        "SETTING_WORLD_RULES",
        "FORMAT_EPISODE_DURATION",
        "FORMAT_SEASON_EPISODES",
        "FORMAT_STRUCTURE",
        "FORMAT_SERIALIZED_LEVEL",
        "CANON_FACTS",
        "RECURRING_LOCATIONS",
        "SEASON_LOGLINE",
        "SEASON_MAIN_ANTAGONISM",
        "SEASON_MID_TWIST",
        "SEASON_FINALE_PAYOFF"
    };

    public static readonly IReadOnlyCollection<string> RequiredCharacterTags = new[]
    {
        "CHARACTER",
        "RELATIONSHIP",
        "CHARACTER_SEASON_ARC"
    };

    public static readonly IReadOnlyCollection<string> RequiredSeasonTags = new[]
    {
        "EPISODE"
    };

    public static readonly IReadOnlyCollection<string> RequiredEpisodeStructureTags = new[]
    {
        "EPISODE_STRUCTURE",
        "BEAT",
        "CAST",
        "LOCATIONS",
        "SETUP",
        "PAYOFF"
    };

    public static readonly IReadOnlyCollection<string> RequiredValidatorTags = new[]
    {
        "VALIDATION_OK",
        "VALIDATION_ERROR"
    };

    public SeriesValidationRules(SeriesTagParser parser, SeriesGenerationOptions options)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool HasRequiredTags(string text, IReadOnlyCollection<string> requiredTags, out List<string> missingTags)
    {
        missingTags = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (requiredTags == null || requiredTags.Count == 0)
        {
            return true;
        }

        if (requiredTags.Contains("VALIDATION_OK") && requiredTags.Contains("VALIDATION_ERROR"))
        {
            return text.Contains("[VALIDATION_OK]", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("[VALIDATION_ERROR]", StringComparison.OrdinalIgnoreCase);
        }

        var tags = _parser.CollectTags(text);
        foreach (var tag in requiredTags)
        {
            if (!tags.Contains(tag))
            {
                missingTags.Add(tag);
            }
        }

        return missingTags.Count == 0;
    }

    public string? ValidateBibleOutput(string text)
    {
        if (!_options.Validation.EnableBibleValidation)
        {
            return null;
        }

        var blocks = _parser.ParseTagBlocks(text);
        foreach (var tag in RequiredBibleTags)
        {
            var block = blocks.FirstOrDefault(b => b.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
            if (block == null || block.Lines.Count == 0 || block.Lines.All(l => string.IsNullOrWhiteSpace(l)))
            {
                return $"Output bible incompleto: tag [{tag}] mancante o vuoto";
            }
        }

        if (_parser.GetListTag(blocks, "SERIES_GENRE").Count == 0)
        {
            return "Output bible incompleto: [SERIES_GENRE] deve contenere almeno 1 valore";
        }

        if (_parser.GetListTag(blocks, "SERIES_THEMES").Count == 0)
        {
            return "Output bible incompleto: [SERIES_THEMES] deve contenere almeno 1 valore";
        }

        return null;
    }

    public string? ValidateCharactersOutput(string text)
    {
        if (!_options.Validation.EnableCharactersValidation)
        {
            return null;
        }

        var blocks = _parser.ParseTagBlocks(text);
        var characterKvs = blocks.Where(b => b.Tag.Equals("CHARACTER", StringComparison.OrdinalIgnoreCase))
            .Select(b => _parser.ParseKeyValues(b.Lines))
            .ToList();

        if (characterKvs.Count == 0)
        {
            return "Output characters incompleto: nessun blocco [CHARACTER]";
        }

        var ids = new List<string>();
        foreach (var kv in characterKvs)
        {
            if (!kv.TryGetValue("ID", out var id) || string.IsNullOrWhiteSpace(id))
            {
                return "Output characters incompleto: ogni [CHARACTER] deve avere ID:";
            }

            if (!kv.TryGetValue("NAME", out var name) || string.IsNullOrWhiteSpace(name))
            {
                return "Output characters incompleto: ogni [CHARACTER] deve avere NAME:";
            }

            ids.Add(id.Trim());
        }

        var dupId = ids.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (!string.IsNullOrWhiteSpace(dupId))
        {
            return $"Output characters non valido: ID personaggio duplicato: {dupId}";
        }

        if (_options.Validation.ValidateCharacterReferences)
        {
            var idSet = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            foreach (var rel in blocks.Where(b => b.Tag.Equals("RELATIONSHIP", StringComparison.OrdinalIgnoreCase)))
            {
                var kv = _parser.ParseKeyValues(rel.Lines);
                if (!kv.TryGetValue("FROM", out var from) || string.IsNullOrWhiteSpace(from))
                {
                    return "Output relationship incompleto: FROM:";
                }

                if (!kv.TryGetValue("TO", out var to) || string.IsNullOrWhiteSpace(to))
                {
                    return "Output relationship incompleto: TO:";
                }

                if (!idSet.Contains(from.Trim()))
                {
                    return $"Output relationship non valido: FROM id sconosciuto: {from}";
                }

                if (!idSet.Contains(to.Trim()))
                {
                    return $"Output relationship non valido: TO id sconosciuto: {to}";
                }
            }

            foreach (var arc in blocks.Where(b => b.Tag.Equals("CHARACTER_SEASON_ARC", StringComparison.OrdinalIgnoreCase)))
            {
                var kv = _parser.ParseKeyValues(arc.Lines);
                if (!kv.TryGetValue("ID", out var id) || string.IsNullOrWhiteSpace(id))
                {
                    return "Output season arc incompleto: ID:";
                }

                var idRef = id.Trim();
                var fromRef = kv.TryGetValue("FROM", out var fromValue) ? fromValue?.Trim() : null;

                var idMatchesCharacter = !string.IsNullOrWhiteSpace(idRef) && idSet.Contains(idRef);
                var fromMatchesCharacter = !string.IsNullOrWhiteSpace(fromRef) && idSet.Contains(fromRef);
                if (!idMatchesCharacter && !fromMatchesCharacter)
                {
                    return $"Output season arc non valido: riferimento personaggio sconosciuto (ID={id}, FROM={fromRef})";
                }
            }
        }

        return null;
    }

    public string? ValidateSeasonOutput(string text)
    {
        if (!_options.Validation.EnableSeasonValidation)
        {
            return null;
        }

        var blocks = _parser.ParseTagBlocks(text);
        var episodes = blocks.Where(b => b.Tag.Equals("EPISODE", StringComparison.OrdinalIgnoreCase)).ToList();
        if (episodes.Count == 0)
        {
            return "Output season incompleto: nessun blocco [EPISODE]";
        }

        var numbers = new HashSet<int>();
        foreach (var ep in episodes)
        {
            var kv = _parser.ParseKeyValues(ep.Lines);
            if (!kv.TryGetValue("NUMBER", out var n) || string.IsNullOrWhiteSpace(n))
            {
                return "Output season incompleto: EPISODE senza NUMBER:";
            }

            var number = SeriesTagParser.TryParseInt(n);
            if (number <= 0)
            {
                return "Output season non valido: EPISODE NUMBER deve essere un intero > 0";
            }

            if (_options.Validation.ValidateDuplicateEpisodeNumbers && !numbers.Add(number))
            {
                return $"Output season non valido: EPISODE NUMBER duplicato: {number}";
            }

            if (!kv.TryGetValue("TITLE", out var title) || string.IsNullOrWhiteSpace(title))
            {
                return $"Output season incompleto: EPISODE {number} senza TITLE:";
            }

            if (!kv.TryGetValue("LOGLINE", out var logline) || string.IsNullOrWhiteSpace(logline))
            {
                return $"Output season incompleto: EPISODE {number} senza LOGLINE:";
            }

            if (!kv.TryGetValue("A_PLOT", out var a) || string.IsNullOrWhiteSpace(a))
            {
                return $"Output season incompleto: EPISODE {number} senza A_PLOT:";
            }

            if (!kv.TryGetValue("B_PLOT", out var b) || string.IsNullOrWhiteSpace(b))
            {
                return $"Output season incompleto: EPISODE {number} senza B_PLOT:";
            }

            if (!kv.TryGetValue("THEME", out var th) || string.IsNullOrWhiteSpace(th))
            {
                return $"Output season incompleto: EPISODE {number} senza THEME:";
            }
        }

        return null;
    }

    public string? ValidateEpisodeStructureOutput(string text, int expectedEpisodeNumber)
    {
        if (!_options.Validation.EnableEpisodeStructureValidation)
        {
            return null;
        }

        var blocks = _parser.ParseTagBlocks(text);
        var map = _parser.ParseEpisodeStructures(blocks);
        if (!map.TryGetValue(expectedEpisodeNumber, out var structure))
        {
            return $"Output episode_structure incompleto: [EPISODE_STRUCTURE] NUMBER deve essere {expectedEpisodeNumber}";
        }

        var minBeats = Math.Max(1, _options.Validation.MinEpisodeBeats);
        if (structure.Beats.Count < minBeats)
        {
            return $"Output episode_structure incompleto: episodio {expectedEpisodeNumber} deve avere almeno {minBeats} [BEAT]";
        }

        if (structure.Beats.Any(b => string.IsNullOrWhiteSpace(b.Type) || string.IsNullOrWhiteSpace(b.Summary)))
        {
            return $"Output episode_structure incompleto: ogni [BEAT] deve avere TYPE e SUMMARY (episodio {expectedEpisodeNumber})";
        }

        if (structure.Cast.Count == 0)
        {
            return $"Output episode_structure incompleto: [CAST] vuoto (episodio {expectedEpisodeNumber})";
        }

        if (structure.Locations.Count == 0)
        {
            return $"Output episode_structure incompleto: [LOCATIONS] vuoto (episodio {expectedEpisodeNumber})";
        }

        if (structure.Setup.Count == 0)
        {
            return $"Output episode_structure incompleto: [SETUP] vuoto (episodio {expectedEpisodeNumber})";
        }

        if (structure.Payoff.Count == 0)
        {
            return $"Output episode_structure incompleto: [PAYOFF] vuoto (episodio {expectedEpisodeNumber})";
        }

        return null;
    }

    public bool RequiresValidationOkTag()
        => _options.Validation.RequireValidationOkTag;
}
