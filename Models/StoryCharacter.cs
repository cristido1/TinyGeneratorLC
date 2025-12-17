using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyGenerator.Models
{
    /// <summary>
    /// Represents a character in a story with their canonical name and attributes.
    /// Used for TTS voice assignment and character normalization.
    /// Format from story step: [NOME] [TITOLO: comandante/ufficiale/dottore...] [SESSO: male/female/robot/alien] [ETA: età]
    /// </summary>
    public class StoryCharacter
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
        
        [JsonPropertyName("title")]
        public string? Title { get; set; } // e.g., "comandante", "dottore", "tenente"
        
        [JsonPropertyName("gender")]
        public string Gender { get; set; } = "male"; // "male", "female", "robot", "alien"
        
        [JsonPropertyName("age")]
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? Age { get; set; } // e.g., "35", "anziano", "giovane" - accepts both string and number
        
        [JsonPropertyName("role")]
        public string? Role { get; set; } // e.g., "protagonist", "antagonist", "narrator"
        
        [JsonPropertyName("aliases")]
        public List<string>? Aliases { get; set; } // e.g., ["COMANDANTE CARTA", "Alessandro", "Carta"]
        
        /// <summary>
        /// Returns the full name with title for display purposes.
        /// </summary>
        [JsonIgnore]
        public string FullName => string.IsNullOrWhiteSpace(Title) 
            ? Name 
            : $"{Title} {Name}";
    }
    
    /// <summary>
    /// JSON converter that accepts both string and number values and converts them to string.
    /// </summary>
    public class FlexibleStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetInt64(out long l) ? l.ToString() : reader.GetDouble().ToString(),
                JsonTokenType.Null => null,
                _ => null
            };
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value);
        }
    }
}
