using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services.Commands;

namespace TinyGenerator.Services;

public sealed partial class StoriesService
{
    /// <summary>
    /// Command to normalize character names in tts_schema.json using the story's character list.
    /// </summary>
    private sealed class NormalizeCharacterNamesCommand : IStoryCommand, ICommand
    {
        private readonly StoriesService _service;

        public NormalizeCharacterNamesCommand(StoriesService service) => _service = service;

        public bool RequireStoryText => false;
        public bool EnsureFolder => true;
        public bool HandlesStatusTransition => false;

        public async Task<(bool success, string? message)> ExecuteAsync(StoryCommandContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
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
                            $"La lista personaggi della storia Ã¨ vuota o non valida. JSON attuale: {story.Characters.Substring(0, Math.Min(200, story.Characters.Length))}...");
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
                var genderUpdatedCount = 0;
                var characterMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var resolvedGenderByLookupName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Build mapping from old names to canonical names
                foreach (var ttsChar in schema.Characters)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var matched = StoryCharacterParser.FindCharacter(storyCharacters, ttsChar.Name);
                    if (matched != null && !ttsChar.Name.Equals(matched.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        characterMapping[ttsChar.Name] = matched.Name;
                        _service._customLogger?.Log("Information", "NormalizeNames",
                            $"Mapping '{ttsChar.Name}' -> '{matched.Name}'");
                    }
                }

                // Update character names in the Characters list
                var newCharacters = new List<TtsCharacter>();
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var ttsChar in schema.Characters)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
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
                    var resolvedGender = await ResolveCharacterGenderAsync(
                        canonicalName,
                        storyChar?.Gender,
                        resolvedGenderByLookupName,
                        context.CancellationToken);
                    var updatedChar = new TtsCharacter
                    {
                        Name = canonicalName,
                        VoiceId = ttsChar.VoiceId,
                        EmotionDefault = ttsChar.EmotionDefault,
                        Gender = resolvedGender
                    };

                    newCharacters.Add(updatedChar);
                    if (!ttsChar.Name.Equals(canonicalName, StringComparison.Ordinal))
                    {
                        normalizedCount++;
                    }
                    if (!string.Equals(
                            DatabaseService.NormalizeCharacterGender(ttsChar.Gender),
                            DatabaseService.NormalizeCharacterGender(resolvedGender),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        genderUpdatedCount++;
                    }
                }

                schema.Characters = newCharacters;

