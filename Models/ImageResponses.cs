using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TinyGenerator.Models
{
    public class GenerateCharactersResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("characters")]
        public List<CharacterResponse> Characters { get; set; } = new();
    }

    public class CharacterResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }

    public class GenerateBackgroundsResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("backgrounds")]
        public List<BackgroundResponse> Backgrounds { get; set; } = new();
    }

    public class BackgroundResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }

    public class GenerateScenesResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("scenes")]
        public List<SceneResponse> Scenes { get; set; } = new();
    }

    public class SceneResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }

    public class SceneRendererStatusResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }
}