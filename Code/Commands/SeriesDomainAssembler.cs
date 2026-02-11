using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using TinyGenerator.Models;

namespace TinyGenerator.Services.Commands;

internal sealed class SeriesDomainAssembler
{
    private readonly SeriesTagParser _parser;

    public SeriesDomainAssembler(SeriesTagParser parser)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    public SeriesBuildResult BuildSeriesData(string bibleTags, string characterTags, string seasonTags, string episodeStructures)
    {
        var bibleBlocks = _parser.ParseTagBlocks(bibleTags);
        var characterBlocks = _parser.ParseTagBlocks(characterTags);
        var seasonBlocks = _parser.ParseTagBlocks(seasonTags);
        var episodeBlocks = _parser.ParseTagBlocks(episodeStructures);

        var series = new Series
        {
            Titolo = _parser.GetSingleTag(bibleBlocks, "SERIES_TITLE") ?? "Untitled Series",
            Genere = JoinList(_parser.GetListTag(bibleBlocks, "SERIES_GENRE")),
            TonoBase = _parser.GetSingleTag(bibleBlocks, "SERIES_TONE"),
            Target = _parser.GetSingleTag(bibleBlocks, "SERIES_AUDIENCE"),
            PremessaSerie = _parser.GetSingleTag(bibleBlocks, "SERIES_PREMISE"),
            AmbientazioneBase = BuildAmbientazione(_parser.GetSingleTag(bibleBlocks, "SETTING_PLACE"), _parser.GetSingleTag(bibleBlocks, "SETTING_TIME")),
            PeriodoNarrativo = _parser.GetSingleTag(bibleBlocks, "SETTING_TIME"),
            LivelloTecnologicoMedio = _parser.GetSingleTag(bibleBlocks, "SETTING_TECH_LEVEL"),
            RegoleNarrative = JoinList(_parser.GetListTag(bibleBlocks, "SETTING_WORLD_RULES")) ?? _parser.GetSingleTag(bibleBlocks, "SETTING_WORLD_RULES"),
            TemiObbligatori = JoinList(_parser.GetListTag(bibleBlocks, "SERIES_THEMES")),
            CosaNonDeveMaiSuccedere = JoinList(_parser.GetListTag(bibleBlocks, "SERIES_FORBIDDEN_TOPICS")),
            ArcoNarrativoSerie = BuildSeasonArc(bibleBlocks),
            SerieFinalGoal = _parser.GetSingleTag(bibleBlocks, "SEASON_FINALE_PAYOFF"),
            NoteAI = BuildNoteAi(bibleTags, seasonTags)
        };

        var characters = ParseCharacters(characterBlocks);
        ApplyCharacterArcs(characters, characterBlocks);
        ApplyRelationships(characters, characterBlocks);

        var baseEpisodes = _parser.ParseEpisodeBlocksFromBlocks(seasonBlocks);
        var structureMap = _parser.ParseEpisodeStructures(episodeBlocks);

        var episodes = new List<SeriesEpisode>();
        foreach (var baseEp in baseEpisodes)
        {
            var structure = structureMap.TryGetValue(baseEp.Number, out var s) ? s : null;
            var payload = new
            {
                base_episode = baseEp,
                structure
            };
            var tramaJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            episodes.Add(new SeriesEpisode
            {
                Number = baseEp.Number,
                Title = baseEp.Title,
                EpisodeGoal = baseEp.APlot,
                StartSituation = baseEp.BPlot,
                Trama = tramaJson
            });
        }

        return new SeriesBuildResult(series, characters, episodes);
    }

    private static string? BuildAmbientazione(string? place, string? time)
    {
        if (string.IsNullOrWhiteSpace(place) && string.IsNullOrWhiteSpace(time))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(time))
        {
            return place;
        }

        if (string.IsNullOrWhiteSpace(place))
        {
            return time;
        }

