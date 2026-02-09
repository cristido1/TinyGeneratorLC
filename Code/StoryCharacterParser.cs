using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using TinyGenerator.Models;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Parses character list from story generation step output.
    /// Supports tagged blocks like:
    /// [NOME: ...] [TITOLO: ... oppure nessuno] [SESSO: male|female|alien|robot]
    /// [ETA: et? approssimativa] [NOTE: carattere, ruolo, tratti distintivi]
    /// </summary>
    public static class StoryCharacterParser
    {
        /// <summary>
        /// Parses a character list text into a list of StoryCharacter objects.
        /// </summary>
        public static List<StoryCharacter> ParseCharacterList(string text)
        {
            var characters = new List<StoryCharacter>();
            
            if (string.IsNullOrWhiteSpace(text))
                return characters;

            var tagged = ParseTaggedCharacterBlocks(text);
            if (tagged.Count > 0)
            {
                characters.AddRange(tagged);
            }
            else
            {
                var inlineTagged = ParseInlinePersonaggioTags(text);
                if (inlineTagged.Count > 0)
                {
                    characters.AddRange(inlineTagged);
                }
                else
                {
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;
                        
                    // Skip header lines or separators
                    if (trimmed.StartsWith("#") || trimmed.StartsWith("-") || trimmed.StartsWith("="))
                        continue;
                    
                    // Skip lines that look like section headers
                    if (trimmed.StartsWith("[PERSONAGGI]", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("PERSONAGGI", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("LISTA", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var character = ParseCharacterLine(trimmed);
                    if (character != null && !string.IsNullOrWhiteSpace(character.Name))
                    {
                        characters.Add(character);
                    }
                }
                }
            }

            characters = characters
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            // Add Narratore if not present
            if (!characters.Any(c => c.Name.Equals("Narratore", StringComparison.OrdinalIgnoreCase)))
            {
                characters.Insert(0, new StoryCharacter
                {
                    Name = "Narratore",
                    Gender = "male",
                    Role = "narrator"
                });
            }

            return characters;
        }

        /// <summary>
        /// Parses a single character line.
        /// Format: Name [TITOLO: value] [SESSO: value] [ETA: value]
        /// </summary>
        private static StoryCharacter? ParseCharacterLine(string line)
        {
            var character = new StoryCharacter();
            
            // Extract bracketed attributes
            var titleMatch = Regex.Match(line, @"\[TITOLO:\s*([^\]]+)\]", RegexOptions.IgnoreCase);
            var genderMatch = Regex.Match(line, @"\[SESSO:\s*([^\]]+)\]", RegexOptions.IgnoreCase);
            var ageMatch = Regex.Match(line, @"\[ETA:\s*([^\]]+)\]", RegexOptions.IgnoreCase);
            var roleMatch = Regex.Match(line, @"\[RUOLO:\s*([^\]]+)\]", RegexOptions.IgnoreCase);
            
            if (titleMatch.Success)
                character.Title = titleMatch.Groups[1].Value.Trim().ToLowerInvariant();
            
            if (genderMatch.Success)
                character.Gender = NormalizeGender(genderMatch.Groups[1].Value.Trim());
            
            if (ageMatch.Success)
                character.Age = ageMatch.Groups[1].Value.Trim();
                
            if (roleMatch.Success)
                character.Role = roleMatch.Groups[1].Value.Trim().ToLowerInvariant();
            
            // Extract name: everything before the first bracket
            var nameEnd = line.IndexOf('[');
            var name = nameEnd > 0 ? line.Substring(0, nameEnd).Trim() : line.Trim();
            
            // Remove any leading numbers or bullets
            name = Regex.Replace(name, @"^\d+[\.\)]\s*", "").Trim();
            name = Regex.Replace(name, @"^[-•]\s*", "").Trim();
            
            if (string.IsNullOrWhiteSpace(name))
                return null;
                
            // Normalize name to Title Case
            character.Name = FormatName(name);
            
            // Generate aliases from the name
            character.Aliases = GenerateAliases(character.Name, character.Title);
            
            return character;
        }

        private static List<StoryCharacter> ParseTaggedCharacterBlocks(string text)
        {
            var characters = new List<StoryCharacter>();
            var pattern = @"\[NOME:\s*(?<name>[^\]]+)\]\s*\[TITOLO:\s*(?<title>[^\]]+)\]\s*\[SESSO:\s*(?<gender>[^\]]+)\]\s*(?:\r?\n|\s)*\[(?:ETA|ET[AÀ]):\s*(?<age>[^\]]+)\]\s*\[NOTE:\s*(?<note>[^\]]+)\]";
            var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                if (!match.Success) continue;
                var name = match.Groups["name"].Value.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var titleRaw = match.Groups["title"].Value.Trim();
                var genderRaw = match.Groups["gender"].Value.Trim();
                var ageRaw = match.Groups["age"].Value.Trim();
                var noteRaw = match.Groups["note"].Value.Trim();

                var character = new StoryCharacter
                {
                    Name = FormatName(name),
                    Title = string.IsNullOrWhiteSpace(titleRaw) || titleRaw.Equals("nessuno", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : titleRaw.ToLowerInvariant(),
                    Gender = NormalizeGender(genderRaw),
                    Age = string.IsNullOrWhiteSpace(ageRaw) ? null : ageRaw,
                    Role = string.IsNullOrWhiteSpace(noteRaw) ? null : noteRaw
                };

                character.Aliases = GenerateAliases(character.Name, character.Title);
                characters.Add(character);
            }

            return characters;
        }

        private static List<StoryCharacter> ParseInlinePersonaggioTags(string text)
        {
            var characters = new List<StoryCharacter>();
            var matches = Regex.Matches(text, @"\[PERSONAGGIO:\s*(?<name>[^\]]+)\]", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                if (!match.Success) continue;
                var name = match.Groups["name"].Value.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var character = new StoryCharacter
                {
                    Name = FormatName(name),
                    Gender = "male"
                };
                character.Aliases = GenerateAliases(character.Name, character.Title);
                characters.Add(character);
            }

            return characters;
        }


        /// <summary>
        /// Normalizes gender values to standard format.
        /// </summary>
        private static string NormalizeGender(string gender)
        {
            var lower = gender.ToLowerInvariant();
            
            return lower switch
            {
                "m" or "male" or "maschio" or "uomo" => "male",
                "f" or "female" or "femmina" or "donna" => "female",
                "robot" or "androide" or "ia" or "ai" => "robot",
                "alien" or "alieno" or "extraterrestre" => "alien",
                _ => "male" // default
            };
        }

        /// <summary>
        /// Formats a name to Title Case.
        /// </summary>
        private static string FormatName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
                
            var textInfo = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
            return textInfo.ToTitleCase(name.ToLowerInvariant());
        }

        /// <summary>
        /// Generates common aliases for a character based on their name and title.
        /// </summary>
        private static List<string> GenerateAliases(string name, string? title)
        {
            var aliases = new List<string>();
            var nameParts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            // Add full name in different cases
            aliases.Add(name.ToUpperInvariant());
            
            // Add surname only (last part)
            if (nameParts.Length > 1)
            {
                var surname = nameParts.Last();
                aliases.Add(surname);
                aliases.Add(surname.ToUpperInvariant());
            }
            
            // Add first name only
            if (nameParts.Length > 0)
            {
                var firstName = nameParts.First();
                if (nameParts.Length > 1) // Only add if there's also a surname
                {
                    aliases.Add(firstName);
                    aliases.Add(firstName.ToUpperInvariant());
                }
            }
            
            // Add with title
            if (!string.IsNullOrWhiteSpace(title))
            {
                var titleFormatted = System.Globalization.CultureInfo.CurrentCulture.TextInfo
                    .ToTitleCase(title.ToLowerInvariant());
                aliases.Add($"{titleFormatted} {name}");
                aliases.Add($"{title.ToUpperInvariant()} {name.ToUpperInvariant()}");
                
                // Title + surname
                if (nameParts.Length > 1)
                {
                    var surname = nameParts.Last();
                    aliases.Add($"{titleFormatted} {surname}");
                    aliases.Add($"{title.ToUpperInvariant()} {surname.ToUpperInvariant()}");
                }
            }
            
            return aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Serializes character list to JSON for storage.
        /// </summary>
        public static string ToJson(List<StoryCharacter> characters)
        {
            return JsonSerializer.Serialize(characters, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        /// <summary>
        /// Deserializes character list from JSON.
        /// Handles both regular arrays and nested arrays (e.g., [[...]] instead of [...]).
        /// Throws JsonException with details on parse failure.
        /// </summary>
        public static List<StoryCharacter> FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new List<StoryCharacter>();
                
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            
            Exception? lastError = null;
            
            try
            {
                // First, try parsing as a regular array
                var result = JsonSerializer.Deserialize<List<StoryCharacter>>(json, options);
                if (result != null && result.Count > 0)
                    return result;
            }
            catch (JsonException ex)
            {
                lastError = ex;
                // Fall through to try nested array
            }
            
            try
            {
                // Try parsing as nested array [[...]]
                var nested = JsonSerializer.Deserialize<List<List<StoryCharacter>>>(json, options);
                if (nested != null && nested.Count > 0 && nested[0] != null)
                    return nested[0];
            }
            catch (JsonException ex)
            {
                lastError ??= ex;
                // Fall through
            }
            
            // If we had an error, throw it with context
            if (lastError != null)
            {
                throw new JsonException($"Errore nel parsing del JSON personaggi: {lastError.Message}", lastError);
            }
            
            return new List<StoryCharacter>();
        }
        
        /// <summary>
        /// Tries to deserialize character list from JSON, returning success/failure with error message.
        /// </summary>
        public static (List<StoryCharacter> characters, string? error) TryFromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (new List<StoryCharacter>(), null);
                
            try
            {
                var result = FromJson(json);
                return (result, null);
            }
            catch (JsonException ex)
            {
                return (new List<StoryCharacter>(), ex.Message);
            }
        }

        /// <summary>
        /// Finds the canonical character matching a name variation.
        /// Returns the StoryCharacter if found, null otherwise.
        /// </summary>
        public static StoryCharacter? FindCharacter(List<StoryCharacter> characters, string nameVariation)
        {
            if (string.IsNullOrWhiteSpace(nameVariation) || characters == null)
                return null;

            var normalized = nameVariation.Trim();
            
            // Check Narratore first
            if (normalized.Equals("NARRATORE", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Narratore", StringComparison.OrdinalIgnoreCase))
            {
                return characters.FirstOrDefault(c => 
                    c.Name.Equals("Narratore", StringComparison.OrdinalIgnoreCase));
            }

            // Exact name match
            var exact = characters.FirstOrDefault(c => 
                c.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // Check aliases
            var byAlias = characters.FirstOrDefault(c => 
                c.Aliases?.Any(a => a.Equals(normalized, StringComparison.OrdinalIgnoreCase)) == true);
            if (byAlias != null) return byAlias;

            // Extract surname from variation and match
            var variationParts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (variationParts.Length > 0)
            {
                var surname = variationParts.Last().ToUpperInvariant();
                var bySurname = characters.FirstOrDefault(c =>
                {
                    var nameParts = c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    return nameParts.Length > 0 && 
                           nameParts.Last().Equals(surname, StringComparison.OrdinalIgnoreCase);
                });
                if (bySurname != null) return bySurname;
            }

            return null;
        }
    }
}
