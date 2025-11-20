using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using Microsoft.AspNetCore.Routing.Constraints;

namespace TinyGenerator.Skills
{
    public class TtsSchemaSkill : ITinySkill
    {
        private string _storyText;                                    // Story text (can be set initially or updated later)
        private readonly string _workingFolder;                       // File path for schema saving
        private TtsSchema _schema;                                    // Working schema structure for the agent

        // ITinySkill implementation
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? AgentName { get; set; }
        public DateTime? LastCalled { get; set; }
        public string? LastFunction { get; set; }

        // Supported emotions for TTS phrases
        private static readonly HashSet<string> SupportedEmotions = new(StringComparer.OrdinalIgnoreCase)
        {
            "neutral", "happy", "sad", "angry", "fearful", "disgusted", "surprised"
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // ================================================================
        // CONSTRUCTOR
        // ================================================================
        /// <summary>
        /// Creates a TtsSchemaSkill instance.
        /// </summary>
        /// <param name="workingFolder">Directory where tts_schema.json will be saved</param>
        /// <param name="storyText">Optional story text. Can be provided now or set later via the story.</param>
        public TtsSchemaSkill(string workingFolder, string? storyText = null)
        {
            _workingFolder = workingFolder;
            _storyText = storyText ?? string.Empty;
            _schema = new TtsSchema();
        }

        // ================================================================
        // STORY READING
        // ================================================================
        [KernelFunction, Description("Returns the complete story as plain text.")]
        public string ReadStoryText() => _storyText;

        // ================================================================
        // SCHEMA RESET
        // ================================================================
        [KernelFunction, Description("Completely resets the TTS schema.")]
        public string ResetSchema()
        {
            _schema = new TtsSchema();
            return "OK";
        }

        // ================================================================
        // CHARACTERS
        // ================================================================
        [KernelFunction, Description("Adds a character to the schema.")]
        public string AddCharacter(string name, string voice, string gender, string emotionDefault)
        {
            _schema.Characters.Add(new TtsCharacter
            {
                Name = name,
                Voice = voice,
                Gender = gender,
                EmotionDefault = emotionDefault
            });

            return "OK";
        }

        [KernelFunction, Description("Removes a character from the schema.")]
        public string DeleteCharacter(string name)
        {
            _schema.Characters.RemoveAll(c => c.Name == name);
            return "OK";
        }

        // ================================================================
        // PHRASES
        // ================================================================
        [KernelFunction, Description("Adds a phrase spoken by a character.")]
        public string AddPhrase(string character, string text, string emotion)
        {
            // Validate required parameters
            if (string.IsNullOrWhiteSpace(character))
                return "ERROR: Character name is required.";
            if (string.IsNullOrWhiteSpace(text))
                return "ERROR: Phrase text is required.";
            if (string.IsNullOrWhiteSpace(emotion))
                return "ERROR: Emotion is mandatory for each phrase.";

            // Validate emotion value
            if (!SupportedEmotions.Contains(emotion))
            {
                return $"ERROR: Emotion '{emotion}' is not supported. Supported emotions are: {string.Join(", ", SupportedEmotions)}.";
            }

            // Check if character is defined
            bool characterExists = _schema.Characters.Any(c => c.Name.Equals(character, StringComparison.OrdinalIgnoreCase));
            if (!characterExists)
                return $"ERROR: Character '{character}' is not defined. Define the character with AddCharacter before adding phrases. Phrase not added.";

            _schema.Timeline.Add(new TtsPhrase
            {
                Character = character,
                Text = text,
                Emotion = emotion
            });

            return "OK";
        }

        // ================================================================
        // PAUSES
        // ================================================================
        [KernelFunction, Description("Adds a pause lasting a given number of seconds.")]
        public string AddPause(int seconds)
        {
            if (seconds < 1) seconds = 1;

            _schema.Timeline.Add(new TtsPause(seconds));

            return "OK";
        }

        // ================================================================
        // DELETE LAST ENTRY (phrase or pause)
        // ================================================================
        [KernelFunction, Description("Deletes the last phrase or pause added.")]
        public string DeleteLast()
        {
            if (_schema.Timeline.Count == 0) 
                return "EMPTY";

            _schema.Timeline.RemoveAt(_schema.Timeline.Count - 1);
            return "OK";
        }

        // ================================================================
        // SERIALIZATION
        // ================================================================
        [KernelFunction, Description("Saves the TTS schema to a JSON file.")]
        public string ConfirmSchema()
        {
            try
            {
                // Validate schema before saving
                var validationResult = CheckSchema();
                if (validationResult != "OK")
                {
                    return validationResult; // Return validation error instead of saving
                }

                string filePath = Path.Combine(_workingFolder, "tts_schema.json");
                File.WriteAllText(filePath, JsonSerializer.Serialize(_schema, JsonOptions));
                LastCalled = DateTime.UtcNow;
                LastFunction = "ConfirmSchema";
                return "OK";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        // ================================================================
        // SCHEMA VALIDATION
        // ================================================================
        [KernelFunction, Description("Verifies that the TTS schema is valid and complete.")]
        public string CheckSchema()
        {
            // Check 1: Characters list is not empty
            if (_schema.Characters.Count == 0)
                return "ERROR: No characters defined. Add at least one character with AddCharacter.";

            // Check 2: Narrator character must be present
            bool hasNarrator = _schema.Characters.Any(c => c.Name.Equals("Narratore", StringComparison.OrdinalIgnoreCase));
            if (!hasNarrator)
                return "ERROR: Narratore character is required. Add a Narratore character with AddCharacter.";

            // Check 3: Timeline has entries (phrases or pauses)
            if (_schema.Timeline.Count == 0)
                return "ERROR: No timeline entries. Add phrases or pauses with AddPhrase or AddPause.";

            // Check 4: Story is not empty
            if (string.IsNullOrWhiteSpace(_storyText))
                return "ERROR: Story text is empty.";

            // Check 5: All characters are used in the timeline
            var unusedError = CheckUnusedCharacters();
            if (unusedError != null)
                return unusedError;

            // Check 6: All character names in phrases match defined characters
            var characterError = CheckCharacterConsistency();
            if (characterError != null)
                return characterError;

            // Check 7: Story coverage - verify all story text has been included in the schema
            var coverageError = CheckStoryCoverage();
            if (coverageError != null)
                return coverageError;

            return "OK";
        }

        /// <summary>
        /// Validates that all significant content from the story has been included in the schema.
        /// Removes all phrases from the timeline, character names from the residual text,
        /// and checks if less than 5% of the original story remains (mostly punctuation and connectors).
        /// Returns an error message if coverage is insufficient, otherwise returns null.
        /// </summary>
        private string? CheckStoryCoverage()
        {
            // Start with a copy of the original story
            string remainingText = _storyText;

            // Remove all phrases that are in the schema timeline
            foreach (var entry in _schema.Timeline)
            {
                if (entry is TtsPhrase phrase && !string.IsNullOrWhiteSpace(phrase.Text))
                {
                    // Remove the phrase text from the story (case-insensitive, normalize whitespace)
                    string normalizedPhrase = System.Text.RegularExpressions.Regex.Replace(phrase.Text, @"\s+", " ").Trim();
                    remainingText = System.Text.RegularExpressions.Regex.Replace(
                        remainingText,
                        System.Text.RegularExpressions.Regex.Escape(normalizedPhrase),
                        "",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    );
                }
            }

            // Remove all character names from the residual text
            foreach (var character in _schema.Characters)
            {
                if (!string.IsNullOrWhiteSpace(character.Name))
                {
                    remainingText = System.Text.RegularExpressions.Regex.Replace(
                        remainingText,
                        System.Text.RegularExpressions.Regex.Escape(character.Name),
                        "",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    );
                }
            }

            // Clean up: remove extra whitespace and common punctuation/connectors
            remainingText = System.Text.RegularExpressions.Regex.Replace(remainingText, @"[""':,;!?\-—–\[\]\(\)]+", "");
            remainingText = System.Text.RegularExpressions.Regex.Replace(remainingText, @"\s+", " ").Trim();

            // Calculate coverage: check if less than 5% of original content remains
            double originalLength = _storyText.Length;
            double remainingLength = remainingText.Length;
            double coveragePercentage = (remainingLength / originalLength) * 100.0;

            if (coveragePercentage > 5.0)
            {
                return $"ERROR: Insufficient story coverage. {coveragePercentage:F1}% of the story content was not included in the schema. " +
                       $"Please ensure all significant dialogue and narrative elements are captured as phrases or pauses.";
            }

            return null; // Coverage is acceptable
        }

        /// <summary>
        /// Checks that all defined characters are used at least once in the timeline.
        /// Returns an error message if unused characters are found, otherwise returns null.
        /// </summary>
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
                return $"ERROR: Unused characters: {string.Join(", ", unusedCharacters)}. " +
                       $"All characters must be used in at least one phrase.";
            }

            return null;
        }

        /// <summary>
        /// Checks that all character names used in phrases are defined in the characters list.
        /// Returns an error message if undefined character names are found, otherwise returns null.
        /// </summary>
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
                return $"ERROR: Undefined characters in phrases: {string.Join(", ", undefinedCharacters)}. " +
                       $"All character names in phrases must match defined characters.";
            }

            return null;
        }
    }
}