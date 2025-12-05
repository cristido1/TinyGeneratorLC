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
        private readonly DatabaseService? _database;
        private readonly int _storyChunkSize = 1800;
        private int _confirmAttempts = 0;
        private const int MaxConfirmAttempts = 3;
        private const double MinimumTextCoverageThreshold = 0.90; // 90%
        // Track which part indexes have been requested by the model to avoid infinite loops
        private readonly HashSet<int> _requestedParts = new();
        public long? CurrentStoryId { get; set; }
        private List<string>? _chunksCache;
        private int? _lastChunkIndex;
        private int _chunkTimelineStart = 0;
        private string _lastChunkText = string.Empty;

        private static readonly HashSet<string> SupportedEmotions = new(StringComparer.OrdinalIgnoreCase)
        {
            "neutral", "happy", "sad", "angry", "fearful", "disgusted", "surprised"
        };

        private static readonly Dictionary<string, string> EmotionSynonyms = new(StringComparer.OrdinalIgnoreCase)
        {
            // Neutral-ish
            { "neutral", "neutral" }, { "calm", "neutral" }, { "collected", "neutral" }, { "composed", "neutral" }, { "determined", "neutral" },
            { "focused", "neutral" }, { "steady", "neutral" }, { "confident", "neutral" },
            // Happy equivalents
            { "happy", "happy" }, { "cheerful", "happy" }, { "joyful", "happy" }, { "content", "happy" }, { "delighted", "happy" },
            { "upbeat", "happy" }, { "bright", "happy" },
            // Sad equivalents
            { "sad", "sad" }, { "melancholy", "sad" }, { "sorrowful", "sad" }, { "dejected", "sad" }, { "grief", "sad" },
            { "somber", "sad" }, { "bleak", "sad" }, { "downcast", "sad" },
            // Angry equivalents
            { "angry", "angry" }, { "furious", "angry" }, { "irate", "angry" }, { "mad", "angry" }, { "resentful", "angry" },
            { "enraged", "angry" }, { "annoyed", "angry" }, { "agitated", "angry" },
            // Fearful / anxious
            { "fearful", "fearful" }, { "scared", "fearful" }, { "terrified", "fearful" }, { "anxious", "fearful" }, { "nervous", "fearful" },
            { "worried", "fearful" }, { "alarm", "fearful" }, { "paranoid", "fearful" }, { "panicked", "fearful" },
            // Disgusted
            { "disgusted", "disgusted" }, { "revolted", "disgusted" }, { "repulsed", "disgusted" }, { "nauseated", "disgusted" },
            { "horrified", "disgusted" }, { "appalled", "disgusted" },
            // Surprised
            { "surprised", "surprised" }, { "astonished", "surprised" }, { "amazed", "surprised" }, { "shocked", "surprised" },
            { "stunned", "surprised" }, { "startled", "surprised" }, { "bewildered", "surprised" }
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
        public bool AllowReadStoryPart { get; set; } = true;

        // Expose read-only info for external loop orchestrator
        public IReadOnlyCollection<int> RequestedParts => _requestedParts;
        public int? LastChunkIndex => _lastChunkIndex;

        public TtsSchemaTool(string workingFolder, string? storyText = null, ICustomLogger? logger = null, DatabaseService? database = null) 
            : base("ttsschema", "TTS schema operations", logger)
        {
            _workingFolder = workingFolder;
            _database = database;
            _schema = new TtsSchema();
            _storyText = ExtractStorySegment(storyText);
            LoadExistingSchema();

            if (!string.IsNullOrWhiteSpace(_storyText))
            {
                CustomLogger?.Log("Info", "TtsSchemaTool", $"Loaded story text ({_storyText.Length} chars)");
            }
        }

        public override IEnumerable<string> FunctionNames
        {
            get
            {
                if (AllowReadStoryPart)
                    return new[] { "add_narration", "add_phrase", "confirm", "read_story_part" };
                return new[] { "add_narration", "add_phrase", "confirm" };
            }
        }

        public override Dictionary<string, object> GetSchema() => CreateAddNarrationSchema();

        public override IEnumerable<Dictionary<string, object>> GetFunctionSchemas()
        {
            yield return CreateAddNarrationSchema();
            yield return CreateAddPhraseSchema();
            yield return CreateConfirmSchema();

            if (AllowReadStoryPart)
            {
                yield return CreateFunctionSchema(
                    "read_story_part",
                    "Reads a segment of the story for reference.",
                    new Dictionary<string, object>
                    {
                        { "part_index", new Dictionary<string, object> { { "type", "integer" }, { "description", "0-based segment index" } } }
                    },
                    new List<string> { "part_index" });
            }
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
                "read_story_part" => ReadStoryPartAsync(input),
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

        private async Task<string> ReadStoryPartAsync(string jsonInput)
        {
            // NOTE: In modalità multistep con chunk forniti nel prompt ({{CHUNK_n}}),
            // questa funzione non viene chiamata dal modello: i chunk sono già inseriti nello step.
            // Rimane disponibile per la modalità tradizionale con read_story_part.
            try
            {
                EnsureStoryChunks();

                var input = ParseInput<ReadStoryPartInput>(jsonInput);
                if (input == null)
                    return SerializeResult(new { error = "Invalid input format" });

                // Ignore any incoming story_id; rely solely on CurrentStoryId
                input.StoryId = CurrentStoryId;

                if (input.PartIndex < 0)
                    return SerializeResult(new { error = "part_index must be non-negative" });

                if (!CurrentStoryId.HasValue || CurrentStoryId.Value <= 0)
                    return SerializeResult(new { error = "CurrentStoryId not set" });

                // register that this part was requested (helps the orchestrator avoid infinite loops)
                try
                {
                    _requestedParts.Add(input.PartIndex);
                }
                catch { }

                var chunks = _chunksCache ?? new List<string>();
                if (input.PartIndex >= chunks.Count)
                    return SerializeResult(new { error = "part_index out of range" });

                var chunk = chunks[input.PartIndex];
                _lastChunkIndex = input.PartIndex;
                _chunkTimelineStart = _schema.Timeline.Count;
                _lastChunkText = chunk;
                var payload = new
                {
                    part_index = input.PartIndex,
                    text = chunk,
                    is_last = input.PartIndex >= chunks.Count - 1
                };

                // Diagnostic logging: report that a story part was read and its length
                try
                {
                    var isLast = input.PartIndex >= chunks.Count - 1;
                    CustomLogger?.Log("Info", "TtsSchemaTool", $"ReadStoryPart part={input.PartIndex} len={chunk.Length} is_last={isLast}");
                }
                catch { }

                return SerializeResult(payload);
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "TtsSchemaTool", $"ReadStoryPart failed: {ex.Message}", ex.ToString());
                return SerializeResult(new { error = ex.Message });
            }
        }

        internal string AddNarration(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return SerializeResult(new { error = "Narration text is required" });

            EnsureCharacterExists("Narratore");

            _schema.Timeline.Add(new TtsPhrase { Character = "Narratore", Text = text, Emotion = "neutral" });
            PersistSchemaSnapshot();
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

            emotion = NormalizeEmotion(emotion);

            EnsureCharacterExists(charName);

            _schema.Timeline.Add(new TtsPhrase { Character = charName, Text = text, Emotion = emotion });
            PersistSchemaSnapshot();
            return SerializeResult(new { result = "Phrase added" });
        }

        private static string NormalizeEmotion(string? emotion)
        {
            if (string.IsNullOrWhiteSpace(emotion))
                return "neutral";

            var trimmed = emotion.Trim();

            if (SupportedEmotions.Contains(trimmed))
                return trimmed.ToLowerInvariant();

            if (EmotionSynonyms.TryGetValue(trimmed, out var mapped))
                return mapped;

            // Fallback to neutral if nothing matches
            return "neutral";
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
                _confirmAttempts++;

                var validationResult = CheckSchema();

                // If validation failed but we have attempts remaining, return retry message
                var resultObj = JsonSerializer.Deserialize<JsonElement>(validationResult);
                if (resultObj.TryGetProperty("error", out var errorProp))
                {
                    // This is an error response
                    return validationResult; // Return as-is; ReActLoop will present it to model for retry
                }

                // Validation passed - save the file
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

        /// <summary>
        /// Resets the confirm attempt counter. Called by orchestrator before each new story generation.
        /// </summary>
        public void ResetConfirmAttempts()
        {
            _confirmAttempts = 0;
        }

        /// <summary>
        /// Reset the internal set of requested parts (called when starting a new generation).
        /// </summary>
        public void ResetRequestedParts()
        {
            _requestedParts.Clear();
        }

        /// <summary>
        /// Check whether the model has requested all parts of the story.
        /// </summary>
        public bool HasRequestedAllParts()
        {
            string storyText = _storyText;
            if (string.IsNullOrWhiteSpace(storyText) && CurrentStoryId.HasValue && _database != null)
            {
                var s = _database.GetStoryById(CurrentStoryId.Value);
                storyText = s?.Story ?? string.Empty;
            }

            if (string.IsNullOrEmpty(storyText))
                return false;

            var totalParts = Math.Max(1, (int)Math.Ceiling((double)storyText.Length / _storyChunkSize));
            return _requestedParts.Count >= totalParts;
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

        private void LoadExistingSchema()
        {
            try
            {
                var filePath = Path.Combine(_workingFolder, "tts_schema.json");
                if (!File.Exists(filePath))
                    return;

                var json = File.ReadAllText(filePath);
                var existing = JsonSerializer.Deserialize<TtsSchema>(json, DefaultOptions);
                if (existing != null)
                {
                    _schema = existing;
                    CustomLogger?.Log("Info", "TtsSchemaTool", "Loaded existing tts_schema.json for incremental updates");
                }
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Warning", "TtsSchemaTool", $"Unable to load existing schema: {ex.Message}");
            }
        }

        private void PersistSchemaSnapshot()
        {
            try
            {
                if (!HasSchemaEntries)
                    return;

                Directory.CreateDirectory(_workingFolder);
                var filePath = Path.Combine(_workingFolder, "tts_schema.json");
                File.WriteAllText(filePath, JsonSerializer.Serialize(_schema, DefaultOptions));
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Warning", "TtsSchemaTool", $"Failed to persist schema snapshot: {ex.Message}");
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

            var coverageError = CheckTextCoverage();
            if (coverageError != null)
                return coverageError;

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

        private string? CheckTextCoverage()
        {
            if (string.IsNullOrWhiteSpace(_storyText) && _chunksCache == null)
                return SerializeResult(new { error = "Missing story text for coverage check" });

            EnsureStoryChunks();
            var fullStory = _storyText;
            if (string.IsNullOrWhiteSpace(fullStory) && _chunksCache != null)
            {
                fullStory = string.Join("", _chunksCache);
            }
            if (string.IsNullOrWhiteSpace(fullStory))
                return SerializeResult(new { error = "Missing story text for coverage check" });

            // Create a mutable copy of the story text to remove covered parts
            var remainingText = fullStory;

            // Collect all text from timeline entries (narrations and phrases)
            var timelineTexts = _schema.Timeline
                .OfType<TtsPhrase>()
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (!timelineTexts.Any())
                return SerializeResult(new { error = "No timeline text entries to cover source story" });

            // Remove each timeline text from the remaining story text (case-insensitive)
            foreach (var text in timelineTexts)
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // Find and remove the text (case-insensitive, first match)
                var lowerRemaining = remainingText.ToLower();
                var lowerText = text.ToLower();
                var index = lowerRemaining.IndexOf(lowerText);

                if (index >= 0)
                {
                    // Remove the matched text (using original casing positions)
                    remainingText = remainingText.Remove(index, text.Length);
                }
            }

            // Calculate coverage percentage
            var originalLength = _storyText.Length;
            var remainingLength = remainingText.Length;
            var coveragePercentage = (originalLength - remainingLength) / (double)originalLength;

            // Check if coverage meets threshold
            if (coveragePercentage < MinimumTextCoverageThreshold)
            {
                var uncoveredPercentage = (1 - coveragePercentage) * 100;
                var attemptsRemaining = MaxConfirmAttempts - _confirmAttempts;

                if (attemptsRemaining > 0)
                {
                    return SerializeResult(new
                    {
                        error = $"Text coverage only {coveragePercentage * 100:F1}% (uncovered: {uncoveredPercentage:F1}%). Must cover at least 90% of the source story. Please add narration or phrases for the missing parts. Attempts remaining: {attemptsRemaining}",
                        coverage_percentage = coveragePercentage * 100,
                        remaining_attempts = attemptsRemaining
                    });
                }
                else
                {
                    return SerializeResult(new
                    {
                        error = $"Text coverage {coveragePercentage * 100:F1}% is below 90% threshold. Maximum retry attempts ({MaxConfirmAttempts}) exceeded. Schema generation failed.",
                        coverage_percentage = coveragePercentage * 100,
                        attempts_exhausted = true
                    });
                }
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

        private class ReadStoryPartInput
        {
            // story_id is managed internally via CurrentStoryId; this field is ignored if provided
            [JsonPropertyName("story_id")]
            public long? StoryId { get; set; }

            [JsonPropertyName("part_index")]
            public int PartIndex { get; set; }
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

        public bool HasCoveredCurrentChunk(out double coveragePercent, double minCoverage = 0.9)
        {
            coveragePercent = 0;
            if (!_lastChunkIndex.HasValue || string.IsNullOrWhiteSpace(_lastChunkText))
                return false;

            var remaining = _lastChunkText;
            var entries = _schema.Timeline.Skip(_chunkTimelineStart)
                .OfType<TtsPhrase>()
                .Select(p => p.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t));

            foreach (var t in entries)
            {
                var lowerRem = remaining.ToLower();
                var lowerT = t.ToLower();
                var idx = lowerRem.IndexOf(lowerT, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    remaining = remaining.Remove(idx, t.Length);
                }
            }

            var origLen = _lastChunkText.Length;
            var remainingLen = remaining.Length;
            coveragePercent = origLen == 0 ? 0 : (origLen - remainingLen) / (double)origLen;
            return coveragePercent >= minCoverage;
        }

        private void EnsureStoryChunks()
        {
            if (_chunksCache != null) return;
            if (!string.IsNullOrWhiteSpace(_storyText))
            {
                _chunksCache = StoryChunkHelper.SplitIntoChunks(_storyText, _storyChunkSize);
                return;
            }

            if (CurrentStoryId.HasValue && _database != null)
            {
                var s = _database.GetStoryById(CurrentStoryId.Value);
                var storyText = s?.Story ?? string.Empty;
                _chunksCache = StoryChunkHelper.SplitIntoChunks(storyText, _storyChunkSize);
            }
        }
    }
}
