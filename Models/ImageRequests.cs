using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TinyGenerator.Models
{
    public class GenerateCharactersRequest
    {
        [JsonPropertyName("story_id")]
        public string StoryId { get; set; } = string.Empty;

        [JsonPropertyName("characters")]
        public List<CharacterImageRequest> Characters { get; set; } = new();
    }

    public class CharacterImageRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("emotion")]
        public string? Emotion { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("style")]
        public string? Style { get; set; }

        [JsonPropertyName("seed")]
        public int? Seed { get; set; }

        [JsonPropertyName("output_size")]
        public int[]? OutputSize { get; set; }

        [JsonPropertyName("background")]
        public string? Background { get; set; }

        [JsonPropertyName("clothing")]
        public string? Clothing { get; set; }

        [JsonPropertyName("accessories")]
        public string? Accessories { get; set; }
    }

    public class GenerateBackgroundsRequest
    {
        [JsonPropertyName("story_id")]
        public string StoryId { get; set; } = string.Empty;

        [JsonPropertyName("environments")]
        public List<BackgroundEnvironmentRequest> Environments { get; set; } = new();

        [JsonPropertyName("output_dir")]
        public string? OutputDir { get; set; }
    }

    public class BackgroundEnvironmentRequest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("prompt")]
        public string? Prompt { get; set; }
    }

    public class GenerateScenesRequest
    {
        [JsonPropertyName("story_id")]
        public string StoryId { get; set; } = string.Empty;

        [JsonPropertyName("scenes")]
        public List<SceneRenderRequest> Scenes { get; set; } = new();
    }

    public class SceneRenderRequest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("background")]
        public string Background { get; set; } = string.Empty;

        [JsonPropertyName("characters")]
        public List<SceneCharacterPlacementRequest> Characters { get; set; } = new();
    }

    public class SceneCharacterPlacementRequest
    {
        [JsonPropertyName("image")]
        public string Image { get; set; } = string.Empty;

        [JsonPropertyName("position")]
        public int[] Position { get; set; } = new[] { 0, 0 };
    }
}