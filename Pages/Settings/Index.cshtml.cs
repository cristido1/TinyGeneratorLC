using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace TinyGenerator.Pages.Settings
{
    public class IndexModel : PageModel
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _configuration;
        private readonly ILogger<IndexModel> _logger;

        public JsonTreeNode? Root { get; private set; }

        [TempData]
        public string? StatusMessage { get; set; }

        public IndexModel(IWebHostEnvironment env, IConfiguration configuration, ILogger<IndexModel> logger)
        {
            _env = env;
            _configuration = configuration;
            _logger = logger;
        }

        public void OnGet()
        {
            LoadTree();
        }

        public IActionResult OnPostSave(Dictionary<string, string> values, Dictionary<string, string> types)
        {
            try
            {
                var path = GetSettingsPath();
                if (!System.IO.File.Exists(path))
                {
                    StatusMessage = "appsettings.json non trovato.";
                    LoadTree();
                    return Page();
                }

                var json = System.IO.File.ReadAllText(path);
                var root = JsonNode.Parse(json);
                if (root == null)
                {
                    StatusMessage = "Impossibile leggere appsettings.json.";
                    LoadTree();
                    return Page();
                }

                var errors = new List<string>();
                foreach (var kvp in values ?? new Dictionary<string, string>())
                {
                    var formKey = kvp.Key;
                    var rawValue = kvp.Value;

                    if (string.IsNullOrWhiteSpace(formKey))
                    {
                        continue;
                    }

                    try
                    {
                        var pointer = DecodeFormKey(formKey);
                        if (string.IsNullOrWhiteSpace(pointer))
                        {
                            continue;
                        }

                        var type = (types != null && types.TryGetValue(formKey, out var t)) ? t : "string";
                        SetValueByPointer(root, pointer, type, rawValue);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{formKey}: {ex.Message}");
                    }
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var output = root.ToJsonString(options);
                System.IO.File.WriteAllText(path, output, Encoding.UTF8);

                ReloadConfiguration();

                StatusMessage = errors.Count == 0
                    ? "Impostazioni salvate e ricaricate."
                    : $"Impostazioni salvate con {errors.Count} errori: {string.Join(" | ", errors)}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore salvataggio appsettings.json");
                StatusMessage = $"Errore durante il salvataggio: {ex.Message}";
            }

            return RedirectToPage();
        }

        private void LoadTree()
        {
            var path = GetSettingsPath();
            if (!System.IO.File.Exists(path))
            {
                Root = null;
                StatusMessage = "appsettings.json non trovato.";
                return;
            }

            var json = System.IO.File.ReadAllText(path);
            var root = JsonNode.Parse(json);
            if (root == null)
            {
                Root = null;
                StatusMessage = "Impossibile leggere appsettings.json.";
                return;
            }

            Root = BuildTree(root, displayName: "(root)", jsonPointer: string.Empty, depth: 0);
        }

        private string GetSettingsPath()
        {
            return Path.Combine(_env.ContentRootPath, "appsettings.json");
        }

        private void ReloadConfiguration()
        {
            if (_configuration is IConfigurationRoot root)
            {
                root.Reload();
            }
        }

        private static JsonTreeNode BuildTree(JsonNode node, string displayName, string jsonPointer, int depth)
        {
            var kind = GetNodeKind(node);
            var treeNode = new JsonTreeNode
            {
                DisplayName = displayName,
                JsonPointer = jsonPointer,
                FormKey = EncodeFormKey(jsonPointer),
                Kind = kind,
                Depth = depth,
                Value = GetLeafValue(node, kind)
            };

            if (node is JsonObject obj)
            {
                foreach (var kvp in obj.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (kvp.Value == null)
                    {
                        var childPointer = CombinePointer(jsonPointer, EscapePointerToken(kvp.Key));
                        treeNode.Children.Add(new JsonTreeNode
                        {
                            DisplayName = kvp.Key,
                            JsonPointer = childPointer,
                            FormKey = EncodeFormKey(childPointer),
                            Kind = "null",
                            Depth = depth + 1,
                            Value = string.Empty
                        });
                        continue;
                    }

                    var childPtr = CombinePointer(jsonPointer, EscapePointerToken(kvp.Key));
                    treeNode.Children.Add(BuildTree(kvp.Value, kvp.Key, childPtr, depth + 1));
                }
            }
            else if (node is JsonArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    var item = arr[i];
                    var name = $"[{i}]";
                    var childPtr = CombinePointer(jsonPointer, i.ToString(CultureInfo.InvariantCulture));
                    if (item == null)
                    {
                        treeNode.Children.Add(new JsonTreeNode
                        {
                            DisplayName = name,
                            JsonPointer = childPtr,
                            FormKey = EncodeFormKey(childPtr),
                            Kind = "null",
                            Depth = depth + 1,
                            Value = string.Empty
                        });
                    }
                    else
                    {
                        treeNode.Children.Add(BuildTree(item, name, childPtr, depth + 1));
                    }
                }
            }

            return treeNode;
        }

        private static string GetNodeKind(JsonNode node)
        {
            if (node is JsonObject) return "object";
            if (node is JsonArray) return "array";

            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out _)) return "bool";
                if (value.TryGetValue<long>(out _)) return "number";
                if (value.TryGetValue<double>(out _)) return "number";
                if (value.TryGetValue<string>(out _)) return "string";
                return "string";
            }

            return "string";
        }

        private static string GetLeafValue(JsonNode node, string kind)
        {
            if (kind is "object" or "array") return string.Empty;
            if (kind == "null") return string.Empty;

            if (node is JsonValue value)
            {
                if (kind == "bool" && value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                if (kind == "number" && value.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
                if (kind == "number" && value.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
                if (value.TryGetValue<string>(out var s)) return s ?? string.Empty;
                return value.ToString() ?? string.Empty;
            }

            return node.ToString() ?? string.Empty;
        }

        private static void SetValueByPointer(JsonNode root, string jsonPointer, string? type, string? rawValue)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (string.IsNullOrWhiteSpace(jsonPointer)) return;

            var pointer = jsonPointer.Trim();
            if (pointer == "/") return;
            if (!pointer.StartsWith('/'))
            {
                throw new InvalidOperationException("JSON Pointer non valido.");
            }

            var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(UnescapePointerToken)
                .ToArray();

            if (segments.Length == 0) return;

            JsonNode current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                var nextSeg = segments[i + 1];

                if (current is JsonObject currentObj)
                {
                    if (currentObj[seg] == null)
                    {
                        currentObj[seg] = IsArrayIndex(nextSeg) ? new JsonArray() : new JsonObject();
                    }
                    current = currentObj[seg]!;
                    continue;
                }

                if (current is JsonArray currentArr)
                {
                    if (!int.TryParse(seg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || idx < 0)
                    {
                        throw new InvalidOperationException("Indice array non valido nel puntatore.");
                    }
                    EnsureArraySize(currentArr, idx + 1);
                    if (currentArr[idx] == null)
                    {
                        currentArr[idx] = IsArrayIndex(nextSeg) ? new JsonArray() : new JsonObject();
                    }
                    current = currentArr[idx]!;
                    continue;
                }

                throw new InvalidOperationException("Percorso non modificabile: nodo intermedio non è container.");
            }

            var leafSeg = segments[^1];
            var normalizedType = string.IsNullOrWhiteSpace(type) ? "string" : type.Trim().ToLowerInvariant();
            var value = rawValue ?? string.Empty;

            if (current is JsonObject leafObj)
            {
                leafObj[leafSeg] = CreateTypedNode(normalizedType, value);
                return;
            }

            if (current is JsonArray leafArr)
            {
                if (!int.TryParse(leafSeg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leafIdx) || leafIdx < 0)
                {
                    throw new InvalidOperationException("Indice array non valido nel puntatore.");
                }
                EnsureArraySize(leafArr, leafIdx + 1);
                leafArr[leafIdx] = CreateTypedNode(normalizedType, value);
                return;
            }

            throw new InvalidOperationException("Nodo destinazione non modificabile.");
        }

        private static JsonNode? CreateTypedNode(string normalizedType, string value)
        {
            switch (normalizedType)
            {
                case "bool":
                    if (bool.TryParse(value, out var boolValue)) return JsonValue.Create(boolValue);
                    throw new InvalidOperationException("Valore booleano non valido.");
                case "number":
                    if (TryParseNumber(value, out var numberNode)) return numberNode;
                    throw new InvalidOperationException("Valore numerico non valido.");
                case "null":
                    return null;
                default:
                    return JsonValue.Create(value);
            }
        }

        private static bool IsArrayIndex(string segment)
        {
            return int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0;
        }

        private static void EnsureArraySize(JsonArray arr, int size)
        {
            while (arr.Count < size)
            {
                arr.Add(null);
            }
        }

        private static string CombinePointer(string parentPointer, string token)
        {
            if (string.IsNullOrWhiteSpace(parentPointer)) return "/" + token;
            if (parentPointer == "/") return "/" + token;
            return parentPointer + "/" + token;
        }

        private static string EscapePointerToken(string token)
        {
            return token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        }

        private static string UnescapePointerToken(string token)
        {
            return token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }

        private static string EncodeFormKey(string pointer)
        {
            // base64url, safe for form field keys
            var bytes = Encoding.UTF8.GetBytes(pointer ?? string.Empty);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string DecodeFormKey(string formKey)
        {
            if (string.IsNullOrWhiteSpace(formKey)) return string.Empty;

            var padded = formKey.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            var bytes = Convert.FromBase64String(padded);
            return Encoding.UTF8.GetString(bytes);
        }

        private static bool TryParseNumber(string value, out JsonNode node)
        {
            node = JsonValue.Create(0);
            if (string.IsNullOrWhiteSpace(value)) return false;

            if (value.Contains('.') || value.Contains(','))
            {
                var normalized = value.Replace(',', '.');
                if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                {
                    node = JsonValue.Create(doubleValue);
                    return true;
                }
                return false;
            }

            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
            {
                node = JsonValue.Create(longValue);
                return true;
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fallback))
            {
                node = JsonValue.Create(fallback);
                return true;
            }

            return false;
        }

        public sealed class JsonTreeNode
        {
            public string DisplayName { get; init; } = string.Empty;
            public string JsonPointer { get; init; } = string.Empty;
            public string FormKey { get; init; } = string.Empty;
            public string Kind { get; init; } = "string";
            public int Depth { get; init; }
            public string Value { get; init; } = string.Empty;
            public List<JsonTreeNode> Children { get; } = new();
        }
    }
}
