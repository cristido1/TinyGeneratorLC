using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    /// <summary>
    /// Command that assigns TTS voices to characters in a story's tts_schema.json.
    /// Rules:
    /// - Narratore must use only voices with archetype "narratore".
    /// - Prefer unique voiceId per character; if a specific gender pool is exhausted,
    ///   allow controlled reuse of a compatible voice to avoid blocking prepare_tts_schema.
    /// - Character voice must match required gender.
    /// </summary>
    internal sealed class AssignVoicesCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public AssignVoicesCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false; // Only needs Characters JSON
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var story = context.Story;
                var folderPath = context.FolderPath;

                var schemaPath = Path.Combine(folderPath, "tts_schema.json");
                if (!File.Exists(schemaPath))
                    return Task.FromResult<(bool, string?)>((false, "File tts_schema.json mancante: genera prima lo schema TTS"));

                var schemaJson = File.ReadAllText(schemaPath);
                var schema = JsonSerializer.Deserialize<TtsSchema>(schemaJson, SchemaJsonOptions);
                if (schema?.Characters == null || schema.Characters.Count == 0)
                    return Task.FromResult<(bool, string?)>((false, "Nessun personaggio definito nello schema TTS"));

                var allVoices = _service._database.ListTtsVoices(onlyEnabled: true);
                if (allVoices == null || allVoices.Count == 0)
                    return Task.FromResult<(bool, string?)>((false, "Nessuna voce disponibile nella tabella tts_voices"));

                var voicesById = allVoices
                    .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
                    .GroupBy(v => v.VoiceId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var storyCharacters = new List<StoryCharacter>();
                if (!string.IsNullOrWhiteSpace(story.Characters))
                {
                    try
                    {
                        storyCharacters = StoryCharacterParser.FromJson(story.Characters);
                    }
                    catch
                    {
                        // Best effort.
                    }
                }

                var usedVoiceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var errors = new List<string>();
                int assignedCount = 0;
                int replacedCount = 0;
                int reusedCount = 0;

                var narrator = schema.Characters.FirstOrDefault(c => IsNarratorName(c.Name));
                if (narrator != null)
                {
                    var original = narrator.VoiceId;
                    var selectedNarrator = SelectNarratorVoice(story, narrator, allVoices, voicesById, usedVoiceIds);
                    if (selectedNarrator == null)
                    {
                        errors.Add("Narratore: nessuna voce disponibile con archetype='narratore'.");
                    }
                    else
                    {
                        narrator.VoiceId = selectedNarrator.VoiceId;
                        narrator.Voice = selectedNarrator.Name;
                        if (string.IsNullOrWhiteSpace(narrator.Gender))
                            narrator.Gender = NormalizeGender(selectedNarrator.Gender);

                        usedVoiceIds.Add(selectedNarrator.VoiceId);
                        assignedCount++;
                        if (!string.Equals(original, selectedNarrator.VoiceId, StringComparison.OrdinalIgnoreCase))
                            replacedCount++;
                    }
                }

                foreach (var character in schema.Characters)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (IsNarratorName(character.Name))
                        continue;

                    var storyChar = storyCharacters.FirstOrDefault(sc =>
                        string.Equals(sc.Name, character.Name, StringComparison.OrdinalIgnoreCase) ||
                        (sc.Aliases?.Any(a => string.Equals(a, character.Name, StringComparison.OrdinalIgnoreCase)) == true));

                    var targetGender = NormalizeGender(storyChar?.Gender ?? character.Gender);
                    if (string.IsNullOrWhiteSpace(targetGender))
                        targetGender = "male";

                    var original = character.VoiceId;
                    TinyGenerator.Models.TtsVoice? selected = null;

                    if (!string.IsNullOrWhiteSpace(original) &&
                        voicesById.TryGetValue(original, out var existing) &&
                        !usedVoiceIds.Contains(existing.VoiceId) &&
                        !IsNarratorVoice(existing) &&
                        VoiceGenderMatches(existing, targetGender))
                    {
                        selected = existing;
                    }
                    else
                    {
                        selected = PickBestCharacterVoice(targetGender, storyChar?.Age, allVoices, usedVoiceIds);
                    }

                    // Fallback per gender "unknown"/non disponibile: non bloccare il prepare_tts_schema
                    // se esiste almeno una voce personaggio utilizzabile.
                    if (selected == null && IsUnknownOrUnspecifiedGender(targetGender))
                    {
                        selected = PickBestCharacterVoiceAnyGender(storyChar?.Age, allVoices, usedVoiceIds);
                        if (selected != null)
                        {
                            var selectedGender = NormalizeGender(selected.Gender);
                            if (!string.IsNullOrWhiteSpace(selectedGender))
                                targetGender = selectedGender;
                        }
                    }

                    // Last-resort fallback: if the required gender exists but all matching voices are already used,
                    // allow reuse of a compatible character voice so prepare_tts_schema can complete.
                    if (selected == null && !IsUnknownOrUnspecifiedGender(targetGender))
                    {
                        selected = PickBestCharacterVoiceAllowReuse(targetGender, storyChar?.Age, allVoices);
                        if (selected != null)
                        {
                            reusedCount++;
                        }
                    }

                    if (selected == null)
                    {
                        errors.Add($"{character.Name}: nessuna voce disponibile (gender richiesto: {targetGender}).");
                        continue;
                    }

                    character.VoiceId = selected.VoiceId;
                    character.Voice = selected.Name;
                    character.Gender = targetGender;

                    usedVoiceIds.Add(selected.VoiceId);
                    assignedCount++;
                    if (!string.Equals(original, selected.VoiceId, StringComparison.OrdinalIgnoreCase))
                        replacedCount++;
                }

                if (errors.Count > 0)
                {
                    return Task.FromResult<(bool, string?)>((false,
                        $"Assegnazione voci fallita: {string.Join(" | ", errors)}"));
                }

                var duplicates = schema.Characters
                    .Where(c => !string.IsNullOrWhiteSpace(c.VoiceId))
                    .GroupBy(c => c.VoiceId!, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{g.Key}: {string.Join(", ", g.Select(c => c.Name ?? "?"))}")
                    .ToList();

                if (duplicates.Any() && reusedCount == 0)
                {
                    return Task.FromResult<(bool, string?)>((false,
                        $"Violazione unicita voiceId: {string.Join("; ", duplicates)}"));
                }

                var outputOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var updatedJson = JsonSerializer.Serialize(schema, outputOptions);
                _service.SaveSanitizedTtsSchemaJson(schemaPath, updatedJson);

                _service._logger?.LogInformation(
                    "Assigned/revalidated voices for story {StoryId}: assigned={Assigned}, replaced={Replaced}, reused={Reused}, total={Total}",
                    story.Id, assignedCount, replacedCount, reusedCount, schema.Characters.Count);

                return Task.FromResult<(bool, string?)>((true,
                    $"Assegnazione voci completata: {assignedCount} assegnazioni, {replacedCount} riassegnazioni, {reusedCount} riusi, {schema.Characters.Count} personaggi."));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante l'assegnazione voci per la storia {Id}", context.Story.Id);
                return Task.FromResult<(bool, string?)>((false, ex.Message));
            }
        }

        private TinyGenerator.Models.TtsVoice? SelectNarratorVoice(
            StoryRecord story,
            TtsCharacter narrator,
            List<TinyGenerator.Models.TtsVoice> allVoices,
            Dictionary<string, TinyGenerator.Models.TtsVoice> voicesById,
            HashSet<string> usedVoiceIds)
        {
            if (!string.IsNullOrWhiteSpace(narrator.VoiceId) &&
                voicesById.TryGetValue(narrator.VoiceId, out var currentNarratorVoice) &&
                IsNarratorVoice(currentNarratorVoice) &&
                !usedVoiceIds.Contains(currentNarratorVoice.VoiceId))
            {
                return currentNarratorVoice;
            }

            if (story.SerieId.HasValue)
            {
                try
                {
                    var seriesChars = _service._database.ListSeriesCharacters(story.SerieId.Value);
                    var seriesNarrator = seriesChars.FirstOrDefault(c => IsNarratorName(c.Name));
                    if (seriesNarrator?.VoiceId.HasValue == true)
                    {
                        var seriesVoice = allVoices.FirstOrDefault(v => v.Id == seriesNarrator.VoiceId.Value);
                        if (seriesVoice != null &&
                            IsNarratorVoice(seriesVoice) &&
                            !usedVoiceIds.Contains(seriesVoice.VoiceId))
                        {
                            return seriesVoice;
                        }
                    }
                }
                catch
                {
                    // Best effort.
                }
            }

            var defaultVoiceId = _service.GetNarratorDefaultVoiceId();
            if (!string.IsNullOrWhiteSpace(defaultVoiceId))
            {
                var defaultVoice = _service.FindVoiceByNameOrId(allVoices, defaultVoiceId);
                if (defaultVoice != null &&
                    IsNarratorVoice(defaultVoice) &&
                    !usedVoiceIds.Contains(defaultVoice.VoiceId))
                {
                    return defaultVoice;
                }
            }

            return PickBestNarratorVoice(allVoices, usedVoiceIds);
        }

        private static TinyGenerator.Models.TtsVoice? PickBestNarratorVoice(
            List<TinyGenerator.Models.TtsVoice> allVoices,
            HashSet<string> usedVoiceIds)
        {
            return allVoices
                .Where(v =>
                    !string.IsNullOrWhiteSpace(v.VoiceId) &&
                    !usedVoiceIds.Contains(v.VoiceId) &&
                    IsNarratorVoice(v))
                .OrderByDescending(v => v.Score ?? 0)
                .ThenByDescending(v => v.Confidence ?? 0)
                .FirstOrDefault();
        }

        private static TinyGenerator.Models.TtsVoice? PickBestCharacterVoice(
            string targetGender,
            string? targetAge,
            List<TinyGenerator.Models.TtsVoice> allVoices,
            HashSet<string> usedVoiceIds)
        {
            var candidates = allVoices
                .Where(v =>
                    !string.IsNullOrWhiteSpace(v.VoiceId) &&
                    !usedVoiceIds.Contains(v.VoiceId) &&
                    !IsNarratorVoice(v) &&
                    VoiceGenderMatches(v, targetGender))
                .ToList();

            if (candidates.Count == 0)
                return null;

            int? targetAgeNum = ParseAgeToNumber(targetAge);

            return candidates
                .Select(v => new
                {
                    Voice = v,
                    Score = v.Score ?? 0,
                    AgeDiff = CalculateAgeDifference(v.Age, targetAgeNum)
                })
                .OrderBy(x => x.AgeDiff)
                .ThenByDescending(x => x.Score)
                .Select(x => x.Voice)
                .FirstOrDefault();
        }

        private static TinyGenerator.Models.TtsVoice? PickBestCharacterVoiceAnyGender(
            string? targetAge,
            List<TinyGenerator.Models.TtsVoice> allVoices,
            HashSet<string> usedVoiceIds)
        {
            var candidates = allVoices
                .Where(v =>
                    !string.IsNullOrWhiteSpace(v.VoiceId) &&
                    !usedVoiceIds.Contains(v.VoiceId) &&
                    !IsNarratorVoice(v))
                .ToList();

            if (candidates.Count == 0)
                return null;

            // Preserve scarce special voices (robot/alien) for explicit gender requests.
            var preferred = candidates
                .Where(v =>
                {
                    var g = NormalizeGender(v.Gender);
                    return g == "male" || g == "female" || g == "neutral";
                })
                .ToList();
            if (preferred.Count > 0)
            {
                candidates = preferred;
            }

            int? targetAgeNum = ParseAgeToNumber(targetAge);

            return candidates
                .Select(v => new
                {
                    Voice = v,
                    Score = v.Score ?? 0,
                    AgeDiff = CalculateAgeDifference(v.Age, targetAgeNum)
                })
                .OrderBy(x => x.AgeDiff)
                .ThenByDescending(x => x.Score)
                .Select(x => x.Voice)
                .FirstOrDefault();
        }

        private static TinyGenerator.Models.TtsVoice? PickBestCharacterVoiceAllowReuse(
            string targetGender,
            string? targetAge,
            List<TinyGenerator.Models.TtsVoice> allVoices)
        {
            var candidates = allVoices
                .Where(v =>
                    !string.IsNullOrWhiteSpace(v.VoiceId) &&
                    !IsNarratorVoice(v) &&
                    VoiceGenderMatches(v, targetGender))
                .ToList();

            if (candidates.Count == 0)
                return null;

            int? targetAgeNum = ParseAgeToNumber(targetAge);

            return candidates
                .Select(v => new
                {
                    Voice = v,
                    Score = v.Score ?? 0,
                    AgeDiff = CalculateAgeDifference(v.Age, targetAgeNum)
                })
                .OrderBy(x => x.AgeDiff)
                .ThenByDescending(x => x.Score)
                .Select(x => x.Voice)
                .FirstOrDefault();
        }

        private static bool IsNarratorName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            return name.Equals("Narratore", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Narrator", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeGender(string? gender)
        {
            if (string.IsNullOrWhiteSpace(gender))
                return string.Empty;

            var g = gender.Trim().ToLowerInvariant();
            return g switch
            {
                "male" or "m" or "maschio" or "uomo" => "male",
                "female" or "f" or "femmina" or "donna" => "female",
                "alien" or "alieno" or "extraterrestre" => "alien",
                "robot" or "android" or "androide" => "robot",
                "neutral" or "neutro" or "other" => "neutral",
                _ => g
            };
        }

        private static bool IsUnknownOrUnspecifiedGender(string? gender)
        {
            var normalized = NormalizeGender(gender);
            return string.IsNullOrWhiteSpace(normalized)
                || normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("neutral", StringComparison.OrdinalIgnoreCase);
        }

        private static bool VoiceGenderMatches(TinyGenerator.Models.TtsVoice voice, string expectedGender)
        {
            var voiceGender = NormalizeGender(voice.Gender);
            var target = NormalizeGender(expectedGender);
            return !string.IsNullOrWhiteSpace(target)
                && !string.IsNullOrWhiteSpace(voiceGender)
                && voiceGender.Equals(target, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNarratorVoice(TinyGenerator.Models.TtsVoice voice)
        {
            var archetype = (voice.Archetype ?? string.Empty).Trim();
            return archetype.Equals("narratore", StringComparison.OrdinalIgnoreCase)
                || archetype.Equals("narrator", StringComparison.OrdinalIgnoreCase);
        }

        private static int? ParseAgeToNumber(string? age)
        {
            if (string.IsNullOrWhiteSpace(age))
                return null;

            if (int.TryParse(age, out int numericAge))
                return numericAge;

            var ageLower = age.ToLowerInvariant();
            return ageLower switch
            {
                "bambino" or "child" or "kid" => 8,
                "ragazzo" or "giovane" or "young" or "teen" or "teenager" => 18,
                "adulto" or "adult" => 35,
                "mezza eta" or "middle-aged" or "middle aged" => 50,
                "anziano" or "elderly" or "old" => 70,
                _ => null
            };
        }

        private static int CalculateAgeDifference(string? voiceAge, int? targetAge)
        {
            if (!targetAge.HasValue)
                return 0;

            var voiceAgeNum = ParseAgeToNumber(voiceAge);
            if (!voiceAgeNum.HasValue)
                return 100;

            return Math.Abs(voiceAgeNum.Value - targetAge.Value);
        }
    }
}
