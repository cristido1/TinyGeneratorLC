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

        public override IEnumerable<string> FunctionNames
        {
            get
            {
                yield return Name;
                yield return "add_narration";
                yield return "add_phrase";
                yield return "confirm";
            }
        }

        public override Dictionary<string, object> GetSchema()
        {
            return CreateAddNarrationSchema();
        }

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
                            { "description", "Emotion for the line. Allowed: neutral, happy, sad, angry, fearful, disgusted, surprised" }
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
                var request = ParseInput<TtsSchemaToolRequest>(input);
                if (request == null)
                {
                    CustomLogger?.Log("Error", "TtsSchemaTool", "Invalid input format");
                    return SerializeResult(new { error = "Invalid input format" });
                }

                // Log operation with all parameters
                var paramDetails = new List<string>();
                if (!string.IsNullOrEmpty(request.Operation)) paramDetails.Add($"operation={request.Operation}");
                if (!string.IsNullOrEmpty(request.Name)) paramDetails.Add($"name={request.Name}");
                if (!string.IsNullOrEmpty(request.Gender)) paramDetails.Add($"gender={request.Gender}");
                if (!string.IsNullOrEmpty(request.Voice)) paramDetails.Add($"voice={request.Voice}");
                if (!string.IsNullOrEmpty(request.Character)) paramDetails.Add($"character={request.Character}");
                if (!string.IsNullOrEmpty(request.Text)) paramDetails.Add($"text={request.Text}");
                if (!string.IsNullOrEmpty(request.Emotion)) paramDetails.Add($"emotion={request.Emotion}");
                if (request.Seconds.HasValue) paramDetails.Add($"seconds={request.Seconds}");
                

                CustomLogger?.Log("Info", "TtsSchemaTool", $"Executing: {string.Join(", ", paramDetails)}");

                return request.Operation?.ToLowerInvariant() switch
                {
                    "add_narration" => AddNarration(request.Text),
                    "add_phrase" => AddPhraseAutoCreate(request.Character, request.Text, request.Emotion),
                    "set_gender" => SetGender(request.Character, request.Gender),
                    "confirm" => ConfirmSchemaAllowSave(),
                    "describe" => SerializeResult(new { result = "Operations: add_narration(text), add_phrase(character, text, emotion), set_gender(character, gender), confirm()" }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "TtsSchemaTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        public override Task<string> ExecuteFunctionAsync(string functionName, string input)
        {
            if (functionName.Equals(Name, StringComparison.OrdinalIgnoreCase))
            {
                return ExecuteAsync(input);
            }
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

        private string AddCharacter(string? name, string? gender)
        {
            if (string.IsNullOrWhiteSpace(name))
                return SerializeResult(new { error = "Character name is required" });

            if (_schema.Characters.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return SerializeResult(new { error = $"Character '{name}' already exists" });

            _schema.Characters.Add(new TtsCharacter
            {
                Name = name,
                Voice = "default",
                Gender = gender ?? "neutral"
            });

            return SerializeResult(new { result = "Character added" });
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

        internal string AddNarration(string? text)
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

        internal string SetGender(string? character, string? gender)
        {
            if (string.IsNullOrWhiteSpace(character))
                return SerializeResult(new { error = "Character name is required" });

            if (string.IsNullOrWhiteSpace(gender))
                return SerializeResult(new { error = "Gender is required" });

            var existing = _schema.Characters.FirstOrDefault(c => c.Name.Equals(character, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                return SerializeResult(new { error = $"Character '{character}' not defined. Use add_phrase to create it first." });

            existing.Gender = gender;
            return SerializeResult(new { result = $"Gender for '{existing.Name}' set to {gender}" });
        }

        private string AddPause(int seconds)
        {
            _schema.Timeline.Add(new TtsPause(seconds));
            return SerializeResult(new { result = "Pause added" });
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

            // If character not defined, add it automatically
            if (!_schema.Characters.Any(c => c.Name.Equals(charName, StringComparison.OrdinalIgnoreCase)))
            {
                _schema.Characters.Add(new TtsCharacter { Name = charName, Voice = "default", Gender = "neutral" });
            }

            _schema.Timeline.Add(new TtsPhrase { Character = charName, Text = text, Emotion = emotion });
            return SerializeResult(new { result = "Phrase added" });
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

        private class TtsSchemaToolRequest
        {
            public string? Operation { get; set; }
            public string? Name { get; set; }
            public string? Gender { get; set; }
            public string? Voice { get; set; }
            public string? Character { get; set; }
            public string? Text { get; set; }
            public string? Emotion { get; set; }
            public int? Seconds { get; set; }
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