                // Update character references in timeline
                foreach (var item in schema.Timeline)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
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
                    context.CancellationToken.ThrowIfCancellationRequested();
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
                    "Normalized names={NameCount}, genders={GenderCount} in tts_schema.json for story {StoryId}",
                    normalizedCount, genderUpdatedCount, story.Id);

                if (normalizedCount == 0 && genderUpdatedCount == 0)
                {
                    return (true, "Nessuna modifica necessaria: nomi e gender già allineati.");
                }

                return (true,
                    $"Aggiornato schema personaggi: nomi normalizzati={normalizedCount}, gender aggiornati={genderUpdatedCount}, personaggi={schema.Characters.Count}.");
            }
            catch (Exception ex)
            {
                _service._logger?.LogError(ex, "Errore durante la normalizzazione dei nomi per la storia {Id}", story.Id);
                return (false, ex.Message);
            }
        }

        private async Task<string> ResolveCharacterGenderAsync(
            string? canonicalName,
            string? fallbackGender,
            Dictionary<string, string> cache,
            CancellationToken ct)
        {
            var lookupName = ExtractLookupName(canonicalName);
            if (string.IsNullOrWhiteSpace(lookupName))
            {
                return DatabaseService.NormalizeCharacterGender(fallbackGender);
            }

            if (cache.TryGetValue(lookupName, out var cached))
            {
                return cached;
            }

            try
            {
                var existing = _service._database.GetNameGenderByName(lookupName);
                if (existing != null && !string.IsNullOrWhiteSpace(existing.Gender))
                {
                    var dbGender = DatabaseService.NormalizeCharacterGender(existing.Gender);
                    cache[lookupName] = dbGender;
                    return dbGender;
                }
            }
            catch (Exception ex)
            {
                _service._logger?.LogWarning(ex, "Lookup name_gender failed for '{Name}'", lookupName);
            }

            var inferred = await InferGenderViaUtilityAgentAsync(lookupName, canonicalName, ct);
            var normalized = DatabaseService.NormalizeCharacterGender(
                string.IsNullOrWhiteSpace(inferred) ? fallbackGender : inferred);

            try
            {
                _service._database.UpsertNameGender(lookupName, normalized, verified: false);
            }
            catch (Exception ex)
            {
                _service._logger?.LogWarning(ex, "Upsert name_gender failed for '{Name}'", lookupName);
            }

            cache[lookupName] = normalized;
            return normalized;
        }

        private async Task<string?> InferGenderViaUtilityAgentAsync(string lookupName, string? fullName, CancellationToken ct)
        {
            try
            {
                var utility = _service._database.GetAgentByRole("utility_agent");
                if (utility == null || !utility.IsActive)
                {
                    return "unknown";
                }

                var callCenter = ResolveCallCenter();
                if (callCenter == null)
                {
                    return "unknown";
                }

                var history = new ChatHistory();
                history.AddSystem(
                    "Classifica il genere/entita' di un nome. Rispondi esclusivamente con UNA parola tra: " +
                    "male, female, robot, alien, unknown. Nessun testo extra.");
                history.AddUser(
                    $"Nome da classificare: {lookupName}\n" +
                    $"Nome completo (se disponibile): {(string.IsNullOrWhiteSpace(fullName) ? lookupName : fullName)}\n" +
                    "Output consentito: male | female | robot | alien | unknown");

                var options = new CallOptions
                {
                    Operation = "normalize_characters_gender_lookup",
                    Timeout = TimeSpan.FromSeconds(25),
                    MaxRetries = 1,
                    UseResponseChecker = false,
                    AskFailExplanation = false,
                    AllowFallback = true
                };

                var result = await callCenter.CallAgentAsync(
                    storyId: 0,
                    threadId: ("utility_agent:name_gender:" + lookupName).GetHashCode(StringComparison.Ordinal),
                    agent: utility,
                    history: history,
                    options: options,
                    cancellationToken: ct).ConfigureAwait(false);

                if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
                {
                    _service._logger?.LogWarning(
                        "utility_agent failed for name '{Name}': {Reason}",
                        lookupName,
                        result.FailureReason ?? "empty response");
                    return "unknown";
                }

                return ParseUtilityGenderResponse(result.ResponseText);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _service._logger?.LogWarning(ex, "utility_agent gender inference exception for '{Name}'", lookupName);
                return "unknown";
            }
        }

        private ICallCenter? ResolveCallCenter()
        {
            try
            {
                using var scope = _service._scopeFactory?.CreateScope();
                var scoped = scope?.ServiceProvider.GetService(typeof(ICallCenter)) as ICallCenter;
                if (scoped != null)
                {
                    return scoped;
                }
            }
            catch
            {
                // best effort
            }

            return ServiceLocator.Services?.GetService(typeof(ICallCenter)) as ICallCenter;
        }

        private static string ParseUtilityGenderResponse(string? text)
        {
            var normalized = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Contains('\n'))
            {
                normalized = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? normalized;
            }

            foreach (var token in Regex.Split(normalized, @"[^a-z]+"))
            {
                var t = token.Trim();
                if (t is "male" or "female" or "robot" or "alien" or "unknown")
                {
                    return t;
                }
            }

            return "unknown";
        }

        private static string ExtractLookupName(string? fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return string.Empty;
            }

            var raw = fullName.Trim();
            raw = raw.Replace("_", " ");
            raw = Regex.Replace(raw, @"[^\p{L}\p{N}\s'\-\.]", " ");
            var tokens = raw
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (tokens.Count == 0)
            {
                return string.Empty;
            }

            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "sig", "sig.", "signor", "signora", "mr", "mrs", "ms", "dr", "dott", "dott.", "dottore",
                "prof", "prof.", "professoressa", "capitano", "comandante", "tenente", "sergente", "colonnello"
            };

            var token = tokens.FirstOrDefault(t => !skip.Contains(t)) ?? tokens[0];
            token = token.Trim('.', '\'', '"', '-', ' ');
            return token;
        }
    }
}

