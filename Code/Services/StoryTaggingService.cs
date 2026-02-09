using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed class StoryTaggingService
{
    public const string TagTypeFormatter = "formatter";
    public const string TagTypeAmbient = "ambient_expert";
    public const string TagTypeFx = "fx_expert";
    public const string TagTypeMusic = "music_expert";

    public sealed record StoryRow(int LineId, string Text);

    public sealed record StoryTagEntry(string Type, int Line, string Tag, string? Ts = null);

    public sealed record StoryRowsBuildResult(string StoryRows, IReadOnlyList<FormatterV2.Piece> Pieces, int LineCount);

    public static StoryRowsBuildResult BuildStoryRows(string storyRevised)
    {
        var build = FormatterV2.BuildNumberedLines(storyRevised ?? string.Empty);
        return new StoryRowsBuildResult(build.NumberedLines, build.Pieces, build.TaggableLineCount);
    }

    public static List<StoryRow> ParseStoryRows(string? storyRows)
    {
        var rows = new List<StoryRow>();
        if (string.IsNullOrWhiteSpace(storyRows)) return rows;

        var lines = storyRows.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            var line = (raw ?? string.Empty).TrimEnd();
            if (line.Length < 4) continue;
            int i = 0;
            while (i < line.Length && char.IsDigit(line[i])) i++;
            if (i == 0 || i >= line.Length) continue;
            if (!int.TryParse(line.Substring(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            var text = line.Substring(i).TrimStart();
            rows.Add(new StoryRow(id, text));
        }

        return rows;
    }

    public sealed record RowChunk(IReadOnlyList<StoryRow> Rows, string Text, int StartLineId, int EndLineId);

    public static List<RowChunk> SplitRowsIntoChunks(
        IReadOnlyList<StoryRow> rows,
        int minTokens,
        int maxTokens,
        int targetTokens)
    {
        var chunks = new List<RowChunk>();
        if (rows.Count == 0) return chunks;

        var minT = Math.Max(0, minTokens);
        var maxT = Math.Max(1, maxTokens);
        var targetT = Math.Max(1, targetTokens);

        int index = 0;
        while (index < rows.Count)
        {
            int start = index;
            int end = start;
            int tokens = 0;

            while (end < rows.Count)
            {
                var rowTokens = CountTokens(rows[end].Text);
                if (tokens + rowTokens > maxT && tokens >= minT) break;

                tokens += rowTokens;
                end++;

                if (tokens >= targetT && tokens >= minT) break;
            }

            if (end == start)
            {
                end = Math.Min(start + 1, rows.Count);
            }

            var slice = rows.Skip(start).Take(end - start).ToList();
            var text = string.Join("\n", slice.Select(r => $"{r.LineId:000} {r.Text}"));
            chunks.Add(new RowChunk(slice, text, slice.First().LineId, slice.Last().LineId));

            if (end >= rows.Count) break;
            index = end;
        }

        return chunks;
    }

    public static List<StoryTagEntry> LoadStoryTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<StoryTagEntry>();
        try
        {
            var items = JsonSerializer.Deserialize<List<StoryTagEntry>>(json);
            return items ?? new List<StoryTagEntry>();
        }
        catch
        {
            return new List<StoryTagEntry>();
        }
    }

    public static string SerializeStoryTags(IEnumerable<StoryTagEntry> tags)
    {
        return JsonSerializer.Serialize(tags ?? Array.Empty<StoryTagEntry>());
    }

    public static IReadOnlyList<StoryTagEntry> ParseFormatterMapping(
        IReadOnlyList<StoryRow> chunkRows,
        string mappingText)
    {
        var parsed = FormatterV2.ParseIdToTagsMapping(mappingText);
        var map = new Dictionary<int, string>();
        var rowIds = new HashSet<int>(chunkRows.Select(r => r.LineId));

        foreach (var kvp in parsed)
        {
            if (rowIds.Contains(kvp.Key))
            {
                map[kvp.Key] = kvp.Value;
            }
        }

        var result = new List<StoryTagEntry>(chunkRows.Count);
        foreach (var row in chunkRows)
        {
            if (map.TryGetValue(row.LineId, out var tags) && !string.IsNullOrWhiteSpace(tags))
            {
                result.Add(new StoryTagEntry(TagTypeFormatter, row.LineId, tags.Trim()));
            }
            else
            {
                result.Add(new StoryTagEntry(TagTypeFormatter, row.LineId, "[NARRATORE]"));
            }
        }

        return result;
    }

    public static IReadOnlyList<StoryTagEntry> ParseTagMappingByType(
        string mappingText,
        string tagType)
    {
        var result = new List<StoryTagEntry>();
        if (string.IsNullOrWhiteSpace(mappingText)) return result;

        var lines = mappingText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            if (!TryParseLineWithIdPrefix(raw, out var id, out var tail)) continue;
            if (tail.Length == 0) continue;

            foreach (Match match in Regex.Matches(tail, "\\[[^\\]]+\\]"))
            {
                var tag = match.Value.Trim();
                if (IsTagAllowedForType(tag, tagType))
                {
                    result.Add(new StoryTagEntry(tagType, id, NormalizeTag(tag)));
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<StoryTagEntry> ParseAmbientMapping(string mappingText)
    {
        var result = new List<StoryTagEntry>();
        if (string.IsNullOrWhiteSpace(mappingText)) return result;

        var lines = mappingText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            if (!TryParseLineWithIdPrefix(raw, out var id, out var tail)) continue;
            if (tail.Length == 0) continue;

            var matchedAny = false;
            foreach (Match match in Regex.Matches(tail, "\\[[^\\]]+\\]"))
            {
                var tag = match.Value.Trim();
                if (IsTagAllowedForType(tag, TagTypeAmbient))
                {
                    result.Add(new StoryTagEntry(TagTypeAmbient, id, NormalizeTag(tag)));
                    matchedAny = true;
                }
            }

            if (!matchedAny)
            {
                var text = CleanAmbientDescription(tail);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(new StoryTagEntry(TagTypeAmbient, id, $"[RUMORI: {text}]"));
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<StoryTagEntry> ParseFxMapping(string mappingText, out int invalidLines)
    {
        var result = new List<StoryTagEntry>();
        invalidLines = 0;
        if (string.IsNullOrWhiteSpace(mappingText)) return result;

        var lines = mappingText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            if (!TryParseLineWithIdPrefix(raw, out var id, out var tail)) continue;
            if (tail.Length == 0) continue;

            // Accept only strict formats:
            // 1) "001 descrizione [2]" or "001 descrizione [2 s|2sec|2 sec]"
            // 2) "001 [2] descrizione" (same duration variants)
            var durationPattern = @"\[(\d+(?:[.,]\d+)?)(?:\s*(?:s|sec|secondi))?\]";
            var m1 = Regex.Match(tail, $"^\\s*{durationPattern}\\s*(.+)$", RegexOptions.IgnoreCase);
            var m2 = Regex.Match(tail, $"^\\s*(.+?)\\s*{durationPattern}\\s*$", RegexOptions.IgnoreCase);

            string? description = null;
            string? durationStr = null;

            if (m1.Success)
            {
                durationStr = m1.Groups[1].Value;
                description = m1.Groups[2].Value;
            }
            else if (m2.Success)
            {
                description = m2.Groups[1].Value;
                durationStr = m2.Groups[2].Value;
            }

            if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(durationStr))
            {
                invalidLines++;
                continue;
            }

            var durationNorm = durationStr.Replace(',', '.');
            if (!double.TryParse(durationNorm, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
            {
                invalidLines++;
                continue;
            }

            var durationSeconds = (int)Math.Ceiling(duration);
            if (durationSeconds <= 0)
            {
                invalidLines++;
                continue;
            }

            var tag = $"[FX: {durationSeconds} : {description.Trim()}]";
            result.Add(new StoryTagEntry(TagTypeFx, id, tag));
        }

        return result;
    }

    public static IReadOnlyList<StoryTagEntry> ParseMusicMapping(string mappingText)
    {
        var result = new List<StoryTagEntry>();
        if (string.IsNullOrWhiteSpace(mappingText)) return result;

        var lines = mappingText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var raw in lines)
        {
            if (!TryParseLineWithIdPrefix(raw, out var id, out var tail)) continue;
            if (tail.Length == 0) continue;

            var durationSeconds = (int?)null;
            var durationMatch = Regex.Match(tail, @"\[(\d+(?:[.,]\d+)?)\]\s*$", RegexOptions.IgnoreCase);
            if (durationMatch.Success)
            {
                var durationStr = durationMatch.Groups[1].Value.Replace(',', '.');
                if (double.TryParse(durationStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                {
                    durationSeconds = (int)Math.Ceiling(duration);
                }

                tail = tail.Substring(0, durationMatch.Index).TrimEnd();
            }

            var mood = tail.Trim();
            if (string.IsNullOrWhiteSpace(mood)) continue;

            var tag = durationSeconds.HasValue
                ? $"[MUSIC: {durationSeconds.Value} s | {mood}]"
                : $"[MUSIC: {mood}]";
            result.Add(new StoryTagEntry(TagTypeMusic, id, tag));
        }

        return result;
    }

    public static IReadOnlyList<StoryTagEntry> FilterMusicTagsByProximity(
        IReadOnlyList<StoryTagEntry> tags,
        int minLineDistance = 20)
    {
        if (tags == null || tags.Count == 0) return tags ?? Array.Empty<StoryTagEntry>();

        var ordered = tags
            .OrderBy(t => t.Line)
            .ThenBy(t => t.Tag ?? string.Empty)
            .ToList();

        var filtered = new List<StoryTagEntry>();
        string? lastTag = null;
        int lastRow = int.MinValue;

        foreach (var tag in ordered)
        {
            // Drop duplicates of the same music tag on consecutive lines.
            if (lastTag != null &&
                string.Equals(tag.Tag, lastTag, StringComparison.OrdinalIgnoreCase) &&
                tag.Line == lastRow + 1)
            {
                continue;
            }

            // Drop tags too close to the previous kept music tag.
            if (lastRow != int.MinValue && (tag.Line - lastRow) < minLineDistance)
            {
                continue;
            }

            filtered.Add(tag);
            lastTag = tag.Tag;
            lastRow = tag.Line;
        }

        return filtered;
    }

    public static bool IsTagAllowedForType(string tag, string tagType)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        if (tagType == TagTypeAmbient)
        {
            return Regex.IsMatch(tag, @"^\[(?:RUMORI|RUMORE|AMBIENTE)\b", RegexOptions.IgnoreCase);
        }

        if (tagType == TagTypeFx)
        {
            return Regex.IsMatch(tag, @"^\[\s*FX\b", RegexOptions.IgnoreCase);
        }

        if (tagType == TagTypeMusic)
        {
            return Regex.IsMatch(tag, @"^\[\s*MUSIC\b", RegexOptions.IgnoreCase);
        }

        if (tagType == TagTypeFormatter)
        {
            return Regex.IsMatch(tag, @"\[(?:NARRATORE|PERSONAGGIO:)\b", RegexOptions.IgnoreCase);
        }

        return false;
    }

    public static string NormalizeTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return tag;
        var trimmed = tag.Trim();
        trimmed = Regex.Replace(trimmed, @"\[(\s*)RUMORE\b", "[$1RUMORI", RegexOptions.IgnoreCase);
        trimmed = Regex.Replace(trimmed, @"\[(\s*)AMBIENTE\b", "[$1RUMORI", RegexOptions.IgnoreCase);
        trimmed = Regex.Replace(trimmed, @"^\[\s*RUMORI\s*:?\s*-?\d+\s*(.+)\]$", m => $"[RUMORI: {m.Groups[1].Value.Trim()}]", RegexOptions.IgnoreCase);
        trimmed = Regex.Replace(trimmed, @"^\[\s*RUMORI\s*:?\s*:\s*(.+)\]$", m => $"[RUMORI: {m.Groups[1].Value.Trim()}]", RegexOptions.IgnoreCase);
        return trimmed;
    }

    private static string CleanAmbientDescription(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var cleaned = text.Trim();
        cleaned = Regex.Replace(cleaned, @"^\s*-?\d+\s+", "");
        return cleaned.Trim();
    }

    private static bool TryParseLineWithIdPrefix(string? raw, out int id, out string tail)
    {
        id = 0;
        tail = string.Empty;

        var line = (raw ?? string.Empty).Trim();
        if (line.Length < 2) return false;

        // Accept optional bullet prefix: "- 054: text"
        if (line.StartsWith("-", StringComparison.Ordinal))
        {
            line = line.Substring(1).TrimStart();
            if (line.Length < 2) return false;
        }

        int i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i == 0) return false;
        if (!int.TryParse(line.Substring(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) return false;

        var rest = line.Substring(i).TrimStart();
        // Accept optional delimiter: "054: text"
        if (rest.StartsWith(":", StringComparison.Ordinal))
        {
            rest = rest.Substring(1).TrimStart();
        }

        tail = rest;
        return true;
    }

    public static string BuildStoryTagged(string storyRevised, IReadOnlyList<StoryTagEntry> tags)
    {
        var build = FormatterV2.BuildNumberedLines(storyRevised ?? string.Empty);
        var pieces = build.Pieces;
        var maxLine = build.TaggableLineCount;

        var formatterTags = tags
            .Where(t => t.Type == TagTypeFormatter && t.Line >= 1 && t.Line <= maxLine)
            .GroupBy(t => t.Line)
            .ToDictionary(g => g.Key, g => g.Last().Tag, EqualityComparer<int>.Default);

        var orderedTypes = new[] { TagTypeAmbient, TagTypeFx, TagTypeMusic };
        var tagsByLine = tags
            .Where(t => t.Type != TagTypeFormatter && t.Line >= 1 && t.Line <= maxLine)
            .GroupBy(t => t.Line)
            .ToDictionary(g => g.Key, g => g.ToList(), EqualityComparer<int>.Default);

        string? lastFormatterTag = null;
        var sb = new StringBuilder(storyRevised?.Length ?? 0);

        static void AppendTagLine(StringBuilder sb, string tag)
        {
            if (sb.Length > 0)
            {
                var last = sb[sb.Length - 1];
                if (last != '\n' && last != '\r')
                {
                    sb.Append('\n');
                }
            }

            sb.Append(tag).Append('\n');
        }

        foreach (var piece in pieces)
        {
            if (piece.IsTaggable && piece.LineId.HasValue)
            {
                var lineId = piece.LineId.Value;
                var hasNonFormatterTags = false;
                if (tagsByLine.TryGetValue(lineId, out var lineTags))
                {
                    foreach (var type in orderedTypes)
                    {
                        foreach (var t in lineTags.Where(t => t.Type == type))
                        {
                            AppendTagLine(sb, t.Tag);
                            hasNonFormatterTags = true;
                        }
                    }
                }

                var formatterTag = formatterTags.TryGetValue(lineId, out var ft)
                    ? ft
                    : "[NARRATORE]";

                if (hasNonFormatterTags || !string.Equals(lastFormatterTag, formatterTag, StringComparison.Ordinal))
                {
                    AppendTagLine(sb, formatterTag);
                    lastFormatterTag = formatterTag;
                }
            }

            sb.Append(piece.Text);
        }

        return sb.ToString();
    }

    public static int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 0;
        bool inToken = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (inToken) inToken = false;
            }
            else
            {
                if (!inToken)
                {
                    count++;
                    inToken = true;
                }
            }
        }
        return count;
    }
}
