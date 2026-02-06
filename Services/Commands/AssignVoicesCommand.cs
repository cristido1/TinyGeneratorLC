using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    /// <summary>
    /// Command that assigns TTS voices to characters in a story's tts_schema.json.
    /// Narrator gets a random voice with archetype "narratore".
    /// Characters get voices matching gender and closest age, preferring higher scores.
    /// Each character must have a distinct voice.
    /// </summary>
    internal sealed class AssignVoicesCommand : IStoryCommand
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
                var story = context.Story;
                var folderPath = context.FolderPath;

                var schemaPath = Path.Combine(folderPath, "tts_schema.json");
                if (!File.Exists(schemaPath))
                    return Task.FromResult<(bool, string?)>((false, "File tts_schema.json mancante: genera prima lo schema TTS"));

                // Load TTS schema
                var schemaJson = File.ReadAllText(schemaPath);
                var schema = JsonSerializer.Deserialize<TtsSchema>(schemaJson, SchemaJsonOptions);
                if (schema?.Characters == null || schema.Characters.Count == 0)
                    return Task.FromResult<(bool, string?)>((false, "Nessun personaggio definito nello schema TTS"));

                // Load available voices from database (only enabled voices for assignment)
                var allVoices = _service._database.ListTtsVoices(onlyEnabled: true);
                if (allVoices == null || allVoices.Count == 0)
                    return Task.FromResult<(bool, string?)>((false, "Nessuna voce disponibile nella tabella tts_voices"));

                // Load story characters for age/gender info
                var storyCharacters = new List<StoryCharacter>();
                if (!string.IsNullOrWhiteSpace(story.Characters))
                {
                    try
                    {
                        storyCharacters = StoryCharacterParser.FromJson(story.Characters);
                    }
                    catch
                    {
                        // Best effort - proceed without character metadata
                    }
                }

                // Track used voice IDs to ensure uniqueness
                var usedVoiceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int assignedCount = 0;

                // 1. Assign narrator voice first (series narrator -> appsettings default -> archetype fallback)
                var narrator = schema.Characters.FirstOrDefault(c =>
                    string.Equals(c.Name, "Narratore", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(c.Name, "Narrator", StringComparison.OrdinalIgnoreCase));

                if (narrator != null)
                {
                    if (!string.IsNullOrWhiteSpace(narrator.VoiceId))
                    {
                        usedVoiceIds.Add(narrator.VoiceId);
                    }
                    else
                    {
                        TinyGenerator.Models.TtsVoice? selectedNarratorVoice = null;

                        // Series narrator voice (if defined)
                        if (story.SerieId.HasValue)
                        {
                            try
                            {
                                var seriesChars = _service._database.ListSeriesCharacters(story.SerieId.Value);
                                var seriesNarrator = seriesChars.FirstOrDefault(c =>
                                    string.Equals(c.Name, "Narratore", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(c.Name, "Narrator", StringComparison.OrdinalIgnoreCase));
                                if (seriesNarrator?.VoiceId.HasValue == true)
                                {
                                    selectedNarratorVoice = _service._database.GetTtsVoiceById(seriesNarrator.VoiceId.Value);
                                }
                            }
                            catch
                            {
                                // best-effort
                            }
                        }

                        // Appsettings default narrator voice (by name or voiceId)
                        if (selectedNarratorVoice == null)
                        {
                            var defaultVoiceId = _service.GetNarratorDefaultVoiceId();
                            if (!string.IsNullOrWhiteSpace(defaultVoiceId))
                            {
                                selectedNarratorVoice = _service.FindVoiceByNameOrId(allVoices, defaultVoiceId);
                            }
                        }

                        // Fallback: pick archetype narrator voice (if any), else best available male voice
                        if (selectedNarratorVoice == null)
                        {
                            var narratorVoices = allVoices
                                .Where(v => !string.IsNullOrWhiteSpace(v.Archetype) &&
                                           v.Archetype.Equals("narratore", StringComparison.OrdinalIgnoreCase) &&
                                           !string.IsNullOrWhiteSpace(v.VoiceId))
                                .ToList();

                            if (narratorVoices.Count > 0)
                            {
                                selectedNarratorVoice = narratorVoices[Random.Shared.Next(narratorVoices.Count)];
                            }
                            else
                            {
                                selectedNarratorVoice = PickBestAvailableVoice("male", null, allVoices, usedVoiceIds);
                            }
                        }

                        if (selectedNarratorVoice != null)
                        {
                            narrator.VoiceId = selectedNarratorVoice.VoiceId;
                            narrator.Voice = selectedNarratorVoice.Name;
                            narrator.Gender = selectedNarratorVoice.Gender ?? "";
                            usedVoiceIds.Add(selectedNarratorVoice.VoiceId);
                            assignedCount++;
                        }
                    }
                }

                // 2. Assign voices to other characters
                foreach (var character in schema.Characters)
                {
                    // Skip narrator (already assigned) and characters with existing voice
                    if (string.Equals(character.Name, "Narratore", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(character.Name, "Narrator", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrWhiteSpace(character.VoiceId))
                    {
                        usedVoiceIds.Add(character.VoiceId);
                        continue;
                    }

                    // Find matching story character for age/gender info
                    var storyChar = storyCharacters.FirstOrDefault(sc =>
                        string.Equals(sc.Name, character.Name, StringComparison.OrdinalIgnoreCase) ||
                        (sc.Aliases?.Any(a => string.Equals(a, character.Name, StringComparison.OrdinalIgnoreCase)) == true));

                    // Determine gender (from story character or TTS character)
                    var gender = storyChar?.Gender ?? character.Gender;
                    if (string.IsNullOrWhiteSpace(gender))
                        gender = "male"; // Default fallback

                    // Determine age (from story character)
                    var age = storyChar?.Age;

                    // Pick best voice matching criteria
                    var selectedVoice = PickBestAvailableVoice(gender, age, allVoices, usedVoiceIds);
                    if (selectedVoice != null)
                    {
                        character.VoiceId = selectedVoice.VoiceId;
                        character.Voice = selectedVoice.Name;
                        if (string.IsNullOrWhiteSpace(character.Gender))
                            character.Gender = selectedVoice.Gender ?? "";
                        usedVoiceIds.Add(selectedVoice.VoiceId);
                        assignedCount++;
                    }
                    else
                    {
                        _service._logger?.LogWarning(
                            "No available voice for character {Name} (gender={Gender}, age={Age})",
                            character.Name, gender, age ?? "unknown");
                    }
                }

                // Validate: all characters must have a voice
                var missingVoices = schema.Characters
                    .Where(c => string.IsNullOrWhiteSpace(c.VoiceId))
                    .Select(c => c.Name ?? "<senza nome>")
                    .ToList();

                if (missingVoices.Any())
                {
                    return Task.FromResult<(bool, string?)>((false,
                        $"Non è stato possibile assegnare voci a: {string.Join(", ", missingVoices)}. " +
                        $"Aggiungi altre voci nella tabella tts_voices."));
                }

                // Check for duplicate voices
                var duplicates = schema.Characters
                    .Where(c => !string.IsNullOrWhiteSpace(c.VoiceId))
                    .GroupBy(c => c.VoiceId!, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{g.Key}: {string.Join(", ", g.Select(c => c.Name ?? "?"))}")
                    .ToList();

                if (duplicates.Any())
                {
                    _service._logger?.LogWarning("Duplicate voices assigned: {Duplicates}", string.Join("; ", duplicates));
                }

                // Save updated schema
                var outputOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var updatedJson = JsonSerializer.Serialize(schema, outputOptions);
                _service.SaveSanitizedTtsSchemaJson(schemaPath, updatedJson);

                _service._logger?.LogInformation(
                    "Assigned {Count} voices to characters in story {StoryId}",
                    assignedCount, story.Id);

                return Task.FromResult<(bool, string?)>((true,
                    $"Assegnate {assignedCount} voci a {schema.Characters.Count} personaggi."));
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante l'assegnazione voci per la storia {Id}", context.Story.Id);
                return Task.FromResult<(bool, string?)>((false, ex.Message));
            }
        }

        /// <summary>
        /// Picks the best available voice matching gender and age criteria.
        /// Priority: same gender > closest age > highest score
        /// </summary>
        private static TinyGenerator.Models.TtsVoice? PickBestAvailableVoice(
            string gender,
            string? targetAge,
            List<TinyGenerator.Models.TtsVoice> allVoices,
            HashSet<string> usedVoiceIds)
        {
            // Filter by gender and exclude already used voices
            var candidates = allVoices
                .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId) &&
                           !usedVoiceIds.Contains(v.VoiceId) &&
                           string.Equals(v.Gender, gender, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // If no candidates of same gender, try all unused voices
            if (candidates.Count == 0)
            {
                candidates = allVoices
                    .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId) &&
                               !usedVoiceIds.Contains(v.VoiceId))
                    .ToList();
            }

            if (candidates.Count == 0)
                return null;

            // Parse target age to numeric if possible
            int? targetAgeNum = ParseAgeToNumber(targetAge);

            // Score each candidate
            var scoredCandidates = candidates
                .Select(v => new
                {
                    Voice = v,
                    Score = v.Score ?? 0,
                    AgeDiff = CalculateAgeDifference(v.Age, targetAgeNum)
                })
                .OrderBy(x => x.AgeDiff)        // Closest age first
                .ThenByDescending(x => x.Score) // Then highest score
                .ToList();

            return scoredCandidates.First().Voice;
        }

        /// <summary>
        /// Parses age string to a numeric value. Handles numeric ages and descriptive ages.
        /// </summary>
        private static int? ParseAgeToNumber(string? age)
        {
            if (string.IsNullOrWhiteSpace(age))
                return null;

            // Try direct numeric parse
            if (int.TryParse(age, out int numericAge))
                return numericAge;

            // Handle descriptive ages
            var ageLower = age.ToLowerInvariant();
            return ageLower switch
            {
                "bambino" or "child" or "kid" => 8,
                "ragazzo" or "giovane" or "young" or "teen" or "teenager" => 18,
                "adulto" or "adult" => 35,
                "mezza età" or "middle-aged" or "middle aged" => 50,
                "anziano" or "elderly" or "old" => 70,
                _ => null
            };
        }

        /// <summary>
        /// Calculates age difference. Returns 0 if no comparison possible.
        /// </summary>
        private static int CalculateAgeDifference(string? voiceAge, int? targetAge)
        {
            if (!targetAge.HasValue)
                return 0; // No preference, treat as equal

            var voiceAgeNum = ParseAgeToNumber(voiceAge);
            if (!voiceAgeNum.HasValue)
                return 100; // Penalize voices without age info

            return Math.Abs(voiceAgeNum.Value - targetAge.Value);
        }
    }
}
