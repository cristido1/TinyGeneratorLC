using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for TTS schema building and validation.
    /// Converted from TtsSchemaSkill (Semantic Kernel).
    /// Manages character definitions, phrases, pauses, and schema validation for text-to-speech.
    /// </summary>
    public class TtsSchemaUtil : BaseLangChainTool, ITinyTool
    {
        private string _storyText;
        private readonly string _workingFolder;
        private TtsSchema _schema;

        private static readonly HashSet<string> SupportedEmotions = new(StringComparer.OrdinalIgnoreCase)
        {
            "neutral", "happy", "sad", "angry", "fearful", "disgusted", "surprised"
        };

        private static readonly JsonSerializerOptions DefaultOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }

        public TtsSchemaUtil(string workingFolder, string? storyText = null, ICustomLogger? logger = null) 
            : base("ttsschema", "Manages TTS schema building: characters, phrases, pauses, validation.", logger)
        {
            _workingFolder = workingFolder;
            _storyText = storyText ?? string.Empty;
            _schema = new TtsSchema();
        }

        public override Dictionary<string, object> GetSchema()
        {
            return CreateFunctionSchema(
                Name,
                Description,
                new Dictionary<string, object>
                {
                    {
                        "operation",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Operation: read_story_text, set_story_text, reset_schema, add_character, add_character_with_voice, delete_character, add_phrase, add_narration, add_pause, delete_last, read_schema, confirm_schema, check_schema, describe" }
                        }
                    },
                    { "name", new Dictionary<string, object> { { "type", "string" }, { "description", "Character name" } } },
                    { "gender", new Dictionary<string, object> { { "type", "string" }, { "description", "Character gender" } } },
                    { "voice", new Dictionary<string, object> { { "type", "string" }, { "description", "Voice name" } } },
                    { "character", new Dictionary<string, object> { { "type", "string" }, { "description", "Character name for phrase" } } },
                    { "text", new Dictionary<string, object> { { "type", "string" }, { "description", "Phrase or narration text" } } },
                    { "emotion", new Dictionary<string, object> { { "type", "string" }, { "description", "Emotion for phrase" } } },
                    { "seconds", new Dictionary<string, object> { { "type", "integer" }, { "description", "Pause duration in seconds" } } },
                    { "storyText", new Dictionary<string, object> { { "type", "string" }, { "description", "Story text to set" } } }
                },
                new List<string> { "operation" }
            );
        }

        public override async Task<string> ExecuteAsync(string input)
        {
            try
            {
                var request = ParseInput<TtsSchemaUtilRequest>(input);
                if (request == null)
                    return SerializeResult(new { error = "Invalid input format" });

                CustomLogger?.Log("Info", "TtsSchemaUtil", $"Executing operation: {request.Operation}");

                return request.Operation?.ToLowerInvariant() switch
                {
                    "read_story_text" => ReadStoryText(),
                    "set_story_text" => SetStoryText(request.StoryText),
                    "reset_schema" => ResetSchema(),
                    "add_character" => AddCharacter(request.Name, request.Gender),
                    "add_character_with_voice" => AddCharacterWithVoice(request.Name, request.Voice, request.Gender),
                    "delete_character" => DeleteCharacter(request.Name),
                    "add_phrase" => AddPhrase(request.Character, request.Text, request.Emotion),
                    "add_narration" => AddNarration(request.Text),
                    "add_pause" => AddPause(request.Seconds ?? 0),
                    "delete_last" => DeleteLast(),
                    "read_schema" => ReadSchema(),
                    "confirm_schema" => ConfirmSchema(),
                    "check_schema" => CheckSchema(),
                    "describe" => SerializeResult(new { result = "Operations: read_story_text(), set_story_text(storyText), reset_schema(), add_character(name, gender), add_phrase(character, text, emotion), add_narration(text), add_pause(seconds), delete_last(), read_schema(), confirm_schema(), check_schema()." }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "TtsSchemaUtil", $"Error executing operation: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        private string ReadStoryText()
        {
            if (!string.IsNullOrWhiteSpace(_storyText))
                return SerializeResult(new { result = _storyText });

            var storyFilePath = Path.Combine(_workingFolder, "tts_storia.txt");
            if (File.Exists(storyFilePath))
            {
                try
                {
                    _storyText = File.ReadAllText(storyFilePath);
                    return SerializeResult(new { result = _storyText });
                }
                catch (Exception ex)
                {
                    return SerializeResult(new { error = $"Could not read story file: {ex.Message}" });
                }
            }

            return SerializeResult(new { result = _storyText });
        }

        private string SetStoryText(string? storyText)
        {
            _storyText = storyText ?? string.Empty;
            return SerializeResult(new { result = "Story text set" });
        }

        private string ResetSchema()
        {
            _schema = new TtsSchema();
            return SerializeResult(new { result = "Schema reset" });
        }

        private string AddCharacter(string? name, string? gender)
        {
            return AddCharacterWithVoice(name, null, gender);
        }

        private string AddCharacterWithVoice(string? name, string? voice, string? gender)
        {
            if (string.IsNullOrWhiteSpace(name))
                return SerializeResult(new { error = "Character name is required" });

            if (_schema.Characters.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return SerializeResult(new { error = $"Character '{name}' already exists" });

            _schema.Characters.Add(new TtsCharacter
            {
                Name = name,
                Voice = string.IsNullOrWhiteSpace(voice) ? "default" : voice,
                Gender = gender ?? "neutral"
            });

            return SerializeResult(new { result = "Character added" });
        }

        private string DeleteCharacter(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return SerializeResult(new { error = "Character name is required" });

            _schema.Characters.RemoveAll(c => c.Name == name);
            return SerializeResult(new { result = "Character deleted" });
        }

        private string AddPhrase(string? character, string? text, string? emotion)
        {
            if (string.IsNullOrWhiteSpace(character))
                return SerializeResult(new { error = "Character name is required" });
            if (string.IsNullOrWhiteSpace(text))
                return SerializeResult(new { error = "Phrase text is required" });
            if (string.IsNullOrWhiteSpace(emotion))
                return SerializeResult(new { error = "Emotion is required" });

            if (!SupportedEmotions.Contains(emotion))
                return SerializeResult(new { error = $"Unsupported emotion '{emotion}'. Supported: {string.Join(", ", SupportedEmotions)}" });

            if (!_schema.Characters.Any(c => c.Name.Equals(character, StringComparison.OrdinalIgnoreCase)))
                return SerializeResult(new { error = $"Character '{character}' not defined. Define it first." });

            _schema.Timeline.Add(new TtsPhrase { Character = character, Text = text, Emotion = emotion });
            return SerializeResult(new { result = "Phrase added" });
        }

        private string AddNarration(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return SerializeResult(new { error = "Narration text is required" });

            if (!_schema.Characters.Any(c => c.Name.Equals("Narratore", StringComparison.OrdinalIgnoreCase)))
            {
                _schema.Characters.Add(new TtsCharacter { Name = "Narratore", Voice = "default", Gender = "neutral" });
            }

            _schema.Timeline.Add(new TtsPhrase { Character = "Narratore", Text = text, Emotion = "neutral" });
            return SerializeResult(new { result = "Narration added" });
        }

        private string AddPause(int seconds)
        {
            _schema.Timeline.Add(new TtsPause(seconds));
            return SerializeResult(new { result = "Pause added" });
        }

        private string DeleteLast()
        {
            if (_schema.Timeline.Count == 0)
                return SerializeResult(new { result = "No entries to delete" });

            _schema.Timeline.RemoveAt(_schema.Timeline.Count - 1);
            return SerializeResult(new { result = "Last entry deleted" });
        }

        private string ReadSchema()
        {
            try
            {
                var json = JsonSerializer.Serialize(_schema, DefaultOptions);
                return SerializeResult(new { result = json });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private string ConfirmSchema()
        {
            try
            {
                var validationResult = CheckSchema();
                if (validationResult.Contains("error"))
                    return validationResult;

                var filePath = Path.Combine(_workingFolder, "tts_schema.json");
                File.WriteAllText(filePath, JsonSerializer.Serialize(_schema, DefaultOptions));
                return SerializeResult(new { result = "Schema saved" });
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private string CheckSchema()
        {
            if (_schema.Characters.Count == 0)
                return SerializeResult(new { error = "No characters defined" });

            if (!_schema.Characters.Any(c => c.Name.Equals("Narratore", StringComparison.OrdinalIgnoreCase)))
                return SerializeResult(new { error = "Narratore character is required" });

            if (_schema.Timeline.Count == 0)
                return SerializeResult(new { error = "No timeline entries" });

            if (string.IsNullOrWhiteSpace(_storyText))
                return SerializeResult(new { error = "Story text is empty" });

            var unusedError = CheckUnusedCharacters();
            if (unusedError != null)
                return unusedError;

            var characterError = CheckCharacterConsistency();
            if (characterError != null)
                return characterError;

            return SerializeResult(new { result = "Schema is valid" });
        }

        private string? CheckUnusedCharacters()
        {
            var phraseCharacters = _schema.Timeline
                .OfType<TtsPhrase>()
                .Select(p => p.Character)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var unusedCharacters = _schema.Characters
                .Where(c => !phraseCharacters.Contains(c.Name))
                .Select(c => c.Name)
                .ToList();

            if (unusedCharacters.Count > 0)
            {
                return SerializeResult(new { error = $"Unused characters: {string.Join(", ", unusedCharacters)}" });
            }

            return null;
        }

        private string? CheckCharacterConsistency()
        {
            var definedCharacters = _schema.Characters
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var undefinedCharacters = _schema.Timeline
                .OfType<TtsPhrase>()
                .Select(p => p.Character)
                .Where(c => !string.IsNullOrWhiteSpace(c) && !definedCharacters.Contains(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (undefinedCharacters.Count > 0)
            {
                return SerializeResult(new { error = $"Undefined characters: {string.Join(", ", undefinedCharacters)}" });
            }

            return null;
        }

        private class TtsSchemaUtilRequest
        {
            public string? Operation { get; set; }
            public string? Name { get; set; }
            public string? Gender { get; set; }
            public string? Voice { get; set; }
            public string? Character { get; set; }
            public string? Text { get; set; }
            public string? Emotion { get; set; }
            public int? Seconds { get; set; }
            public string? StoryText { get; set; }
        }
    }
}
