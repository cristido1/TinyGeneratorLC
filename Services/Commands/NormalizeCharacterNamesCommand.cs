using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Models;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    /// <summary>
    /// Command to normalize character names in tts_schema.json using the story's character list.
    /// </summary>
    private sealed class NormalizeCharacterNamesCommand : IStoryCommand
    {
        private readonly StoriesService _service;

        public NormalizeCharacterNamesCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            var story = context.Story;
            var folderPath = context.FolderPath;

            // Load character list - from series_characters if story belongs to a series, otherwise from story.Characters
            List<StoryCharacter> storyCharacters;

            if (story.SerieId.HasValue)
            {
                var seriesChars = _service._database.ListSeriesCharacters(story.SerieId.Value);
                if (!seriesChars.Any())
                {
                    return (false, $"La serie {story.SerieId.Value} non ha personaggi definiti nella tabella series_characters.");
                }

                storyCharacters = seriesChars.Select(sc => new StoryCharacter
                {
                    Name = sc.Name,
                    Gender = sc.Gender,
                    Age = sc.Eta,
                    Role = sc.Profilo,
                    Aliases = sc.Name.Contains(' ')
                        ? new List<string> { sc.Name.Split(' ').Last() }
                        : null
                }).ToList();

                _service._logger?.LogInformation(
                    "Loaded {CharCount} characters from series_characters for story {StoryId} (serieId={SerieId})",
                    storyCharacters.Count, story.Id, story.SerieId.Value);
            }
            else
            {
                // Check if story has character data
                if (string.IsNullOrWhiteSpace(story.Characters))
                {
                    var maxAttempts = _service._ttsSchemaOptions?.CurrentValue?.CharacterExtractionMaxAttempts ?? 3;
                    var extracted = await _service.TryAutoExtractCharactersAsync(story, maxAttempts);
                    if (!extracted.success)
                    {
                        return (false, extracted.error ?? "La storia non ha una lista di personaggi definita nel campo Characters.");
                    }

                    storyCharacters = extracted.characters;
                    _service._database.UpdateStoryCharacters(story.Id, StoryCharacterParser.ToJson(storyCharacters));
                }
                else
                {
                    // Load character list from story with detailed error
                    var (chars, parseError) = StoryCharacterParser.TryFromJson(story.Characters);
                    if (parseError != null)
                    {
                        return (false, $"Errore nel parsing della lista personaggi: {parseError}");
                    }
                    if (chars.Count == 0)
                    {
                        return (false,
                            $"La lista personaggi della storia è vuota o non valida. JSON attuale: {story.Characters.Substring(0, Math.Min(200, story.Characters.Length))}...");
                    }
                    storyCharacters = chars;
                }
            }

            // Check if tts_schema.json exists
            var schemaPath = Path.Combine(folderPath, "tts_schema.json");
            if (!File.Exists(schemaPath))
            {
                return (false, "File tts_schema.json non trovato. Genera prima lo schema TTS.");
            }

            try
            {
                // Load existing schema
                var jsonContent = File.ReadAllText(schemaPath);
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };
                var schema = JsonSerializer.Deserialize<TtsSchema>(jsonContent, jsonOptions);

                if (schema == null)
                {
                    return (false, "Impossibile deserializzare tts_schema.json");
                }

                var normalizedCount = 0;
                var characterMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Build mapping from old names to canonical names
                foreach (var ttsChar in schema.Characters)
                {
                    var matched = StoryCharacterParser.FindCharacter(storyCharacters, ttsChar.Name);
                    if (matched != null && !ttsChar.Name.Equals(matched.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        characterMapping[ttsChar.Name] = matched.Name;
                        _service._customLogger?.Log("Information", "NormalizeNames",
                            $"Mapping '{ttsChar.Name}' -> '{matched.Name}'");
                    }
                }

                if (characterMapping.Count == 0)
                {
                    return (true, "Nessuna normalizzazione necessaria: tutti i nomi sono già canonici.");
                }

                // Update character names in the Characters list
                var newCharacters = new List<TtsCharacter>();
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var ttsChar in schema.Characters)
                {
                    var canonicalName = characterMapping.TryGetValue(ttsChar.Name, out var mapped) ? mapped : ttsChar.Name;

                    // Skip duplicates that would result from merging
                    if (seenNames.Contains(canonicalName))
                    {
                        _service._customLogger?.Log("Debug", "NormalizeNames",
                            $"Skipping duplicate character '{ttsChar.Name}' (merged into '{canonicalName}')");
                        continue;
                    }

                    seenNames.Add(canonicalName);

                    // Update gender from story character if available
                    var storyChar = StoryCharacterParser.FindCharacter(storyCharacters, canonicalName);
                    var updatedChar = new TtsCharacter
                    {
                        Name = canonicalName,
                        VoiceId = ttsChar.VoiceId,
                        EmotionDefault = ttsChar.EmotionDefault,
                        Gender = storyChar?.Gender ?? ttsChar.Gender
                    };

                    newCharacters.Add(updatedChar);
                    if (!ttsChar.Name.Equals(canonicalName, StringComparison.Ordinal))
                    {
                        normalizedCount++;
                    }
                }

                schema.Characters = newCharacters;

                // Update character references in timeline
                foreach (var item in schema.Timeline)
                {
                    if (item is JsonElement)
                    {
                        // Timeline items are JsonElements, need special handling
                        // This will be handled when we re-serialize
                    }
                    else if (item is TtsPhrase phrase)
                    {
                        if (characterMapping.TryGetValue(phrase.Character, out var newName))
                        {
                            phrase.Character = newName;
                        }
                    }
                }

                // Re-process timeline to normalize character names in phrases
                var updatedTimeline = new List<object>();
                foreach (var item in schema.Timeline)
                {
                    if (item is JsonElement jsonElement)
                    {
                        var phrase = JsonSerializer.Deserialize<TtsPhrase>(jsonElement.GetRawText(), jsonOptions);
                        if (phrase != null)
                        {
                            if (characterMapping.TryGetValue(phrase.Character, out var newCharName))
                            {
                                phrase.Character = newCharName;
                            }
                            updatedTimeline.Add(phrase);
                        }
                    }
                    else if (item is TtsPhrase phrase)
                    {
                        if (characterMapping.TryGetValue(phrase.Character, out var newCharName))
                        {
                            phrase.Character = newCharName;
                        }
                        updatedTimeline.Add(phrase);
                    }
                    else
                    {
                        updatedTimeline.Add(item);
                    }
                }
                schema.Timeline = updatedTimeline;

                // Save the updated schema
                var outputOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var updatedJson = JsonSerializer.Serialize(schema, outputOptions);
                _service.SaveSanitizedTtsSchemaJson(schemaPath, updatedJson);

                _service._logger?.LogInformation(
                    "Normalized {Count} character names in tts_schema.json for story {StoryId}",
                    normalizedCount, story.Id);

                return (true,
                    $"Normalizzati {normalizedCount} nomi personaggi. Schema aggiornato con {schema.Characters.Count} personaggi.");
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la normalizzazione dei nomi per la storia {Id}", story.Id);
                return (false, ex.Message);
            }
        }
    }
}
