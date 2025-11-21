using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using Microsoft.AspNetCore.Routing.Constraints;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    public class TtsSchemaSkill : ITinySkill
    {
        private string _storyText;                                    // Story text (can be set initially or updated later)
        private readonly string _workingFolder;                       // File path for schema saving
        private readonly ICustomLogger? _logger;                      // Logger for skill function calls
        private TtsSchema _schema;                                    // Working schema structure for the agent

        // ITinySkill implementation
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? AgentName { get; set; }
        public DateTime? LastCalled { get; set; }
        public string? LastFunction { get; set; }
        ICustomLogger? ITinySkill.Logger { get => _logger; set { } }

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
        /// <param name="logger">Optional custom logger for skill function calls</param>
        public TtsSchemaSkill(string workingFolder, string? storyText = null, ICustomLogger? logger = null)
        {
            _workingFolder = workingFolder;
            _storyText = storyText ?? string.Empty;
            _logger = logger;
            _schema = new TtsSchema();
        }

        // ================================================================
        // STORY READING
        // ================================================================
        [KernelFunction, Description("Returns the complete story as plain text.")]
        public string ReadStoryText()
        {
            // If story is already loaded, return it
            if (!string.IsNullOrWhiteSpace(_storyText))
                return _storyText;

            // Otherwise try to load from file in the working folder
            var storyFilePath = Path.Combine(_workingFolder, "tts_storia.txt");
            if (File.Exists(storyFilePath))
            {
                try
                {
                    _storyText = File.ReadAllText(storyFilePath);
                    return _storyText;
                }
                catch (Exception ex)
                {
                    return $"ERROR: Could not read story file: {ex.Message}";
                }
            }

            return _storyText; // Return empty or previously set value
        }

        /// <summary>
        /// Sets or updates the story text. Called after kernel initialization if story wasn't available initially.
        /// </summary>
        public void SetStoryText(string? storyText)
        {
            _storyText = storyText ?? string.Empty;
        }

        // ================================================================
        // SCHEMA RESET
        // ================================================================
        [KernelFunction, Description("Completely resets the TTS schema.")]
        public string ResetSchema()
        {
            ((ITinySkill)this).LogFunctionCall("ResetSchema");
            _schema = new TtsSchema();
            return "OK";
        }

        // ================================================================
        // CHARACTERS
        // ================================================================
        [KernelFunction, Description("Adds a character to the schema without specifying voice (defaults to 'default').")]
        public string AddCharacter(string name, string gender)
        {
            return AddCharacterWithVoice(name, null, gender);
        }

        [KernelFunction("AddCharacterWithVoice"), Description("Adds a character to the schema with optional voice specification.")]
        public string AddCharacterWithVoice(string name, string? voice, string gender)
        {
            ((ITinySkill)this).LogFunctionCall("AddCharacter", $"name={name}, gender={gender}");
            
            // Check if character already exists
            if (_schema.Characters.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return $"ERROR: Character '{name}' already exists. Cannot add duplicate character.";
            
            // Use default voice if not provided
            var voiceToUse = string.IsNullOrWhiteSpace(voice) ? "default" : voice;
            
            _schema.Characters.Add(new TtsCharacter
            {
                Name = name,
                Voice = voiceToUse,
                Gender = gender
            });

            return "OK";
        }

        [KernelFunction, Description("Removes a character from the schema.")]
        public string DeleteCharacter(string name)
        {
            ((ITinySkill)this).LogFunctionCall("DeleteCharacter", $"name={name}");
            _schema.Characters.RemoveAll(c => c.Name == name);
            return "OK";
        }

        // ================================================================
        // PHRASES
        // ================================================================
        [KernelFunction, Description("Adds a phrase spoken by a character.")]
        public string AddPhrase(string character, string text, string emotion)
        {
            ((ITinySkill)this).LogFunctionCall("AddPhrase", $"character={character}, emotion={emotion}");
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

        [KernelFunction, Description("Adds a narration phrase (spoken by 'Narratore' character with neutral emotion). If Narratore doesn't exist, creates it automatically.")]
        public string AddNarration(string text)
        {
            ((ITinySkill)this).LogFunctionCall("AddNarration", $"text length={text?.Length ?? 0}");
            
            if (string.IsNullOrWhiteSpace(text))
                return "ERROR: Narration text is required.";

            // Check if Narratore character exists, if not create it
            bool narratoreExists = _schema.Characters.Any(c => c.Name.Equals("Narratore", StringComparison.OrdinalIgnoreCase));
            if (!narratoreExists)
            {
                // Add Narratore as neutral gender with default voice
                _schema.Characters.Add(new TtsCharacter
                {
                    Name = "Narratore",
                    Voice = "default",
                    Gender = "neutral"
                });
                
                ((ITinySkill)this).LogFunctionCall("AddNarration", "Auto-created Narratore character");
            }

            // Add the phrase with Narratore and neutral emotion
            _schema.Timeline.Add(new TtsPhrase
            {
                Character = "Narratore",
                Text = text,
                Emotion = "neutral"
            });

            return "OK";
        }

        // ================================================================
        // PAUSES
        // ================================================================
        [KernelFunction, Description("Adds a pause lasting a given number of seconds.")]
        public string AddPause(int seconds)
        {
            ((ITinySkill)this).LogFunctionCall("AddPause", $"seconds={seconds}");

            _schema.Timeline.Add(new TtsPause(seconds));

            return "OK";
        }

        // ================================================================
        // DELETE LAST ENTRY (phrase or pause)
        // ================================================================
        [KernelFunction, Description("Deletes the last phrase or pause added.")]
        public string DeleteLast()
        {
            ((ITinySkill)this).LogFunctionCall("DeleteLast");
            if (_schema.Timeline.Count == 0) 
                return "EMPTY";

            _schema.Timeline.RemoveAt(_schema.Timeline.Count - 1);
            return "OK";
        }

        // ================================================================
        // SERIALIZATION
        // ================================================================
        [KernelFunction, Description("Returns the current TTS schema as JSON.")]
        public string ReadSchema()
        {
            ((ITinySkill)this).LogFunctionCall("ReadSchema");
            try
            {
                return JsonSerializer.Serialize(_schema, JsonOptions);
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        [KernelFunction, Description("Saves the TTS schema to a JSON file.")]
        public string ConfirmSchema()
        { 
            ((ITinySkill)this).LogFunctionCall("ConfirmSchema");
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
            ((ITinySkill)this).LogFunctionCall("CheckSchema");
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