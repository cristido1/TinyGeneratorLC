using System;
using System.Collections.Generic;

namespace TinyGenerator.Services.Text;

public static class FlexibleTagParser
{
    public static bool TryGetTagContent(string text, string tagName, out string content)
    {
        var all = GetAllTagContents(text, tagName);
        if (all.Count == 0)
        {
            content = string.Empty;
            return false;
        }

        content = all[0];
        return true;
    }

    public static IReadOnlyList<string> GetAllTagContents(string text, string tagName)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(tagName))
        {
            return list;
        }

        var target = NormalizeTagName(tagName);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var collecting = false;
        var current = new List<string>();

        void Flush()
        {
            if (current.Count == 0)
            {
                list.Add(string.Empty);
            }
            else
            {
                list.Add(string.Join("\n", current).Trim());
            }
            current.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (TryParseTagHeaderLine(line, out var parsedTag, out var inlineValue))
            {
                var normalizedParsed = NormalizeTagName(parsedTag);
                if (collecting)
                {
                    Flush();
                    collecting = false;
                }

                if (string.Equals(normalizedParsed, target, StringComparison.OrdinalIgnoreCase))
                {
                    collecting = true;
                    if (!string.IsNullOrWhiteSpace(inlineValue))
                    {
                        current.Add(inlineValue.Trim());
                    }
                }

                continue;
            }

            if (!collecting)
            {
                continue;
            }

            current.Add(raw.TrimEnd());
        }

        if (collecting)
        {
            Flush();
        }

        return list;
    }

    public static bool TryParseTagHeaderLine(string line, out string tag, out string value)
    {
        tag = string.Empty;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var candidate = TrimBulletPrefix(line.Trim());

        // [TAG] value
        if (candidate.StartsWith("[", StringComparison.Ordinal) && candidate.Contains("]", StringComparison.Ordinal))
        {
            var end = candidate.IndexOf(']');
            if (end > 1)
            {
                var header = candidate[1..end].Trim();
                if (IsLikelyTagName(header))
                {
                    tag = header;
                    value = NormalizeValue(candidate[(end + 1)..]);
                    return true;
                }
            }
        }

        // TAG: value / TAG - value / TAG = value
        foreach (var sep in new[] { ':', '-', '=' })
        {
            var idx = candidate.IndexOf(sep);
            if (idx <= 0)
            {
                continue;
            }

            var header = candidate[..idx].Trim();
            if (!IsLikelyTagName(header))
            {
                continue;
            }

            tag = header;
            value = NormalizeValue(candidate[(idx + 1)..]);
            return true;
        }

        return false;
    }

    public static bool TryParseKeyValueLine(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var candidate = TrimBulletPrefix(line.Trim());
        var idx = candidate.IndexOf(':');
        if (idx <= 0)
        {
            return false;
        }

        var rawKey = candidate[..idx].Trim();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return false;
        }

        // Accept markdown emphasis: **ID:** value
        rawKey = rawKey.Trim('*', '`', '_', '[', ']');
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return false;
        }

        key = rawKey;
        value = NormalizeValue(candidate[(idx + 1)..]);
        return true;
    }

    private static string TrimBulletPrefix(string text)
    {
        if (text.StartsWith("- ", StringComparison.Ordinal) ||
            text.StartsWith("* ", StringComparison.Ordinal) ||
            text.StartsWith("• ", StringComparison.Ordinal))
        {
            return text[2..].Trim();
        }

        return text;
    }

    private static string NormalizeValue(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length >= 2 && trimmed.StartsWith("\"", StringComparison.Ordinal) && trimmed.EndsWith("\"", StringComparison.Ordinal))
        {
            return trimmed[1..^1];
        }

        if (trimmed.Length >= 4 && trimmed.StartsWith("\\\"", StringComparison.Ordinal) && trimmed.EndsWith("\\\"", StringComparison.Ordinal))
        {
            return trimmed[2..^2].Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        return trimmed;
    }

    private static bool IsLikelyTagName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var ch in value.Trim())
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == ' '))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeTagName(string value)
        => value.Trim().TrimStart('[').TrimEnd(']').Trim('/').Trim();
}