        return $"{place}. {time}";
    }

    private string? BuildSeasonArc(List<TagBlock> bibleBlocks)
    {
        var logline = _parser.GetSingleTag(bibleBlocks, "SEASON_LOGLINE");
        var antagonist = _parser.GetSingleTag(bibleBlocks, "SEASON_MAIN_ANTAGONISM");
        var twist = _parser.GetSingleTag(bibleBlocks, "SEASON_MID_TWIST");
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(logline))
        {
            sb.AppendLine($"Logline: {logline}");
        }

        if (!string.IsNullOrWhiteSpace(antagonist))
        {
            sb.AppendLine($"Antagonism: {antagonist}");
        }

        if (!string.IsNullOrWhiteSpace(twist))
        {
            sb.AppendLine($"Mid twist: {twist}");
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string? BuildNoteAi(string bibleTags, string seasonTags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BIBLE_TAGS:");
        sb.AppendLine(bibleTags.Trim());
        sb.AppendLine();
        sb.AppendLine("SEASON_TAGS:");
        sb.AppendLine(seasonTags.Trim());
        return sb.ToString();
    }

    private List<SeriesCharacter> ParseCharacters(List<TagBlock> blocks)
    {
        var result = new List<SeriesCharacter>();
        foreach (var block in blocks.Where(b => b.Tag.Equals("CHARACTER", StringComparison.OrdinalIgnoreCase)))
        {
            var kv = _parser.ParseKeyValues(block.Lines);
            var name = kv.TryGetValue("NAME", out var n) ? n : "Unnamed";
            var gender = kv.TryGetValue("GENDER", out var g) ? g : "other";
            var bio = kv.TryGetValue("BIO_SHORT", out var b) ? b : null;
            var role = kv.TryGetValue("ROLE", out var r) ? r : null;
            var internalNeed = kv.TryGetValue("INTERNAL_NEED", out var i) ? i : null;
            var externalGoal = kv.TryGetValue("EXTERNAL_GOAL", out var e) ? e : null;
            var flaws = kv.TryGetValue("FLAWS", out var f) ? f : null;
            var skills = kv.TryGetValue("SKILLS", out var s) ? s : null;
            var limits = kv.TryGetValue("LIMITS", out var l) ? l : null;
            var voiceStyle = kv.TryGetValue("VOICE_STYLE", out var v) ? v : null;

            var profile = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(externalGoal))
            {
                profile.AppendLine($"ExternalGoal: {externalGoal}");
            }

            if (!string.IsNullOrWhiteSpace(internalNeed))
            {
                profile.AppendLine($"InternalNeed: {internalNeed}");
            }

            if (!string.IsNullOrWhiteSpace(flaws))
            {
                profile.AppendLine($"Flaws: {flaws}");
            }

            if (!string.IsNullOrWhiteSpace(skills))
            {
                profile.AppendLine($"Skills: {skills}");
            }

            if (!string.IsNullOrWhiteSpace(limits))
            {
                profile.AppendLine($"Limits: {limits}");
            }

            if (!string.IsNullOrWhiteSpace(voiceStyle))
            {
                profile.AppendLine($"VoiceStyle: {voiceStyle}");
            }

            result.Add(new SeriesCharacter
            {
                Name = name,
                Gender = string.IsNullOrWhiteSpace(gender) ? "other" : gender,
                Description = bio,
                RuoloNarrativo = role,
                ConflittoInterno = internalNeed,
                Profilo = profile.ToString().Trim()
            });
        }

        return result;
    }

    private void ApplyCharacterArcs(List<SeriesCharacter> characters, List<TagBlock> blocks)
    {
        var byId = BuildCharacterIdMap(blocks, characters);
        foreach (var block in blocks.Where(b => b.Tag.Equals("CHARACTER_SEASON_ARC", StringComparison.OrdinalIgnoreCase)))
        {
            var kv = _parser.ParseKeyValues(block.Lines);
            if (!kv.TryGetValue("ID", out var id))
            {
                continue;
            }

            if (!byId.TryGetValue(id, out var character))
            {
                continue;
            }

            var from = kv.TryGetValue("FROM", out var f) ? f : null;
            var to = kv.TryGetValue("TO", out var t) ? t : null;
            var turns = kv.TryGetValue("KEY_TURNS", out var k) ? k : null;
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(from))
            {
                sb.AppendLine($"From: {from}");
            }

            if (!string.IsNullOrWhiteSpace(to))
            {
                sb.AppendLine($"To: {to}");
            }

            if (!string.IsNullOrWhiteSpace(turns))
            {
                sb.AppendLine($"KeyTurns: {turns}");
            }

            character.ArcoPersonale = sb.ToString().Trim();
        }
    }

    private void ApplyRelationships(List<SeriesCharacter> characters, List<TagBlock> blocks)
    {
        var byId = BuildCharacterIdMap(blocks, characters);
        var relationsByChar = new Dictionary<SeriesCharacter, List<string>>();
        foreach (var block in blocks.Where(b => b.Tag.Equals("RELATIONSHIP", StringComparison.OrdinalIgnoreCase)))
        {
            var kv = _parser.ParseKeyValues(block.Lines);
            if (!kv.TryGetValue("FROM", out var fromId) || !kv.TryGetValue("TO", out var toId))
            {
                continue;
            }

            var type = kv.TryGetValue("TYPE", out var t) ? t : "relation";
            var notes = kv.TryGetValue("NOTES", out var n) ? n : null;

            if (!byId.TryGetValue(fromId, out var fromChar))
            {
                continue;
            }

            if (!relationsByChar.TryGetValue(fromChar, out var list))
            {
                list = new List<string>();
                relationsByChar[fromChar] = list;
            }

            list.Add($"{fromId} -> {toId} ({type}){(string.IsNullOrWhiteSpace(notes) ? string.Empty : $": {notes}")}");
        }

        foreach (var kvp in relationsByChar)
        {
            kvp.Key.AlleanzaRelazione = string.Join("\n", kvp.Value);
        }
    }

    private Dictionary<string, SeriesCharacter> BuildCharacterIdMap(List<TagBlock> blocks, List<SeriesCharacter> characters)
    {
        var ids = blocks.Where(b => b.Tag.Equals("CHARACTER", StringComparison.OrdinalIgnoreCase))
            .Select(b => _parser.ParseKeyValues(b.Lines))
            .Select(kv => new
            {
                Id = kv.TryGetValue("ID", out var id) ? id : null,
                Name = kv.TryGetValue("NAME", out var name) ? name : null
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
            .ToList();

        var map = new Dictionary<string, SeriesCharacter>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ids)
        {
            var match = characters.FirstOrDefault(c => c.Name.Equals(item.Name!, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                map[item.Id!] = match;
            }
        }

        return map;
    }

    private static string? JoinList(List<string> list)
        => list.Count == 0 ? null : string.Join(", ", list);
}

internal sealed record SeriesBuildResult(Series Series, List<SeriesCharacter> Characters, List<SeriesEpisode> Episodes);
