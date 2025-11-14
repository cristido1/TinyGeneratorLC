using System;

namespace TinyGenerator.Models
{
    public class TtsVoice
    {
        public int Id { get; set; }
        public string VoiceId { get; set; } = string.Empty; // TTS service id
        public string Name { get; set; } = string.Empty;
        public string? Model { get; set; }
        public string? Language { get; set; }
        public string? Gender { get; set; }
        public string? Age { get; set; }
        public double? Confidence { get; set; }
        public string? Tags { get; set; } // JSON string
        public string? SamplePath { get; set; }
        public string? TemplateWav { get; set; }
        public string? Metadata { get; set; } // full JSON of voice info
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }
}
