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
        // Optional numeric score used by evaluations/admin
        public double? Score { get; set; }
        public string? Tags { get; set; } // JSON string
        public string? TemplateWav { get; set; }
        public string? Archetype { get; set; }
        public string? Notes { get; set; }
        // template_wav stores the sample wav filename (relative to wwwroot/data_voices_samples)
        // metadata and sample_path were removed from DB schema; template_wav now holds the sample filename
        public string? CreatedAt { get; set; }
        public string? UpdatedAt { get; set; }
    }
}
