using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using Microsoft.AspNetCore.Routing.Constraints;

namespace TinyGenerator.Skills
{
    public class TtsSchemaSkill
    {
        private readonly string _storyText;                           // Immutable story text
        private readonly string _workingFolder;                       // File path for schema saving
        private TtsSchema _schema;                                    // Working schema structure for the agent

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // ================================================================
        // CONSTRUCTOR
        // ================================================================
        public TtsSchemaSkill(string storyText, string workingFolder)
        {
            _storyText = storyText;
            _schema = new TtsSchema();
            _workingFolder = workingFolder;
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
                string filePath = Path.Combine(_workingFolder, "tts_schema.json");
                File.WriteAllText(filePath, JsonSerializer.Serialize(_schema, JsonOptions));
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

            // Check 2: Timeline has entries (phrases or pauses)
            if (_schema.Timeline.Count == 0)
                return "ERROR: No timeline entries. Add phrases or pauses with AddPhrase or AddPause.";

            // Check 3: Story is not empty
            if (string.IsNullOrWhiteSpace(_storyText))
                return "ERROR: Story text is empty.";

            // Check 4: Story coverage - verify all story text has been included in the schema
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
    }
}