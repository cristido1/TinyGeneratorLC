using System;
using System.Collections.Generic;
using System.Linq;

namespace TinyGenerator.Services
{
    public static class PluginHelpers
    {
        private static readonly Dictionary<string, string> AliasMap = new(StringComparer.OrdinalIgnoreCase)
        {
            {"filesystem", "filesystem"},
            {"file", "filesystem"},
            {"files", "filesystem"},
            {"http", "http"},
            {"math", "math"},
            {"memory", "memory"},
            {"storyevaluator", "evaluator"},
            {"evaluator", "evaluator"},
            {"storywriter", "story"},
            {"story", "story"},
            {"text", "text"},
            {"time", "time"},
            {"tts", "tts"},
            {"ttsapi", "tts"},
            {"audiocraft", "audiocraft"},
            {"audioevaluator", "audioevaluator"},
            {"audio_evaluator", "audioevaluator"},
            {"audio-evaluator", "audioevaluator"}
        };

        public static string? Normalize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var s = name.Trim();
            // Remove common suffixes
            if (s.EndsWith("Skill", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - "Skill".Length);
            if (s.EndsWith("Plugin", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - "Plugin".Length);
            // Lowercase and remove spaces/underscores
            var key = s.Replace(" ", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            if (AliasMap.TryGetValue(key, out var alias)) return alias;
            // If we don't have an explicit mapping, return the lower-case key and allow KernelFactory to ignore unknowns
            return key;
        }

        public static IEnumerable<string> NormalizeList(IEnumerable<string>? names)
        {
            if (names == null) return Array.Empty<string>();
            return names.Select(Normalize).Where(x => !string.IsNullOrWhiteSpace(x))!;
        }
    }
}
