using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Models;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for assigning voices to TTS schema characters.
    /// Provides helpers to inspect the schema JSON and available TTS voices.
    /// </summary>
    public class VoiceChoserTool : BaseLangChainTool, ITinyTool
    {
        private readonly string _storyFolder;
        private readonly string _schemaPath;
        private readonly DatabaseService? _database;
        private readonly TtsService _ttsService;
        private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
        private List<VoiceInfo>? _cachedVoices;
        private DateTime _voicesCachedAt = DateTime.MinValue;
        private TtsSchema? _schema;

        public VoiceChoserTool(string storyFolder, DatabaseService? database, TtsService? ttsService, ICustomLogger? logger = null)
            : base("voicechoser", "Utility per leggere e aggiornare il file tts_schema.json scegliendo le voci TTS.", logger)
        {
            if (string.IsNullOrWhiteSpace(storyFolder))
                throw new ArgumentException("storyFolder is required", nameof(storyFolder));

            _storyFolder = storyFolder;
            _schemaPath = Path.Combine(storyFolder, "tts_schema.json");
            _database = database;
            _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
        }

        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public override IEnumerable<string> FunctionNames => new[] { "read_characters", "read_voices", "set_voice" };

        public override Dictionary<string, object> GetSchema() => CreateReadCharactersSchema();

        public override IEnumerable<Dictionary<string, object>> GetFunctionSchemas()
        {
            yield return CreateReadCharactersSchema();
            yield return CreateReadVoicesSchema();
            yield return CreateSetVoiceSchema();
        }

        public override Task<string> ExecuteAsync(string input)
            => Task.FromResult(SerializeResult(new { error = "Call read_characters, read_voices or set_voice directly." }));

        public override async Task<string> ExecuteFunctionAsync(string functionName, string input)
        {
            LastFunctionCalled = functionName;
            string result = functionName.ToLowerInvariant() switch
            {
                "read_characters" => await HandleReadCharactersAsync(),
                "read_voices" => await HandleReadVoicesAsync(),
                "set_voice" => await HandleSetVoiceAsync(input),
                _ => SerializeResult(new { error = $"Unknown function: {functionName}" })
            };
            LastFunctionResult = result;
            return result;
        }

        private async Task<string> HandleReadCharactersAsync()
        {
            try
            {
                var schema = await LoadSchemaAsync(forceReload: true);
                if (schema == null)
                    return SerializeResult(new { error = $"Unable to load {_schemaPath}" });

                var characters = schema.Characters.Select(c => new
                {
                    name = c.Name,
                    gender = c.Gender,
                    voice_name = c.Voice
                });

                return SerializeResult(new { characters });
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "VoiceChoserTool", $"Failed to read schema: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> HandleReadVoicesAsync()
        {
            try
            {
                var voices = await EnsureVoicesAsync();
                var simplified = voices.Select(v => new
                {
                    voice_id = v.Id,
                    gender = v.Gender,
                    language = v.Language,
                    archetype = v.Tags != null && v.Tags.TryGetValue("archetype", out var archetype) ? archetype : null
                });
                return SerializeResult(new { voices = simplified });
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "VoiceChoserTool", $"Failed to read voices: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<string> HandleSetVoiceAsync(string input)
        {
            try
            {
                var payload = ParseInput<SetVoiceRequest>(input);
                if (payload == null)
                    return SerializeResult(new { error = "Invalid input format" });

                if (string.IsNullOrWhiteSpace(payload.Character))
                    return SerializeResult(new { error = "character is required" });

                if (string.IsNullOrWhiteSpace(payload.Gender))
                    return SerializeResult(new { error = "gender is required" });

                var schema = await LoadSchemaAsync(forceReload: true);
                if (schema?.Characters == null || schema.Characters.Count == 0)
                    return SerializeResult(new { error = "Unable to parse tts_schema.json" });

                var targetName = NormalizeCharacterName(payload.Character);
                var character = schema.Characters
                    .FirstOrDefault(c => c.Name != null && c.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));

                if (character == null)
                {
                    return SerializeResult(new { error = $"Character '{payload.Character}' not found" });
                }

                var voices = await EnsureVoicesAsync();
                var usedVoiceIds = schema.Characters
                    .Where(c => !string.IsNullOrWhiteSpace(c.VoiceId)
                        && !c.Name.Equals(character.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.VoiceId!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                VoiceInfo? voice = null;
                if (!string.IsNullOrWhiteSpace(payload.VoiceId))
                {
                    voice = voices.FirstOrDefault(v =>
                        !string.IsNullOrWhiteSpace(v.Id) &&
                        v.Id.Equals(payload.VoiceId, StringComparison.OrdinalIgnoreCase));

                    if (voice == null)
                    {
                        return SerializeResult(new { error = $"Voice '{payload.VoiceId}' not found. Use read_voices for the full list." });
                    }

                    if (usedVoiceIds.Contains(voice.Id!))
                    {
                        return SerializeResult(new { error = $"Voice '{payload.VoiceId}' is already assigned to another character." });
                    }

                    if (!string.IsNullOrWhiteSpace(voice.Gender))
                    {
                        var compareVoiceGender = voice.Gender.Trim();
                        if (!string.IsNullOrWhiteSpace(compareVoiceGender) &&
                            !string.Equals(compareVoiceGender, payload.Gender, StringComparison.OrdinalIgnoreCase))
                        {
                            return SerializeResult(new { error = $"Voice gender '{voice.Gender}' does not match requested gender '{payload.Gender}'" });
                        }
                    }
                }
                else
                {
                    var desiredArchetype = NormalizeArchetype(character.Voice);
                    voice = ChooseBestVoice(voices, payload.Gender, desiredArchetype, usedVoiceIds);
                    if (voice == null)
                    {
                        return SerializeResult(new { error = $"Unable to auto-select a voice for '{character.Name}'. Assign voice_id explicitly." });
                    }
                }

                character.Gender = payload.Gender;
                character.VoiceId = voice!.Id ?? string.Empty;
                character.Voice = voice.Name ?? voice.Id ?? string.Empty;

                await SaveSchemaAsync(schema);

                var result = new
                {
                    character = character.Name,
                    gender = character.Gender,
                    voice_id = voice.Id,
                    voice_name = character.Voice
                };

                return SerializeResult(new { result, path = _schemaPath });
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "VoiceChoserTool", $"Failed to set voice: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private async Task<List<VoiceInfo>> EnsureVoicesAsync()
        {
            if (_cachedVoices != null && (DateTime.UtcNow - _voicesCachedAt).TotalMinutes < 5)
            {
                return _cachedVoices;
            }

            List<VoiceInfo> result = new();

            try
            {
                var serviceVoices = await _ttsService.GetVoicesAsync();
                if (serviceVoices != null && serviceVoices.Count > 0)
                {
                    if (_database != null)
                    {
                        foreach (var voice in serviceVoices)
                        {
                            try
                            {
                                _database.UpsertTtsVoice(voice);
                            }
                            catch (Exception ex)
                            {
                                CustomLogger?.Log("Warn", "VoiceChoserTool", $"Failed to upsert voice {voice.Id}: {ex.Message}");
                            }
                        }

                        var dbVoices = _database.ListTtsVoices(onlyEnabled: true);
                        result = dbVoices.Select(v => new VoiceInfo
                        {
                            Id = v.VoiceId,
                            Name = v.Name,
                            Gender = v.Gender,
                            Language = v.Language,
                            Age = v.Age,
                            Confidence = v.Confidence,
                            Tags = ParseTags(v.Tags)
                        }).ToList();
                    }
                    else
                    {
                        result = serviceVoices;
                    }
                }
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "VoiceChoserTool", $"Failed to fetch voices from service: {ex.Message}");
            }

            if (result.Count == 0 && _database != null)
            {
                try
                {
                    var dbVoices = _database.ListTtsVoices(onlyEnabled: true);
                    result = dbVoices.Select(v => new VoiceInfo
                    {
                        Id = v.VoiceId,
                        Name = v.Name,
                        Gender = v.Gender,
                        Language = v.Language,
                        Age = v.Age,
                        Confidence = v.Confidence,
                        Tags = ParseTags(v.Tags)
                    }).ToList();
                }
                catch (Exception ex)
                {
                    CustomLogger?.Log("Error", "VoiceChoserTool", $"Failed to fetch cached voices from db: {ex.Message}");
                }
            }

            _cachedVoices = result;
            _voicesCachedAt = DateTime.UtcNow;
            return _cachedVoices;
        }

        private async Task<TtsSchema?> LoadSchemaAsync(bool forceReload = false)
        {
            if (!forceReload && _schema != null)
                return _schema;

            try
            {
                if (!File.Exists(_schemaPath))
                    return null;

                var content = await File.ReadAllTextAsync(_schemaPath);
                _schema = JsonSerializer.Deserialize<TtsSchema>(content, _jsonOptions);
                return _schema;
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "VoiceChoserTool", $"Failed to load schema: {ex.Message}", ex.ToString());
                return null;
            }
        }

        private async Task SaveSchemaAsync(TtsSchema schema)
        {
            var json = JsonSerializer.Serialize(schema, _jsonOptions);
            await File.WriteAllTextAsync(_schemaPath, json);
            _schema = schema;
        }

        private static Dictionary<string, string>? ParseTags(string? tagsJson)
        {
            if (string.IsNullOrWhiteSpace(tagsJson))
                return null;
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(tagsJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, object> CreateReadCharactersSchema()
        {
            return CreateFunctionSchema(
                "read_characters",
                "Returns the character list from tts_schema.json",
                new Dictionary<string, object>(),
                new List<string>());
        }

        private Dictionary<string, object> CreateReadVoicesSchema()
        {
            return CreateFunctionSchema(
                "read_voices",
                "Returns available voices list",
                new Dictionary<string, object>(),
                new List<string>());
        }

        private Dictionary<string, object> CreateSetVoiceSchema()
        {
            return CreateFunctionSchema(
                "set_voice",
                "Assigns a voice to a character",
                new Dictionary<string, object>
                {
                    { "character", new Dictionary<string, object> { { "type", "string" }, { "description", "Character name (Narrator included)" } } },
                    { "gender", new Dictionary<string, object> { { "type", "string" }, { "description", "Gender to assign (e.g. male, female, neutral)" } } },
                    { "voice_id", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional voice identifier to use" } } }
                },
                new List<string> { "character", "gender" });
        }

        private class SetVoiceRequest
        {
            public string? Character { get; set; }
            public string? Gender { get; set; }
            public string? VoiceId { get; set; }
        }

        private static string NormalizeCharacterName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return name;

            var trimmed = name.Trim();
            if (trimmed.Equals("narrator", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("narratore", StringComparison.OrdinalIgnoreCase))
            {
                return "Narratore";
            }

            return trimmed;
        }

        private static string? NormalizeArchetype(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Equals("default", StringComparison.OrdinalIgnoreCase))
                return null;
            return value.Trim();
        }

        private VoiceInfo? ChooseBestVoice(List<VoiceInfo> voices, string targetGender, string? desiredArchetype, HashSet<string> usedVoiceIds)
        {
            if (voices == null || voices.Count == 0)
                return null;

            var normalizedGender = targetGender?.Trim();
            var normalizedArchetype = NormalizeArchetype(desiredArchetype);

            var ranked = voices
                .Where(v => !string.IsNullOrWhiteSpace(v.Id) && !usedVoiceIds.Contains(v.Id!))
                .Select(v => new
                {
                    Voice = v,
                    Score = ComputeVoiceScore(v, normalizedGender, normalizedArchetype)
                })
                .OrderByDescending(x => x.Score)
                .Select(x => x.Voice)
                .FirstOrDefault();

            return ranked;
        }

        private static double ComputeVoiceScore(VoiceInfo voice, string? targetGender, string? desiredArchetype)
        {
            double score = 0;
            if (voice.Confidence.HasValue)
                score += voice.Confidence.Value;

            if (voice.Tags != null &&
                voice.Tags.TryGetValue("rating", out var ratingRaw) &&
                double.TryParse(ratingRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
            {
                score = Math.Max(score, rating);
            }

            bool genderMatch = !string.IsNullOrWhiteSpace(targetGender) &&
                !string.IsNullOrWhiteSpace(voice.Gender) &&
                voice.Gender!.Equals(targetGender, StringComparison.OrdinalIgnoreCase);

            if (genderMatch)
                score += 1.0;

            if (!string.IsNullOrWhiteSpace(desiredArchetype))
            {
                var voiceArchetype = voice.Tags != null && voice.Tags.TryGetValue("archetype", out var archetypeValue)
                    ? archetypeValue
                    : null;

                if (!string.IsNullOrWhiteSpace(voiceArchetype) &&
                    voiceArchetype!.Equals(desiredArchetype, StringComparison.OrdinalIgnoreCase))
                {
                    score += 1.0;
                }
                else
                {
                    // small penalty if archetype specified but doesn't match
                    score -= 0.25;
                }
            }

            return score;
        }

    }
}
