using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TinyGenerator.Services;

internal static class JsonDeterministicAutoFixer
{
    public static bool TryFix(
        string raw,
        JsonElement schemaRoot,
        out string fixedJson,
        out List<string> appliedFixes,
        out string? error)
    {
        fixedJson = string.Empty;
        appliedFixes = new List<string>();
        error = null;

        var candidate = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "risposta vuota";
            return false;
        }

        candidate = ExtractLikelyJsonBlock(candidate, appliedFixes);
        candidate = NormalizeQuotesAndStrings(candidate, appliedFixes);
        candidate = ReplaceSemicolonArraySeparators(candidate, appliedFixes);
        candidate = RemoveTrailingCommas(candidate, appliedFixes);
        candidate = QuoteUnquotedKeys(candidate, appliedFixes);
        candidate = InsertMissingCommasBetweenLines(candidate, appliedFixes);
        candidate = AutoCloseBrackets(candidate, appliedFixes);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(candidate);
            if (node == null)
            {
                error = "JSON nullo dopo fix";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = $"parse dopo fix fallita: {ex.Message}";
            return false;
        }

        var coerced = CoerceToSchema(node, schemaRoot, "$", appliedFixes);
        fixedJson = coerced.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        return true;
    }

    private static string ExtractLikelyJsonBlock(string text, List<string> fixes)
    {
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count >= 2 && lines[0].StartsWith("```", StringComparison.Ordinal))
            {
                lines.RemoveAt(0);
                if (lines.Count > 0 && lines[^1].StartsWith("```", StringComparison.Ordinal))
                {
                    lines.RemoveAt(lines.Count - 1);
                }
                text = string.Join('\n', lines).Trim();
                fixes.Add("rimosso_blocco_markdown");
            }
        }

        var firstObj = text.IndexOf('{');
        var firstArr = text.IndexOf('[');
        var first = firstObj >= 0 && firstArr >= 0 ? Math.Min(firstObj, firstArr) : Math.Max(firstObj, firstArr);
        var lastObj = text.LastIndexOf('}');
        var lastArr = text.LastIndexOf(']');
        var last = Math.Max(lastObj, lastArr);
        if (first >= 0 && last > first)
        {
            var extracted = text.Substring(first, last - first + 1).Trim();
            if (!string.Equals(extracted, text, StringComparison.Ordinal))
            {
                fixes.Add("estratto_blocco_json");
                return extracted;
            }
        }

        return text;
    }

    private static string NormalizeQuotesAndStrings(string text, List<string> fixes)
    {
        // Replace smart quotes.
        var normalized = text
            .Replace('“', '"')
            .Replace('”', '"')
            .Replace('‘', '\'')
            .Replace('’', '\'');
        if (!string.Equals(normalized, text, StringComparison.Ordinal))
        {
            fixes.Add("normalizzate_virgolette_tipografiche");
        }

        var sb = new StringBuilder(normalized.Length + 16);
        var inDouble = false;
        var inSingle = false;
        var escaped = false;

        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];

            if (inSingle)
            {
                if (escaped)
                {
                    sb.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    sb.Append(c);
                    escaped = true;
                    continue;
                }

                if (c == '\'')
                {
                    sb.Append('"');
                    inSingle = false;
                    fixes.Add("sostituite_virgolette_singole");
                    continue;
                }

                if (c == '"')
                {
                    sb.Append("\\\"");
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    sb.Append("\\n");
                    fixes.Add("normalizzata_stringa_multilinea");
                    continue;
                }

                sb.Append(c);
                continue;
            }

            if (inDouble)
            {
                if (escaped)
                {
                    sb.Append(c);
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    sb.Append(c);
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    // Heuristic: if next significant char is not a JSON delimiter, treat as inner quote.
                    var next = NextNonWhitespace(normalized, i + 1);
                    if (next != '\0' && next != ',' && next != '}' && next != ']' && next != ':')
                    {
                        sb.Append("\\\"");
                        fixes.Add("escapate_virgolette_interne_stringa");
                        continue;
                    }

                    sb.Append(c);
                    inDouble = false;
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    sb.Append("\\n");
                    fixes.Add("normalizzata_stringa_multilinea");
                    continue;
                }

                sb.Append(c);
                continue;
            }

            if (c == '\'')
            {
                inSingle = true;
                sb.Append('"');
                continue;
            }

            if (c == '"')
            {
                inDouble = true;
                sb.Append(c);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static char NextNonWhitespace(string text, int start)
    {
        for (var i = Math.Max(0, start); i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i];
            }
        }

        return '\0';
    }

    private static string ReplaceSemicolonArraySeparators(string text, List<string> fixes)
    {
        var replaced = Regex.Replace(text, @"\]\s*;\s*\[", "],[");
        replaced = Regex.Replace(replaced, @"(""(?:\\.|[^""\\])*""|\d+|true|false|null)\s*;\s*(""(?:\\.|[^""\\])*""|\d+|true|false|null)", "$1,$2");
        if (!string.Equals(replaced, text, StringComparison.Ordinal))
        {
            fixes.Add("sostituiti_separatori_array_puntoevirgola");
        }
        return replaced;
    }

    private static string RemoveTrailingCommas(string text, List<string> fixes)
    {
        var replaced = Regex.Replace(text, @",\s*(\]|\})", "$1");
        if (!string.Equals(replaced, text, StringComparison.Ordinal))
        {
            fixes.Add("rimossa_virgola_finale");
        }
        return replaced;
    }

    private static string QuoteUnquotedKeys(string text, List<string> fixes)
    {
        var replaced = Regex.Replace(
            text,
            @"(?<=\{|,)\s*([A-Za-z_][A-Za-z0-9_\-]*)\s*:",
            m => $" \"{m.Groups[1].Value}\":");
        if (!string.Equals(replaced, text, StringComparison.Ordinal))
        {
            fixes.Add("aggiunte_virgolette_alle_chiavi");
        }
        return replaced;
    }

    private static string InsertMissingCommasBetweenLines(string text, List<string> fixes)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n').ToList();
        if (lines.Count <= 1) return text;

        var changed = false;
        for (var i = 0; i < lines.Count - 1; i++)
        {
            var current = lines[i].TrimEnd();
            var next = lines[i + 1].TrimStart();
            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(next)) continue;
            if (current.EndsWith("{", StringComparison.Ordinal) || current.EndsWith("[", StringComparison.Ordinal) || current.EndsWith(",", StringComparison.Ordinal))
            {
                continue;
            }

            var currentEndsValue =
                current.EndsWith("\"", StringComparison.Ordinal) ||
                current.EndsWith("}", StringComparison.Ordinal) ||
                current.EndsWith("]", StringComparison.Ordinal) ||
                char.IsDigit(current[^1]) ||
                current.EndsWith("true", StringComparison.OrdinalIgnoreCase) ||
                current.EndsWith("false", StringComparison.OrdinalIgnoreCase) ||
                current.EndsWith("null", StringComparison.OrdinalIgnoreCase);

            var nextStartsProp = next.StartsWith("\"", StringComparison.Ordinal) || Regex.IsMatch(next, @"^[A-Za-z_][A-Za-z0-9_\-]*\s*:");
            if (currentEndsValue && nextStartsProp)
            {
                lines[i] = current + ",";
                changed = true;
            }
        }

        if (changed)
        {
            fixes.Add("inserite_virgole_mancanti");
            return string.Join('\n', lines);
        }

        return text;
    }

    private static string AutoCloseBrackets(string text, List<string> fixes)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        foreach (var c in text)
        {
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{' || c == '[')
            {
                stack.Push(c);
                continue;
            }

            if (c == '}' || c == ']')
            {
                if (stack.Count == 0) continue;
                var open = stack.Peek();
                if ((open == '{' && c == '}') || (open == '[' && c == ']'))
                {
                    stack.Pop();
                }
            }
        }

        if (stack.Count == 0) return text;

        var sb = new StringBuilder(text);
        while (stack.Count > 0)
        {
            var open = stack.Pop();
            sb.Append(open == '{' ? '}' : ']');
        }
        fixes.Add("chiuse_parentesi_mancanti");
        return sb.ToString();
    }

    private static JsonNode CoerceToSchema(JsonNode node, JsonElement schema, string path, List<string> fixes)
    {
        var types = GetSchemaTypes(schema);
        if (types.Count == 0)
        {
            if (TryGetPropertyIgnoreCase(schema, "properties", out _)) types.Add("object");
            if (TryGetPropertyIgnoreCase(schema, "items", out _)) types.Add("array");
        }

        if (types.Contains("array") && node is JsonObject objNode)
        {
            fixes.Add($"coerzione_array:{path}");
            node = new JsonArray(objNode);
        }
        else if (types.Contains("object") && node is JsonArray arrNode && arrNode.Count == 1 && arrNode[0] is JsonObject singleObj)
        {
            fixes.Add($"coerzione_object:{path}");
            node = singleObj;
        }

        if (node is JsonValue valueNode)
        {
            node = CoerceValue(valueNode, types, path, fixes);
        }

        if (node is JsonObject obj && TryGetPropertyIgnoreCase(schema, "properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            var propsByName = properties.EnumerateObject().ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var originalNames = obj.Select(kv => kv.Key).ToList();

            foreach (var key in originalNames)
            {
                if (propsByName.ContainsKey(key)) continue;
                var match = FindClosestProperty(key, propsByName.Keys);
                if (!string.IsNullOrWhiteSpace(match))
                {
                    var value = obj[key];
                    obj.Remove(key);
                    obj[match] = value;
                    fixes.Add($"rinominata_proprieta:{path}.{key}->{match}");
                }
            }

            foreach (var kv in obj.ToList())
            {
                if (!propsByName.TryGetValue(kv.Key, out var propSchema))
                {
                    continue;
                }

                if (kv.Value == null) continue;
                obj[kv.Key] = CoerceToSchema(kv.Value, propSchema.Value, $"{path}.{kv.Key}", fixes);
            }
        }

        if (node is JsonArray arr && TryGetPropertyIgnoreCase(schema, "items", out var itemsSchema))
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] == null) continue;
                arr[i] = CoerceToSchema(arr[i]!, itemsSchema, $"{path}[{i}]", fixes);
            }
        }

        return node;
    }

    private static JsonNode CoerceValue(JsonValue valueNode, IReadOnlyCollection<string> types, string path, List<string> fixes)
    {
        if (!valueNode.TryGetValue<string>(out var rawString))
        {
            return valueNode;
        }

        var trimmed = (rawString ?? string.Empty).Trim();

        if (types.Contains("null") && string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
        {
            fixes.Add($"coerzione_null:{path}");
            return null!;
        }

        if (types.Contains("boolean"))
        {
            if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase))
            {
                fixes.Add($"coerzione_boolean:{path}");
                return JsonValue.Create(true)!;
            }
            if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
            {
                fixes.Add($"coerzione_boolean:{path}");
                return JsonValue.Create(false)!;
            }
        }

        if (types.Contains("integer") && long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            fixes.Add($"coerzione_intero:{path}");
            return JsonValue.Create(l)!;
        }

        if ((types.Contains("number") || types.Contains("integer")) &&
            decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            fixes.Add($"coerzione_numero:{path}");
            return JsonValue.Create(d)!;
        }

        return valueNode;
    }

    private static string? FindClosestProperty(string source, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        var ties = 0;

        foreach (var candidate in candidates)
        {
            var distance = Levenshtein(source, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
                ties = 1;
            }
            else if (distance == bestDistance)
            {
                ties++;
            }
        }

        if (best == null) return null;
        if (bestDistance > 2) return null;
        if (ties > 1) return null;
        return best;
    }

    private static int Levenshtein(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.IsNullOrEmpty(a)) return b.Length;
        if (string.IsNullOrEmpty(b)) return a.Length;

        var n = a.Length;
        var m = b.Length;
        var dp = new int[n + 1, m + 1];

        for (var i = 0; i <= n; i++) dp[i, 0] = i;
        for (var j = 0; j <= m; j++) dp[0, j] = j;

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                dp[i, j] = Math.Min(
                    Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost);
            }
        }

        return dp[n, m];
    }

    private static List<string> GetSchemaTypes(JsonElement schema)
    {
        if (!TryGetPropertyIgnoreCase(schema, "type", out var typeNode))
        {
            return new List<string>();
        }

        if (typeNode.ValueKind == JsonValueKind.String)
        {
            var t = typeNode.GetString();
            return string.IsNullOrWhiteSpace(t) ? new List<string>() : new List<string> { t!.Trim().ToLowerInvariant() };
        }

        if (typeNode.ValueKind == JsonValueKind.Array)
        {
            return typeNode
                .EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => (x.GetString() ?? string.Empty).Trim().ToLowerInvariant())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return new List<string>();
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out value))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
