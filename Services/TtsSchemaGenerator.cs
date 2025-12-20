using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Generates TtsSchema from story text with embedded tags.
    /// Expected format:
    ///   [NARRATORE] testo narratore
    ///   [personaggio, emozione] testo dialogo
    ///   [AMBIENTAZIONE: descrizione] (ignored for TTS, used elsewhere)
    /// </summary>
    public class TtsSchemaGenerator
    {
        private readonly ICustomLogger? _logger;
        private readonly DatabaseService? _database;

        public TtsSchemaGenerator(ICustomLogger? logger = null, DatabaseService? database = null)
        {
            _logger = logger;
            _database = database;
        }

        /// <summary>
        /// Parses story text and generates a TtsSchema with characters and timeline.
        /// </summary>
        public TtsSchema GenerateFromStoryText(string storyText, Dictionary<string, string>? voiceAssignments = null)
        {
            return GenerateFromStoryText(storyText, null, voiceAssignments);
        }

        /// <summary>
        /// Parses story text and generates a TtsSchema with characters and timeline.
        /// Uses storyCharacters list (if provided) for accurate name normalization and gender/voice assignment.
        /// </summary>
        public TtsSchema GenerateFromStoryText(string storyText, List<StoryCharacter>? storyCharacters, Dictionary<string, string>? voiceAssignments = null)
        {
            var schema = new TtsSchema();
            var characters = new Dictionary<string, TtsCharacter>(StringComparer.OrdinalIgnoreCase);
            var timeline = new List<object>();

            if (string.IsNullOrWhiteSpace(storyText))
            {
                _logger?.Log("Warning", "TtsSchemaGenerator", "Empty story text provided");
                return schema;
            }

            // Remove <think>...</think> sections from the text
            storyText = Regex.Replace(storyText, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            _logger?.Log("Debug", "TtsSchemaGenerator", "Removed <think> sections from story text");

            // Build character lookup from story characters if available
            var storyCharLookup = storyCharacters ?? new List<StoryCharacter>();
            _logger?.Log("Information", "TtsSchemaGenerator", 
                $"Using {storyCharLookup.Count} pre-defined characters for normalization");

            // Pattern to match tags: [NARRATORE], [personaggio, emozione], [AMBIENTAZIONE: ...]
            // We need to extract the tag and the text that follows until the next tag or end
            var tagPattern = @"\[([^\]]+)\]";
            var matches = Regex.Matches(storyText, tagPattern);

            if (matches.Count == 0)
            {
                _logger?.Log("Warning", "TtsSchemaGenerator", "No tags found in story text");
                return schema;
            }

            // Track pending ambience - only applied to the FIRST phrase after the AMBIENTE tag
            string? pendingAmbience = null;
            
            // Track pending FX for the next phrase
            string? pendingFxDescription = null;
            int? pendingFxDuration = null;
            
            // Track pending music for the next phrase
            string? pendingMusicDescription = null;

            // Process each tag and extract the following text
            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var tagContent = match.Groups[1].Value.Trim();
                
                // Find the text between this tag and the next tag (or end of text)
                int textStart = match.Index + match.Length;
                int textEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : storyText.Length;
                var text = storyText.Substring(textStart, textEnd - textStart).Trim();

                // Handle AMBIENTE/AMBIENTAZIONE tags - extract description and apply to FIRST phrase only
                if (tagContent.StartsWith("AMBIENTE", StringComparison.OrdinalIgnoreCase) ||
                    tagContent.StartsWith("AMBIENTAZIONE", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract ambience description from tag content (everything after the colon)
                    var colonIndex = tagContent.IndexOf(':');
                    if (colonIndex >= 0)
                    {
                        pendingAmbience = tagContent.Substring(colonIndex + 1).Trim();
                        _logger?.Log("Debug", "TtsSchemaGenerator", 
                            $"Parsed ambience (will apply to first phrase only): '{pendingAmbience}'");
                    }
                    continue;
                }

                // Handle MUSICA tags - extract description and apply to next phrase
                if (tagContent.StartsWith("MUSICA", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract music description from tag content (everything after the colon)
                    var colonIndex = tagContent.IndexOf(':');
                    if (colonIndex >= 0)
                    {
                        pendingMusicDescription = tagContent.Substring(colonIndex + 1).Trim();
                        _logger?.Log("Debug", "TtsSchemaGenerator", 
                            $"Parsed music description: '{pendingMusicDescription}'");
                    }
                    continue;
                }

                // Handle FX tags - formats supported:
                // [FX: 15 secondi, descrizione effetto sonoro]
                // [FX: 2 s, clic di tastiera, tintinnio]
                // [FX: Suono di un clic, durata 0.5 s, chiaro e leggero.]
                // [FX, duration_seconds, description]
                // [FX, description]
                if (tagContent.StartsWith("FX", StringComparison.OrdinalIgnoreCase))
                {
                    // Check for format with colon: [FX: ...]
                    var colonIndex = tagContent.IndexOf(':');
                    if (colonIndex >= 0)
                    {
                        var afterColon = tagContent.Substring(colonIndex + 1).Trim();
                        
                        // Try multiple duration patterns:
                        
                        // Pattern 1: Duration at start - "15 secondi, descrizione" or "2 s, descrizione"
                        var durationAtStartMatch = Regex.Match(afterColon, @"^(\d+(?:[.,]\d+)?)\s*(?:secondi?|sec|s)\s*,\s*(.+)$", RegexOptions.IgnoreCase);
                        if (durationAtStartMatch.Success)
                        {
                            var durationStr = durationAtStartMatch.Groups[1].Value.Replace(',', '.');
                            pendingFxDuration = (int)Math.Ceiling(double.Parse(durationStr, System.Globalization.CultureInfo.InvariantCulture));
                            pendingFxDescription = durationAtStartMatch.Groups[2].Value.Trim();
                            _logger?.Log("Debug", "TtsSchemaGenerator", 
                                $"Parsed FX (duration at start): duration={pendingFxDuration}s, description='{pendingFxDescription}'");
                        }
                        // Pattern 2: "durata X s" anywhere in the text - "Descrizione, durata 0.5 s, altro"
                        else
                        {
                            var durataMatch = Regex.Match(afterColon, @"durata\s+(\d+(?:[.,]\d+)?)\s*(?:secondi?|sec|s)?", RegexOptions.IgnoreCase);
                            if (durataMatch.Success)
                            {
                                var durationStr = durataMatch.Groups[1].Value.Replace(',', '.');
                                pendingFxDuration = (int)Math.Ceiling(double.Parse(durationStr, System.Globalization.CultureInfo.InvariantCulture));
                                // Remove the "durata X s" part from the description
                                pendingFxDescription = Regex.Replace(afterColon, @",?\s*durata\s+\d+(?:[.,]\d+)?\s*(?:secondi?|sec|s)?\s*,?", ", ", RegexOptions.IgnoreCase).Trim().Trim(',').Trim();
                                _logger?.Log("Debug", "TtsSchemaGenerator", 
                                    $"Parsed FX (durata keyword): duration={pendingFxDuration}s, description='{pendingFxDescription}'");
                            }
                            else
                            {
                                // No duration specified, use default
                                pendingFxDuration = 5;
                                pendingFxDescription = afterColon;
                                _logger?.Log("Debug", "TtsSchemaGenerator", 
                                    $"Parsed FX (no duration, default 5s): description='{pendingFxDescription}'");
                            }
                        }
                    }
                    else
                    {
                        // Legacy comma format: [FX, duration, description] or [FX, description]
                        var fxParts = tagContent.Split(',', 3);
                        if (fxParts.Length >= 3)
                        {
                            if (int.TryParse(fxParts[1].Trim(), out var fxDuration))
                            {
                                pendingFxDuration = fxDuration;
                            }
                            pendingFxDescription = fxParts[2].Trim();
                            _logger?.Log("Debug", "TtsSchemaGenerator", 
                                $"Parsed FX: duration={pendingFxDuration}s, description='{pendingFxDescription}'");
                        }
                        else if (fxParts.Length == 2)
                        {
                            // Format: [FX, description] with default duration
                            pendingFxDescription = fxParts[1].Trim();
                            pendingFxDuration = 5; // default 5 seconds
                            _logger?.Log("Debug", "TtsSchemaGenerator", 
                                $"Parsed FX (default duration): description='{pendingFxDescription}'");
                        }
                    }
                    continue;
                }

                // Parse the tag content
                var (character, emotion) = ParseTag(tagContent);

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue; // Skip empty phrases
                }

                // Try to find matching character from story's character list
                var matchedChar = StoryCharacterParser.FindCharacter(storyCharLookup, character);
                string canonicalName;
                string gender = "male";

                if (matchedChar != null)
                {
                    // Use the canonical name from story characters
                    canonicalName = matchedChar.Name;
                    gender = matchedChar.Gender ?? "male";
                    _logger?.Log("Debug", "TtsSchemaGenerator", 
                        $"Matched '{character}' -> '{canonicalName}' (gender: {gender})");
                }
                else
                {
                    // Fall back to normalization
                    var normalizedName = NormalizeCharacterName(character);
                    canonicalName = FormatCharacterName(normalizedName);
                }

                // Find existing character in schema by canonical name
                var existingKey = characters.Keys.FirstOrDefault(k => 
                    k.Equals(canonicalName, StringComparison.OrdinalIgnoreCase));

                string characterKey;
                if (existingKey != null)
                {
                    characterKey = existingKey;
                }
                else
                {
                    characterKey = canonicalName;
                    var ttsChar = new TtsCharacter
                    {
                        Name = characterKey,
                        EmotionDefault = "neutral",
                        Gender = gender
                    };

                    // Try to assign voice if we have voice assignments or database
                    if (voiceAssignments != null && voiceAssignments.TryGetValue(characterKey, out var voiceId))
                    {
                        ttsChar.VoiceId = voiceId;
                    }

                    characters[characterKey] = ttsChar;
                }

                // Create phrase with normalized character name
                var phrase = new TtsPhrase
                {
                    Character = characterKey,
                    Text = CleanText(text),
                    Emotion = string.IsNullOrWhiteSpace(emotion) ? "neutral" : emotion,
                    Ambience = pendingAmbience,
                    FxDescription = pendingFxDescription,
                    FxDuration = pendingFxDuration,
                    MusicDescription = pendingMusicDescription,
                    MusicDuration = pendingMusicDescription != null ? 10 : null // Fixed 10 seconds for music
                };

                // Reset pending ambience, FX, and music after applying to phrase (applied only once)
                pendingAmbience = null;
                pendingFxDescription = null;
                pendingFxDuration = null;
                pendingMusicDescription = null;

                timeline.Add(phrase);
            }

            schema.Characters = characters.Values.ToList();
            schema.Timeline = timeline;

            _logger?.Log("Information", "TtsSchemaGenerator", 
                $"Generated TTS schema: {schema.Characters.Count} characters, {schema.Timeline.Count} phrases");

            return schema;
        }

        /// <summary>
        /// Parses a tag like "NARRATORE" or "Mario, arrabbiato" into character and emotion.
        /// </summary>
        private (string character, string emotion) ParseTag(string tagContent)
        {
            // Check for NARRATORE
            if (tagContent.Equals("NARRATORE", StringComparison.OrdinalIgnoreCase))
            {
                return ("Narratore", "neutral");
            }

            // Check for "personaggio, emozione" format
            var parts = tagContent.Split(',', 2);
            if (parts.Length == 2)
            {
                return (parts[0].Trim(), parts[1].Trim().ToLowerInvariant());
            }

            // Just a character name without emotion
            return (tagContent.Trim(), "neutral");
        }

        // Common military/professional titles to remove for normalization
        private static readonly HashSet<string> TitlesToRemove = new(StringComparer.OrdinalIgnoreCase)
        {
            "COMANDANTE", "CAPITANO", "TENENTE", "CAPORALE", "SERGENTE", "MAGGIORE", "COLONNELLO", "GENERALE",
            "TECNICO", "INGEGNERE", "DOTTORE", "DOTTORESSA", "PROFESSORE", "PROFESSORESSA",
            "SIGNOR", "SIGNORA", "SIGNORINA", "SIG", "SIG.RA",
            "MEMBRO", "AGENTE", "DETECTIVE", "ISPETTORE", "COMMISSARIO",
            // Common typos
            "TEENAGINE" // typo for TENENTE
        };

        /// <summary>
        /// Normalizes character name by removing titles and extracting the core name.
        /// Returns just the surname if possible, otherwise the full normalized name.
        /// </summary>
        private string NormalizeCharacterName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            // Handle special case: Narratore
            if (name.Equals("Narratore", StringComparison.OrdinalIgnoreCase))
                return "Narratore";

            // Split into words
            var words = name.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            
            // Remove titles and common prefixes
            var filteredWords = words
                .Where(w => !TitlesToRemove.Contains(w))
                .Where(w => !Regex.IsMatch(w, @"^(N\.|N\.\s*\d+|#\d+|\d+)$")) // Remove things like "N. 734"
                .Where(w => !Regex.IsMatch(w, @"^DELL['']")) // Remove "DELL'" etc.
                .ToList();

            if (filteredWords.Count == 0)
            {
                // All words were titles, use original but cleaned
                return string.Join(" ", words).ToUpperInvariant();
            }

            // Return just the surname (last word) if we have multiple words
            // This consolidates "ALESSANDRO CARTA", "Carta", "COMANDANTE ALESSANDRO CARTA"
            if (filteredWords.Count >= 1)
            {
                return filteredWords.Last().ToUpperInvariant();
            }

            return string.Join(" ", filteredWords).ToUpperInvariant();
        }

        /// <summary>
        /// Formats a normalized name for display (Title Case).
        /// </summary>
        private string FormatCharacterName(string normalizedName)
        {
            if (string.IsNullOrWhiteSpace(normalizedName))
                return string.Empty;

            if (normalizedName.Equals("NARRATORE", StringComparison.OrdinalIgnoreCase))
                return "Narratore";

            // Convert to Title Case
            var textInfo = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
            return textInfo.ToTitleCase(normalizedName.ToLowerInvariant());
        }

        /// <summary>
        /// Cleans text by removing extra whitespace and normalizing quotes.
        /// </summary>
        private string CleanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // Normalize whitespace
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // Remove quotes at start/end if present (they'll be added by TTS if needed)
            text = text.Trim('"', '«', '»', '"', '"');

            return text;
        }

        /// <summary>
        /// Assigns voices to characters based on available voices in the database.
        /// </summary>
        public void AssignVoices(TtsSchema schema)
        {
            if (_database == null)
            {
                _logger?.Log("Warning", "TtsSchemaGenerator", "No database available for voice assignment");
                return;
            }

            var voices = _database.ListTtsVoices();
            if (voices == null || !voices.Any())
            {
                _logger?.Log("Warning", "TtsSchemaGenerator", "No voices available in database");
                return;
            }

            var maleVoices = voices.Where(v => v.Gender?.ToLowerInvariant() == "male").ToList();
            var femaleVoices = voices.Where(v => v.Gender?.ToLowerInvariant() == "female").ToList();
            var narratorVoices = voices.Where(v => v.Name?.Contains("narrator", StringComparison.OrdinalIgnoreCase) == true).ToList();

            int maleIndex = 0;
            int femaleIndex = 0;

            foreach (var character in schema.Characters)
            {
                if (string.IsNullOrWhiteSpace(character.VoiceId))
                {
                    TtsVoice? selectedVoice = null;

                    if (character.Name.Equals("Narratore", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedVoice = narratorVoices.FirstOrDefault() ?? maleVoices.FirstOrDefault() ?? voices.FirstOrDefault();
                    }
                    else if (character.Gender?.ToLowerInvariant() == "female" && femaleVoices.Any())
                    {
                        selectedVoice = femaleVoices[femaleIndex % femaleVoices.Count];
                        femaleIndex++;
                    }
                    else if (maleVoices.Any())
                    {
                        selectedVoice = maleVoices[maleIndex % maleVoices.Count];
                        maleIndex++;
                    }
                    else
                    {
                        selectedVoice = voices.FirstOrDefault();
                    }

                    if (selectedVoice is not null)
                    {
                        character.VoiceId = selectedVoice.VoiceId;
                        character.Voice = selectedVoice.Name;
                        character.Gender = selectedVoice.Gender ?? "";
                    }
                }
            }

            _logger?.Log("Information", "TtsSchemaGenerator", 
                $"Assigned voices to {schema.Characters.Count} characters");
        }
    }
}
