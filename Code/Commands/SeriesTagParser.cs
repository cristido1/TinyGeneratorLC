using System;
using System.Collections.Generic;
using System.Linq;
using TinyGenerator.Services.Text;

namespace TinyGenerator.Services.Commands;

internal sealed class SeriesTagParser
{
    private static readonly HashSet<string> KnownNonTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ID",
        "NAME",
        "ROLE",
        "BIO_SHORT",
        "EXTERNAL_GOAL",
        "INTERNAL_NEED",
        "FLAWS",
        "SKILLS",
        "LIMITS",
        "VOICE_STYLE",
        "FROM",
        "TO",
        "TYPE",
        "NOTES",
        "NUMBER",
        "TITLE",
        "LOGLINE",
        "A_PLOT",
        "B_PLOT",
        "THEME",
        "SUMMARY"
    };

    public List<TagBlock> ParseTagBlocks(string text)
    {
        var blocks = new List<TagBlock>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return blocks;
        }

        TagBlock? current = null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.Contains(']'))
            {
                var end = line.IndexOf(']');
                var tag = line.Substring(1, end - 1).Trim();
                var content = line[(end + 1)..].Trim();
                current = new TagBlock(tag);
                blocks.Add(current);
                if (!string.IsNullOrWhiteSpace(content))
                {
                    current.Lines.Add(content);
                }
                continue;
            }

            if (current != null && FlexibleTagParser.TryParseKeyValueLine(line, out var fieldKey, out var fieldValue))
            {
                current.Lines.Add($"{fieldKey}: {fieldValue}");
                continue;
            }

            if (FlexibleTagParser.TryParseTagHeaderLine(line, out var inlineTag, out var inlineValue) && IsLikelyTopLevelTagName(inlineTag))
            {
                current = new TagBlock(inlineTag);
                blocks.Add(current);
                if (!string.IsNullOrWhiteSpace(inlineValue))
                {
                    current.Lines.Add(inlineValue);
                }
                continue;
            }

            current?.Lines.Add(line);
        }

        return blocks;
    }

    public Dictionary<string, string> ParseKeyValues(List<string> lines)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (!FlexibleTagParser.TryParseKeyValueLine(line, out var key, out var value))
            {
                continue;
            }

            dict[key] = value;
        }

        return dict;
    }

    public HashSet<string> CollectTags(string text)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in ParseTagBlocks(text))
        {
            if (!string.IsNullOrWhiteSpace(block.Tag))
            {
                tags.Add(block.Tag.Trim());
            }
        }

        return tags;
    }

    public List<EpisodeBase> ParseEpisodeBlocks(string seasonTags)
        => ParseEpisodeBlocksFromBlocks(ParseTagBlocks(seasonTags));

    public List<EpisodeBase> ParseEpisodeBlocksFromBlocks(List<TagBlock> blocks)
    {
        var list = new List<EpisodeBase>();
        foreach (var block in blocks.Where(b => b.Tag.Equals("EPISODE", StringComparison.OrdinalIgnoreCase)))
        {
            var kv = ParseKeyValues(block.Lines);
            var number = TryParseInt(kv.TryGetValue("NUMBER", out var n) ? n : null);
            if (number <= 0)
            {
                continue;
            }

            list.Add(new EpisodeBase
            {
                Number = number,
                Title = kv.TryGetValue("TITLE", out var t) ? t : null,
                Logline = kv.TryGetValue("LOGLINE", out var l) ? l : null,
                APlot = kv.TryGetValue("A_PLOT", out var a) ? a : null,
                BPlot = kv.TryGetValue("B_PLOT", out var b) ? b : null,
                Theme = kv.TryGetValue("THEME", out var th) ? th : null,
                RawBlock = block.Raw
            });
        }

        return list;
    }

    public Dictionary<int, EpisodeStructure> ParseEpisodeStructures(List<TagBlock> blocks)
    {
        var map = new Dictionary<int, EpisodeStructure>();
        EpisodeStructure? current = null;
        foreach (var block in blocks)
        {
            if (block.Tag.Equals("EPISODE_STRUCTURE", StringComparison.OrdinalIgnoreCase))
            {
                var kv = ParseKeyValues(block.Lines);
                var number = TryParseInt(kv.TryGetValue("NUMBER", out var n) ? n : null);
                if (number <= 0)
                {
                    continue;
                }

                current = new EpisodeStructure { Number = number };
                map[number] = current;
                continue;
            }

            if (current == null)
            {
                continue;
            }

            if (block.Tag.Equals("BEAT", StringComparison.OrdinalIgnoreCase))
            {
                var kv = ParseKeyValues(block.Lines);
                current.Beats.Add(new Beat
                {
                    Type = kv.TryGetValue("TYPE", out var t) ? t : null,
                    Summary = kv.TryGetValue("SUMMARY", out var s) ? s : null
                });
                continue;
            }

            if (block.Tag.Equals("CAST", StringComparison.OrdinalIgnoreCase))
            {
                current.Cast.AddRange(ParseList(block.Lines));
                continue;
            }

            if (block.Tag.Equals("LOCATIONS", StringComparison.OrdinalIgnoreCase))
            {
                current.Locations.AddRange(ParseList(block.Lines));
                continue;
            }

            if (block.Tag.Equals("SETUP", StringComparison.OrdinalIgnoreCase))
            {
                current.Setup.AddRange(ParseList(block.Lines));
                continue;
            }

            if (block.Tag.Equals("PAYOFF", StringComparison.OrdinalIgnoreCase))
            {
                current.Payoff.AddRange(ParseList(block.Lines));
            }
        }

        return map;
    }

    public List<string> ParseList(List<string> lines)
    {
        var list = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                list.Add(trimmed[2..].Trim());
            }
            else if (!string.IsNullOrWhiteSpace(trimmed))
            {
                list.Add(trimmed);
            }
        }

        return list;
    }

    public string? GetSingleTag(List<TagBlock> blocks, string tag)
    {
        var block = blocks.FirstOrDefault(b => b.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
        if (block == null || block.Lines.Count == 0)
        {
            return null;
        }

        if (block.Lines.Count == 1)
        {
            return block.Lines[0];
        }

        return string.Join(" ", block.Lines);
    }

    public List<string> GetListTag(List<TagBlock> blocks, string tag)
    {
        var block = blocks.FirstOrDefault(b => b.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
        return block == null ? new List<string>() : ParseList(block.Lines);
    }

    public static int TryParseInt(string? value)
        => int.TryParse(value, out var n) ? n : 0;

    private static bool IsLikelyTopLevelTagName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !KnownNonTopLevelKeys.Contains(value);
    }
}

internal sealed class TagBlock
{
    public string Tag { get; }
    public List<string> Lines { get; } = new();
    public string Raw => $"[{Tag}]\n{string.Join("\n", Lines)}";

    public TagBlock(string tag)
    {
        Tag = tag;
    }
}

internal sealed class EpisodeBase
{
    public int Number { get; set; }
    public string? Title { get; set; }
    public string? Logline { get; set; }
    public string? APlot { get; set; }
    public string? BPlot { get; set; }
    public string? Theme { get; set; }
    public string RawBlock { get; set; } = string.Empty;
}

internal sealed class EpisodeStructure
{
    public int Number { get; set; }
    public List<Beat> Beats { get; } = new();
    public List<string> Cast { get; } = new();
    public List<string> Locations { get; } = new();
    public List<string> Setup { get; } = new();
    public List<string> Payoff { get; } = new();
}

internal sealed class Beat
{
    public string? Type { get; set; }
    public string? Summary { get; set; }
}
