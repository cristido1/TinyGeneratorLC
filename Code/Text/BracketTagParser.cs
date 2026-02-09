using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TinyGenerator.Services.Text;

public sealed record TagBlock(string Name, string Content);

public static class BracketTagParser
{
	public static bool ContainsTag(string text, string tagName)
		=> TryGetTagContent(text, tagName, out _);

	public static bool TryGetTagContent(string text, string tagName, out string content)
	{
		content = string.Empty;
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(tagName)) return false;

		var normalized = Normalize(tagName);

		// Preferred: closed tag format [TAG]...[/TAG]
		var match = BuildClosedRegex(normalized).Match(text);
		if (match.Success)
		{
			content = CleanTagValue((match.Groups["content"].Value ?? string.Empty).Trim());
			return true;
		}

		// Fallback: open-only format [TAG]...
		var openToken = "[" + normalized + "]";
		var start = text.IndexOf(openToken, StringComparison.OrdinalIgnoreCase);
		if (start < 0) return false;

		var valueStart = start + openToken.Length;
		if (valueStart >= text.Length)
		{
			content = string.Empty;
			return true;
		}

		// Capture until next tag at start of line or end of text.
		var tail = text.Substring(valueStart);
		var nextTagMatch = NextTagAtLineStartRegex.Match(tail);
		var valueEnd = nextTagMatch.Success ? valueStart + nextTagMatch.Index : text.Length;
		content = CleanTagValue(text.Substring(valueStart, Math.Max(0, valueEnd - valueStart)).Trim());
		return true;
	}

	private static string CleanTagValue(string content)
	{
		if (string.IsNullOrWhiteSpace(content)) return string.Empty;
		var v = content.TrimStart();

		// Be tolerant with common separators used by models: [TAG]: value  or  [TAG] = value
		if (v.Length > 0 && (v[0] == ':' || v[0] == '='))
		{
			v = v.Substring(1).TrimStart();
		}

		return v.Trim();
	}

	public static string? GetTagContentOrNull(string text, string tagName)
		=> TryGetTagContent(text, tagName, out var content) ? content : null;

	public static IReadOnlyList<string> GetAllTagContents(string text, string tagName)
	{
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(tagName)) return Array.Empty<string>();

		var normalized = Normalize(tagName);
		var openToken = "[" + normalized + "]";
		var closeToken = "[/" + normalized + "]";

		var list = new List<string>();
		var index = 0;
		while (index < text.Length)
		{
			var start = text.IndexOf(openToken, index, StringComparison.OrdinalIgnoreCase);
			if (start < 0) break;
			var valueStart = start + openToken.Length;
			if (valueStart > text.Length) break;

			var close = text.IndexOf(closeToken, valueStart, StringComparison.OrdinalIgnoreCase);
			if (close >= 0)
			{
				list.Add(text.Substring(valueStart, close - valueStart).Trim());
				index = close + closeToken.Length;
				continue;
			}

			// No closing tag: capture until next tag at start of line.
			var tail = text.Substring(valueStart);
			var nextTagMatch = NextTagAtLineStartRegex.Match(tail);
			var valueEnd = nextTagMatch.Success ? valueStart + nextTagMatch.Index : text.Length;
			list.Add(text.Substring(valueStart, Math.Max(0, valueEnd - valueStart)).Trim());
			index = valueEnd;
		}

		return list;
	}

	public static IReadOnlyList<TagBlock> GetBlocks(string text, string tagName)
	{
		var contents = GetAllTagContents(text, tagName);
		return contents.Select(c => new TagBlock(Normalize(tagName), c)).ToList();
	}

	public static bool TryParseBool(string? value, out bool parsed)
	{
		parsed = false;
		if (string.IsNullOrWhiteSpace(value)) return false;
		var v = value.Trim();

		if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("1") || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("ok", StringComparison.OrdinalIgnoreCase))
		{
			parsed = true;
			return true;
		}

		if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v.Equals("0") || v.Equals("no", StringComparison.OrdinalIgnoreCase))
		{
			parsed = false;
			return true;
		}

		return false;
	}

	public static IReadOnlyList<int> ParseIntList(string? text)
	{
		if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();

		var parts = text
			.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(p => p.Trim());

		var list = new List<int>();
		foreach (var p in parts)
		{
			if (int.TryParse(p, out var n)) list.Add(n);
		}
		return list;
	}

	private static readonly Regex NextTagAtLineStartRegex = new(
		@"(?m)^\s*\[[A-Za-z0-9_]+\]",
		RegexOptions.CultureInvariant | RegexOptions.Compiled);

	private static Regex BuildClosedRegex(string normalizedTagName)
	{
		var normalized = Regex.Escape(normalizedTagName);
		return new Regex($@"\[{normalized}\]\s*(?<content>[\s\S]*?)\s*\[\/{normalized}\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
	}

	private static string Normalize(string tagName)
		=> tagName.Trim().TrimStart('[').TrimEnd(']').Trim('/').Trim();
}