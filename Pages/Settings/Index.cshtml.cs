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

        public List<SettingEntry> Settings { get; set; } = new();

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
            LoadSettings();
        }

        public IActionResult OnPostSave(List<SettingEntryInput> settings)
        {
            try
            {
                var path = GetSettingsPath();
                if (!System.IO.File.Exists(path))
                {
                    StatusMessage = "appsettings.json non trovato.";
                    LoadSettings();
                    return Page();
                }

                var json = System.IO.File.ReadAllText(path);
                var root = JsonNode.Parse(json) as JsonObject;
                if (root == null)
                {
                    StatusMessage = "Impossibile leggere appsettings.json.";
                    LoadSettings();
                    return Page();
                }

                var errors = new List<string>();
                foreach (var entry in settings ?? new List<SettingEntryInput>())
                {
                    if (string.IsNullOrWhiteSpace(entry.Path))
                    {
                        continue;
                    }

                    try
                    {
                        SetValue(root, entry.Path, entry.Type, entry.Value);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{entry.Path}: {ex.Message}");
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

        private void LoadSettings()
        {
            var path = GetSettingsPath();
            if (!System.IO.File.Exists(path))
            {
                Settings = new List<SettingEntry>();
                StatusMessage = "appsettings.json non trovato.";
                return;
            }

            var json = System.IO.File.ReadAllText(path);
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null)
            {
                Settings = new List<SettingEntry>();
                StatusMessage = "Impossibile leggere appsettings.json.";
                return;
            }

            var list = new List<SettingEntry>();
            Flatten(root, string.Empty, list);
            Settings = list.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToList();
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

        private static void Flatten(JsonNode node, string path, List<SettingEntry> list)
        {
            if (node is JsonObject obj)
            {
                foreach (var kvp in obj)
                {
                    var nextPath = string.IsNullOrWhiteSpace(path) ? kvp.Key : $"{path}:{kvp.Key}";
                    if (kvp.Value != null)
                    {
                        Flatten(kvp.Value, nextPath, list);
                    }
                }
                return;
            }

            if (node is JsonArray array)
            {
                var json = array.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
                list.Add(new SettingEntry(path, json, "json", ComputeDepth(path)));
                return;
            }

            if (node is JsonValue value)
            {
                if (value.TryGetValue<bool>(out var boolValue))
                {
                    list.Add(new SettingEntry(path, boolValue ? "true" : "false", "bool", ComputeDepth(path)));
                    return;
                }
                if (value.TryGetValue<long>(out var longValue))
                {
                    list.Add(new SettingEntry(path, longValue.ToString(CultureInfo.InvariantCulture), "number", ComputeDepth(path)));
                    return;
                }
                if (value.TryGetValue<double>(out var doubleValue))
                {
                    list.Add(new SettingEntry(path, doubleValue.ToString(CultureInfo.InvariantCulture), "number", ComputeDepth(path)));
                    return;
                }
                if (value.TryGetValue<string>(out var stringValue))
                {
                    list.Add(new SettingEntry(path, stringValue ?? string.Empty, "string", ComputeDepth(path)));
                    return;
                }

                list.Add(new SettingEntry(path, value.ToString() ?? string.Empty, "string", ComputeDepth(path)));
            }
        }

        private static int ComputeDepth(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return 0;
            return Math.Max(0, path.Split(':', StringSplitOptions.RemoveEmptyEntries).Length - 1);
        }

        private static void SetValue(JsonObject root, string path, string? type, string? rawValue)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            var segments = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return;

            JsonObject current = root;
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var seg = segments[i];
                if (current[seg] is JsonObject childObj)
                {
                    current = childObj;
                    continue;
                }

                var created = new JsonObject();
                current[seg] = created;
                current = created;
            }

            var key = segments[^1];
            var normalizedType = string.IsNullOrWhiteSpace(type) ? "string" : type.Trim().ToLowerInvariant();
            var value = rawValue ?? string.Empty;

            switch (normalizedType)
            {
                case "bool":
                    if (bool.TryParse(value, out var boolValue))
                    {
                        current[key] = JsonValue.Create(boolValue);
                    }
                    else
                    {
                        throw new InvalidOperationException("Valore booleano non valido.");
                    }
                    break;
                case "number":
                    if (TryParseNumber(value, out var numberNode))
                    {
                        current[key] = numberNode;
                    }
                    else
                    {
                        throw new InvalidOperationException("Valore numerico non valido.");
                    }
                    break;
                case "json":
                    try
                    {
                        var parsed = JsonNode.Parse(value);
                        if (parsed == null)
                        {
                            throw new InvalidOperationException("JSON non valido.");
                        }
                        current[key] = parsed;
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidOperationException(ex.Message);
                    }
                    break;
                case "null":
                    current[key] = null;
                    break;
                default:
                    current[key] = JsonValue.Create(value);
                    break;
            }
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

        public sealed record SettingEntry(string Path, string Value, string Type, int Depth);

        public sealed class SettingEntryInput
        {
            public string Path { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
            public string Type { get; set; } = "string";
        }
    }
}
