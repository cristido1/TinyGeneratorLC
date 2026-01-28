using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using System.Text;
using TinyGenerator.Models;
using System.Text.Json.Nodes;

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

        private static readonly HashSet<char> _ttsTextDisallowedChars = new()
        {
            '*', '-', '_', '"', '(', ')'
        };

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
            
            // Normalize tags that appear inline so they start on their own line.
            storyText = NormalizeInlineTags(storyText);

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
            // NOTE: Ambience now stores environment description for future image generation, NOT for audio
            string? pendingAmbience = null;
            
            // Track pending ambient sounds (from [RUMORI: ...] tag) - these are used for AudioCraft generation.
            // Requirement: background ambient sounds persist from when they're signaled until the next ambient tag (covering the whole story).
            string? pendingAmbientSounds = null;
            
            // Track pending FX for the next phrase
            string? pendingFxDescription = null;
            int? pendingFxDuration = null;
            
            // Track pending music for the next phrase
            string? pendingMusicDescription = null;
            int? pendingMusicDuration = null;

            // Track pending emotion from [EMOZIONE: xxx] tag - applies to the next character
            string? pendingEmotion = null;
            // Track pending character from [PERSONAGGIO: Nome] tag when text follows after another tag
            string? pendingCharacter = null;

            // Process each tag and extract the following text
            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var tagContent = match.Groups[1].Value.Trim();
                
                // Find the text between this tag and the next tag (or end of text)
                int textStart = match.Index + match.Length;
                int textEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : storyText.Length;
                var text = storyText.Substring(textStart, textEnd - textStart).Trim();

                // Handle standalone [EMOZIONE: xxx] tag - store for next character
                if (tagContent.StartsWith("EMOZIONE", StringComparison.OrdinalIgnoreCase))
                {
                    var colonIndex = tagContent.IndexOf(':');
                    if (colonIndex >= 0)
                    {
                        pendingEmotion = tagContent.Substring(colonIndex + 1).Trim().ToLowerInvariant();
                        _logger?.Log("Debug", "TtsSchemaGenerator", 
                            $"Parsed standalone EMOZIONE tag: '{pendingEmotion}'");
                    }
                    if (!string.IsNullOrWhiteSpace(pendingCharacter) && !string.IsNullOrWhiteSpace(text))
                    {
                        var pendingCharacterName = pendingCharacter;
                        var pendingEmotionValue = string.IsNullOrWhiteSpace(pendingEmotion) ? "neutral" : pendingEmotion;
                        pendingCharacter = null;
                        pendingEmotion = null;

                        // Apply pending emotion from standalone [EMOZIONE: xxx] tag if no emotion was found yet
                        if (string.IsNullOrWhiteSpace(pendingEmotionValue))
                        {
                            pendingEmotionValue = "neutral";
                        }

                        // Try to find matching character from story's character list
                        var matchedPendingChar = StoryCharacterParser.FindCharacter(storyCharLookup, pendingCharacterName);
                        string pendingCanonicalName;
                        string pendingGender = "male";

                        if (matchedPendingChar != null)
                        {
                            pendingCanonicalName = matchedPendingChar.Name;
                            pendingGender = matchedPendingChar.Gender ?? "male";
                        }
                        else
                        {
                            var normalizedName = NormalizeCharacterName(pendingCharacterName);
                            pendingCanonicalName = FormatCharacterName(normalizedName);
                        }

                        var pendingExistingKey = characters.Keys.FirstOrDefault(k => 
                            k.Equals(pendingCanonicalName, StringComparison.OrdinalIgnoreCase));

                        string pendingCharacterKey;
                        if (pendingExistingKey != null)
                        {
                            pendingCharacterKey = pendingExistingKey;
                        }
                        else
                        {
                            pendingCharacterKey = pendingCanonicalName;
                            var ttsChar = new TtsCharacter
                            {
                                Name = pendingCharacterKey,
                                EmotionDefault = "neutral",
                                Gender = pendingGender
                            };

                            if (voiceAssignments != null && voiceAssignments.TryGetValue(pendingCharacterKey, out var voiceId))
                            {
                                ttsChar.VoiceId = voiceId;
                            }

                            characters[pendingCharacterKey] = ttsChar;
                        }

                        var pendingPhrase = new TtsPhrase
                        {
                            Character = pendingCharacterKey,
                            Text = CleanText(text),
                            Emotion = pendingEmotionValue,
                            Ambience = pendingAmbience,
                            AmbientSounds = pendingAmbientSounds,
                            FxDescription = pendingFxDescription,
                            FxDuration = pendingFxDuration,
                            MusicDescription = pendingMusicDescription,
                            MusicDuration = pendingMusicDuration ?? (pendingMusicDescription != null ? 10 : null)
                        };

                        pendingAmbience = null;
                        // NOTE: do NOT reset pendingAmbientSounds here: it must persist until the next [RUMORI] tag.
                        pendingFxDescription = null;
                        pendingFxDuration = null;
                        pendingMusicDescription = null;

                        timeline.Add(pendingPhrase);
                    }
                    continue;
                }

                // Handle AMBIENTE/AMBIENTAZIONE tags - extract description for future image generation (NOT for audio)
                if (tagContent.StartsWith("AMBIENTE", StringComparison.OrdinalIgnoreCase) ||
                    tagContent.StartsWith("AMBIENTAZIONE", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract ambience description from tag content (after colon) or from the following text
                    var colonIndex = tagContent.IndexOf(':');
                    var desc = colonIndex >= 0 ? tagContent.Substring(colonIndex + 1).Trim() : string.Empty;
                    if (string.IsNullOrWhiteSpace(desc))
                    {
                        desc = text ?? string.Empty;
                    }
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        pendingAmbience = FilterTextField(desc);
                        _logger?.Log("Debug", "TtsSchemaGenerator",
                            $"Parsed ambiente/ambientazione (for future image generation): '{pendingAmbience}'");
                    }
                    continue;
                }

                // Handle RUMORI tag - extract ambient sounds description for AudioCraft generation
                if (tagContent.StartsWith("RUMORI", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract ambient sounds description from tag content (after colon) or from the following text
                    var colonIndex = tagContent.IndexOf(':');
                    var desc = colonIndex >= 0 ? tagContent.Substring(colonIndex + 1).Trim() : string.Empty;
                    if (string.IsNullOrWhiteSpace(desc))
                    {
                        desc = text ?? string.Empty;
                    }
                    // Allow explicit clearing of ambient sounds
                    if (!string.IsNullOrWhiteSpace(desc) &&
                        (desc.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                         desc.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                         desc.Equals("nessuno", StringComparison.OrdinalIgnoreCase) ||
                         desc.Equals("silenzio", StringComparison.OrdinalIgnoreCase)))
                    {
                        pendingAmbientSounds = null;
                        _logger?.Log("Debug", "TtsSchemaGenerator", "Cleared ambient sounds (OFF/NONE)");
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        pendingAmbientSounds = FilterTextField(desc);
                        _logger?.Log("Debug", "TtsSchemaGenerator",
                            $"Parsed ambient sounds (for AudioCraft): '{pendingAmbientSounds}'");
                    }
                    continue;
                }

                // Handle MUSIC/MUSICA tags - extract info and apply to next phrase
                // New mandatory format (formatter): [MUSIC: <duration_in_seconds> | <type>]
                if (tagContent.StartsWith("MUSIC", StringComparison.OrdinalIgnoreCase))
                {
                    var colonIndex = tagContent.IndexOf(':');
                    var afterColon = colonIndex >= 0 ? tagContent.Substring(colonIndex + 1).Trim() : string.Empty;

                    // Expected: "20 | suspense" (duration | type)
                    var parts = afterColon.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2)
                    {
                        var durationStr = parts[0].Trim().Replace(',', '.');
                        var type = parts[1].Trim();

                        if (double.TryParse(durationStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var durSeconds) &&
                            durSeconds > 0)
                        {
                            pendingMusicDuration = (int)Math.Ceiling(durSeconds);
                        }
                        else
                        {
                            pendingMusicDuration = null;
                        }

                        // We store the type in music_description so downstream can resolve it to a file.
                        pendingMusicDescription = !string.IsNullOrWhiteSpace(type)
                            ? FilterTextField(type)
                            : null;

                        // opening/ending are always applied in the final mix, not inside tts_schema.json timeline.
                        if (string.Equals(pendingMusicDescription, "opening", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(pendingMusicDescription, "ending", StringComparison.OrdinalIgnoreCase))
                        {
                            pendingMusicDuration = null;
                            pendingMusicDescription = null;
                        }

                        _logger?.Log("Debug", "TtsSchemaGenerator",
                            $"Parsed MUSIC: duration={pendingMusicDuration?.ToString() ?? "(null)"}s, type='{pendingMusicDescription}'");
                    }
                    else
                    {
                        _logger?.Log("Warning", "TtsSchemaGenerator",
                            $"Invalid MUSIC tag format (expected '[MUSIC: <seconds> | <type>]'): '{tagContent}'");
                    }

                    continue;
                }

                if (tagContent.StartsWith("MUSICA", StringComparison.OrdinalIgnoreCase))
                {
                    // Expected format: [MUSICA: <duration_seconds>] descriptive prompt text
                    var colonIndex = tagContent.IndexOf(':');
                    var desc = colonIndex >= 0 ? tagContent.Substring(colonIndex + 1).Trim() : string.Empty;

                    if (colonIndex >= 0 && !string.IsNullOrWhiteSpace(desc))
                    {
                        // Try patterns where the tag contains the duration only, or duration + short desc
                        // Pattern A: "15 secondi, descrizione"
                        var durationAtStartMatch = Regex.Match(desc, @"^(\d+(?:[.,]\d+)?)\s*(?:secondi?|sec|s)\s*,\s*(.+)$", RegexOptions.IgnoreCase);
                        if (durationAtStartMatch.Success)
                        {
                            var durationStr = durationAtStartMatch.Groups[1].Value.Replace(',', '.');
                            pendingMusicDuration = (int)Math.Ceiling(double.Parse(durationStr, System.Globalization.CultureInfo.InvariantCulture));
                            pendingMusicDescription = FilterTextField(durationAtStartMatch.Groups[2].Value.Trim());
                            _logger?.Log("Debug", "TtsSchemaGenerator", $"Parsed MUSICA (duration+desc): duration={pendingMusicDuration}s, desc='{pendingMusicDescription}'");
                            continue;
                        }

                        // Pattern B: duration only in tag, description provided in following text
                        var durationOnlyMatch = Regex.Match(desc, @"^(\d+(?:[.,]\d+)?)\s*(?:secondi?|sec|s)?$", RegexOptions.IgnoreCase);
                        if (durationOnlyMatch.Success)
                        {
                            var durationStr = durationOnlyMatch.Groups[1].Value.Replace(',', '.');
                            pendingMusicDuration = (int)Math.Ceiling(double.Parse(durationStr, System.Globalization.CultureInfo.InvariantCulture));
                            pendingMusicDescription = !string.IsNullOrWhiteSpace(text) ? FilterTextField(text.Trim()) : null;
                            _logger?.Log("Debug", "TtsSchemaGenerator", $"Parsed MUSICA (duration only in tag): duration={pendingMusicDuration}s, desc='{pendingMusicDescription}'");
                            continue;
                        }

                        // Pattern C: look for 'durata X s' inside the desc
                        var durataMatch = Regex.Match(desc, @"durata\s+(\d+(?:[.,]\d+)?)\s*(?:secondi?|sec|s)?", RegexOptions.IgnoreCase);
                        if (durataMatch.Success)
                        {
                            var durationStr = durataMatch.Groups[1].Value.Replace(',', '.');
                            pendingMusicDuration = (int)Math.Ceiling(double.Parse(durationStr, System.Globalization.CultureInfo.InvariantCulture));
                            // remove durata part from desc
                            pendingMusicDescription = FilterTextField(
                                Regex.Replace(desc, @",?\s*durata\s+\d+(?:[.,]\d+)?\s*(?:secondi?|sec|s)?\s*,?", ", ", RegexOptions.IgnoreCase)
                                    .Trim().Trim(',').Trim());
                            _logger?.Log("Debug", "TtsSchemaGenerator", $"Parsed MUSICA (durata keyword): duration={pendingMusicDuration}s, desc='{pendingMusicDescription}'");
                            continue;
                        }

                        // Fallback: treat the whole desc as description (no duration parsed)
                        pendingMusicDescription = FilterTextField(desc);
                        _logger?.Log("Debug", "TtsSchemaGenerator", $"Parsed MUSICA (fallback desc): '{pendingMusicDescription}'");
                        continue;
                    }

                    // No colon or empty tag content: use following text as description if present
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        pendingMusicDescription = FilterTextField(text.Trim());
                        _logger?.Log("Debug", "TtsSchemaGenerator", $"Parsed MUSICA (text desc): '{pendingMusicDescription}'");
                    }
                    continue;
                }

                // Handle FX tags - formats supported:
                // [FX: 15 secondi, descrizione effetto sonoro]
                // [FX: 2 s, clic di tastiera, tintinnio]
                // [FX: Suono di un clic, durata 0.5 s, chiaro e leggero.]
                // [FX: 2 s] descrizione su riga successiva
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
                            pendingFxDescription = FilterTextField(durationAtStartMatch.Groups[2].Value.Trim());
                            _logger?.Log("Debug", "TtsSchemaGenerator", 
                                $"Parsed FX (duration at start): duration={pendingFxDuration}s, description='{pendingFxDescription}'");
                        }
                        // Pattern 1b: Duration only in tag, description in following text
                        else if (Regex.IsMatch(afterColon, @"^(\d+(?:[.,]\d+)?)\s*(?:secondi?|sec|s)\s*$", RegexOptions.IgnoreCase))
                        {
                            var durationStr = Regex.Match(afterColon, @"^(\d+(?:[.,]\d+)?)").Groups[1].Value.Replace(',', '.');
                            pendingFxDuration = (int)Math.Ceiling(double.Parse(durationStr, System.Globalization.CultureInfo.InvariantCulture));
                            pendingFxDescription = !string.IsNullOrWhiteSpace(text) ? FilterTextField(text.Trim()) : string.Empty;
                            _logger?.Log("Debug", "TtsSchemaGenerator",
                                $"Parsed FX (duration in tag, description in text): duration={pendingFxDuration}s, description='{pendingFxDescription}'");
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
                                pendingFxDescription = FilterTextField(
                                    Regex.Replace(afterColon, @",?\s*durata\s+\d+(?:[.,]\d+)?\s*(?:secondi?|sec|s)?\s*,?", ", ", RegexOptions.IgnoreCase)
                                        .Trim().Trim(',').Trim());
                                _logger?.Log("Debug", "TtsSchemaGenerator", 
                                    $"Parsed FX (durata keyword): duration={pendingFxDuration}s, description='{pendingFxDescription}'");
                            }
                            else if (!string.IsNullOrWhiteSpace(afterColon))
                            {
                                // No duration specified, use default
                                pendingFxDuration = 5;
                                pendingFxDescription = FilterTextField(afterColon);
                                _logger?.Log("Debug", "TtsSchemaGenerator", 
                                    $"Parsed FX (no duration, default 5s): description='{pendingFxDescription}'");
                            }
                            else if (!string.IsNullOrWhiteSpace(text))
                            {
                                pendingFxDuration = 5;
                                pendingFxDescription = FilterTextField(text.Trim());
                                _logger?.Log("Debug", "TtsSchemaGenerator",
                                    $"Parsed FX (description in text, default 5s): description='{pendingFxDescription}'");
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
                            pendingFxDescription = FilterTextField(fxParts[2].Trim());
                            _logger?.Log("Debug", "TtsSchemaGenerator", 
                                $"Parsed FX: duration={pendingFxDuration}s, description='{pendingFxDescription}'");
                        }
                        else if (fxParts.Length == 2)
                        {
                            // Format: [FX, description] with default duration
                            pendingFxDescription = FilterTextField(fxParts[1].Trim());
                            pendingFxDuration = 5; // default 5 seconds
                            _logger?.Log("Debug", "TtsSchemaGenerator", 
                                $"Parsed FX (default duration): description='{pendingFxDescription}'");
                        }
                    }
                    continue;
                }

                // If we have [PERSONAGGIO: Nome] with no text before the next tag,
                // remember it and wait for [EMOZIONE: ...] or the next tag with text.
                if (tagContent.StartsWith("PERSONAGGIO", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(text))
                {
                    var (pendingChar, pendingEmo) = ParseTag(tagContent);
                    pendingCharacter = pendingChar;
                    if (!string.IsNullOrWhiteSpace(pendingEmo) && pendingEmo != "neutral")
                    {
                        pendingEmotion = pendingEmo;
                    }
                    continue;
                }

                // Parse the tag content
                var (character, emotion) = ParseTag(tagContent);

                // Some models write the emotion immediately AFTER the tag as a parenthetical,
                // e.g. "[DR. MARCUS THOMPSON] (con curiosità) testo...".
                // Detect a leading parenthetical in the text and treat it as the emotion
                // (equivalent to having written [personaggio, emozione]). Remove it from
                // the TTS text so it does not get spoken.
                var leadingParenEmotion = (string?)null;
                var parenMatch = Regex.Match(text ?? string.Empty, @"^\s*\(([^)]+)\)\s*[,:-]?\s*(.*)$", RegexOptions.Singleline);
                if (parenMatch.Success)
                {
                    leadingParenEmotion = parenMatch.Groups[1].Value.Trim();
                    text = parenMatch.Groups[2].Value.Trim();
                    _logger?.Log("Debug", "TtsSchemaGenerator", $"Detected leading parenthetical emotion: '{leadingParenEmotion}' for tag '{tagContent}'");
                }

                if (!string.IsNullOrWhiteSpace(leadingParenEmotion) && (string.IsNullOrWhiteSpace(emotion) || emotion == "neutral"))
                {
                    emotion = leadingParenEmotion.ToLowerInvariant();
                }

                // Apply pending emotion from standalone [EMOZIONE: xxx] tag if no emotion was found yet
                if (!string.IsNullOrWhiteSpace(pendingEmotion) && (string.IsNullOrWhiteSpace(emotion) || emotion == "neutral"))
                {
                    emotion = pendingEmotion;
                    _logger?.Log("Debug", "TtsSchemaGenerator", $"Applied pending emotion '{pendingEmotion}' to character '{character}'");
                    pendingEmotion = null; // Reset after use
                }

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

                // Create a JsonObject phrase that contains both legacy camelCase/PascalCase
                // fields and the new snake_case normalized names so consumers can read either.
                var phraseNode = new JsonObject
                {
                    ["character"] = characterKey,
                    ["text"] = CleanText(text),
                    ["emotion"] = string.IsNullOrWhiteSpace(emotion) ? "neutral" : emotion,
                    ["fileName"] = null,
                    ["durationMs"] = null,
                    ["startMs"] = null,
                    ["endMs"] = null,

                    // Standardized snake_case fields only
                    ["ambient_sound_description"] = pendingAmbientSounds,
                    ["ambient_sound_file"] = null,

                    ["fx_description"] = pendingFxDescription,
                    ["fx_duration"] = pendingFxDuration,
                    ["fx_file"] = null,

                    ["music_description"] = pendingMusicDescription,
                    ["music_duration"] = pendingMusicDuration ?? (pendingMusicDescription != null ? 10 : null),
                    ["music_file"] = null
                };

                // Reset pending ambience, FX, and music after applying to phrase (applied only once).
                // Do NOT reset pendingAmbientSounds: it persists until the next ambient tag.
                pendingAmbience = null;
                pendingFxDescription = null;
                pendingFxDuration = null;
                pendingMusicDescription = null;

                timeline.Add(phraseNode);
            }

            schema.Characters = characters.Values.ToList();
            schema.Timeline = timeline;

            _logger?.Log("Information", "TtsSchemaGenerator", 
                $"Generated TTS schema: {schema.Characters.Count} characters, {schema.Timeline.Count} phrases");

            return schema;
        }

        private static string NormalizeInlineTags(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;

            return Regex.Replace(
                text,
                @"(?<!^)(?<!\r)(?<!\n)(\[(?:NARRATORE|PERSONAGGIO:|EMOZIONE:|SENTIMENTO:|RUMORI|RUMORE|AMBIENTE|FX|MUSIC)\b)",
                "\n$1",
                RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Parses a tag like "NARRATORE", "Mario, arrabbiato", or "PERSONAGGIO: Nome | EMOZIONE: emotion" into character and emotion.
        /// Supported formats:
        ///   - NARRATORE
        ///   - personaggio, emozione
        ///   - PERSONAGGIO: Nome | EMOZIONE: emotion
        ///   - PERSONAGGIO: Nome (emotion can arrive via standalone [EMOZIONE: ...] tag)
        ///   - PERSONAGGIO: Nome
        ///   - just a name (without emotion)
        /// </summary>
        private (string character, string emotion) ParseTag(string tagContent)
        {
            // Check for NARRATORE
            if (tagContent.Equals("NARRATORE", StringComparison.OrdinalIgnoreCase))
            {
                return ("Narratore", "neutral");
            }

            // Check for "PERSONAGGIO: Nome | EMOZIONE: emotion" format
            var personaggioMatch = Regex.Match(tagContent, 
                @"^PERSONAGGIO:\s*(?<name>[^|]+?)\s*(?:\|\s*EMOZIONE:\s*(?<emo>.+))?\s*$", 
                RegexOptions.IgnoreCase);
            if (personaggioMatch.Success)
            {
                var name = personaggioMatch.Groups["name"].Value.Trim();
                var emo = personaggioMatch.Groups["emo"].Success 
                    ? personaggioMatch.Groups["emo"].Value.Trim().ToLowerInvariant() 
                    : "neutral";
                return (name, emo);
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
            return FilterTextField(text);
        }

        /// <summary>
        /// Filters a text field that will be persisted in tts_schema.json.
        /// Requirements:
        /// - Remove: * - _ " ( )
        /// - Do not apply extra escaping here; JSON escaping is handled by the serializer.
        /// </summary>
        private string FilterTextField(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (_ttsTextDisallowedChars.Contains(ch))
                    continue;
                sb.Append(ch);
            }

            // Normalize whitespace after stripping characters
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
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

            var voices = _database.ListTtsVoices(onlyEnabled: true);
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
