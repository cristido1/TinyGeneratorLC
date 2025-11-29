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
    public class TtsSchemaTool : BaseLangChainTool, ITinyTool
    {
        private readonly string _workingFolder;
        private readonly string _storyText;
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

        public TtsSchemaTool(string workingFolder, string? storyText = null, ICustomLogger? logger = null) 
            : base("ttsschema", "TTS schema operations", logger)
        {
            _workingFolder = workingFolder;
            _schema = new TtsSchema();
            _storyText = ExtractStorySegment(storyText);

            if (!string.IsNullOrWhiteSpace(_storyText))
            {
                CustomLogger?.Log("Info", "TtsSchemaTool", $"Loaded story text ({_storyText.Length} chars)");
            }
        }

        public override IEnumerable<string> FunctionNames => new[] { "add_narration", "add_phrase", "confirm" };

        public override Dictionary<string, object> GetSchema() => CreateAddNarrationSchema();

        public override IEnumerable<Dictionary<string, object>> GetFunctionSchemas()
        {
            yield return CreateAddNarrationSchema();
            yield return CreateAddPhraseSchema();
            yield return CreateConfirmSchema();
        }

        private Dictionary<string, object> CreateAddNarrationSchema()
        {
            return CreateFunctionSchema(
                "add_narration",
                "Adds narration text to the story timeline",
                new Dictionary<string, object>
                {
                    {
                        "text",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Narrator text to append" }
                        }
                    }
                },
                new List<string> { "text" }
            );
        }

        private Dictionary<string, object> CreateAddPhraseSchema()
        {
            return CreateFunctionSchema(
                "add_phrase",
                "Adds a character line to the story timeline",
                new Dictionary<string, object>
                {
                    {
                        "character",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Character name" }
                        }
                    },
                    {
                        "text",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Line that the character speaks" }
                        }
                    },
                    {
                        "emotion",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Emotion for the line. Allowed: neutral, happy, sad, angry, fearful, disgusted, surprised" },
                            { "enum", SupportedEmotions.ToArray() }
                        }
                    }
                },
                new List<string> { "character", "text", "emotion" }
            );
        }

        private Dictionary<string, object> CreateConfirmSchema()
        {
            return CreateFunctionSchema(
                "confirm",
                "Validates and saves the generated TTS schema",
                new Dictionary<string, object>(),
                new List<string>()
            );
        }

        public override async Task<string> ExecuteAsync(string input)
        {
            try
            {
                return SerializeResult(new
                {
                    error = "Call add_narration, add_phrase or confirm directly. The generic ttsschema entry is no longer available."
                });
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "TtsSchemaTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        public override Task<string> ExecuteFunctionAsync(string functionName, string input)
        {
            return functionName switch
            {
                "add_narration" => Task.FromResult(ExecuteAddNarration(input)),
                "add_phrase" => Task.FromResult(ExecuteAddPhrase(input)),
                "confirm" => Task.FromResult(ExecuteConfirm()),
                _ => Task.FromResult(SerializeResult(new { error = $"Unknown function: {functionName}" }))
            };
        }

        private string ExecuteAddNarration(string input)
        {
            var request = ParseInput<NarrationRequest>(input);
            if (request == null)
            {
                return SerializeResult(new { error = "Invalid input format" });
            }
            LastFunctionCalled = "add_narration";
            var result = AddNarration(request.Text);
            LastFunctionResult = result;
            return result;
        }

        private string ExecuteAddPhrase(string input)
        {
            var request = ParseInput<PhraseRequest>(input);
            if (request == null)
            {
                return SerializeResult(new { error = "Invalid input format" });
            }
            LastFunctionCalled = "add_phrase";
            var result = AddPhraseAutoCreate(request.Character, request.Text, request.Emotion);
            LastFunctionResult = result;
            return result;
        }

        private string ExecuteConfirm()
        {
            LastFunctionCalled = "confirm";
            var result = ConfirmSchemaAllowSave();
            LastFunctionResult = result;
            return result;
        }

        internal string AddNarration(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return SerializeResult(new { error = "Narration text is required" });

            EnsureCharacterExists("Narratore");

            _schema.Timeline.Add(new TtsPhrase { Character = "Narratore", Text = text, Emotion = "neutral" });
            return SerializeResult(new { result = "Narration added" });
        }

        internal string AddPhraseAutoCreate(string? character, string? text, string? emotion)
        {
            if (string.IsNullOrWhiteSpace(text))
                return SerializeResult(new { error = "Phrase text is required" });

            var charName = character;
            if (string.IsNullOrWhiteSpace(charName))
            {
                // create a generic character name if none provided
                charName = $"Character{_schema.Characters.Count + 1}";
            }

            if (string.IsNullOrWhiteSpace(emotion))
            {
                emotion = "neutral";
            }

            if (!SupportedEmotions.Contains(emotion))
                return SerializeResult(new { error = $"Unsupported emotion '{emotion}'. Supported: {string.Join(", ", SupportedEmotions)}" });

            EnsureCharacterExists(charName);

            _schema.Timeline.Add(new TtsPhrase { Character = charName, Text = text, Emotion = emotion });
            return SerializeResult(new { result = "Phrase added" });
        }

        private void EnsureCharacterExists(string characterName)
        {
            if (string.IsNullOrWhiteSpace(characterName))
                return;

            if (_schema.Characters.Any(c => c.Name.Equals(characterName, StringComparison.OrdinalIgnoreCase)))
                return;

            _schema.Characters.Add(new TtsCharacter
            {
                Name = characterName,
                Gender = "neutral",
                VoiceId = string.Empty,
                Voice = "default",
                EmotionDefault = string.Empty
            });
        }

        internal string ConfirmSchemaAllowSave()
        {
            try
            {
                var validationResult = CheckSchema();

                var filePath = Path.Combine(_workingFolder, "tts_schema.json");
                File.WriteAllText(filePath, JsonSerializer.Serialize(_schema, DefaultOptions));

                try
                {
                    var parsed = JsonDocument.Parse(validationResult).RootElement;
                    return SerializeResult(new { validation = parsed, saved = true });
                }
                catch
                {
                    // if parsing fails, return raw validation string
                    return SerializeResult(new { validation = validationResult, saved = true });
                }
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        public bool HasSchemaEntries => _schema.Timeline.Count > 0;

        public bool TrySaveSnapshot(out string? savedPath)
        {
            savedPath = null;
            if (!HasSchemaEntries)
                return false;

            try
            {
                Directory.CreateDirectory(_workingFolder);
                savedPath = Path.Combine(_workingFolder, "tts_schema.json");
                File.WriteAllText(savedPath, JsonSerializer.Serialize(_schema, DefaultOptions));
                return true;
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "TtsSchemaTool", $"Failed to save snapshot: {ex.Message}");
                return false;
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
                return SerializeResult(new { error = "Missing story text" });

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

        private class NarrationRequest
        {
            public string? Text { get; set; }
        }

        private class PhraseRequest
        {
            public string? Character { get; set; }
            public string? Text { get; set; }
            public string? Emotion { get; set; }
        }

        private static string ExtractStorySegment(string? promptText)
        {
            if (string.IsNullOrWhiteSpace(promptText))
                return string.Empty;

            const string marker = "questa \u00E8 la storia";
            var lower = promptText.ToLowerInvariant();
            var markerIndex = lower.IndexOf(marker, StringComparison.Ordinal);
            int colonIndex = -1;

            if (markerIndex >= 0)
            {
                colonIndex = promptText.IndexOf(':', markerIndex + marker.Length);
            }

            if (colonIndex < 0)
            {
                colonIndex = promptText.IndexOf(':');
            }

            if (colonIndex < 0 || colonIndex + 1 >= promptText.Length)
            {
                return promptText.Trim();
            }

            var extracted = promptText[(colonIndex + 1)..].Trim();
            return extracted;
        }
    }
}
