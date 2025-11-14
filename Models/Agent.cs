using System;

namespace TinyGenerator.Models
{
    public class Agent
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty; // coordinator, writer, story_evaluator, musician, sfx, tts, ambient, mixer
        public int? ModelId { get; set; }
        // Reference to tts_voices.id (rowid) - used for referential integrity
        public int? VoiceId { get; set; }
        // Non-persistent helper to display linked model name in UI
        public string? ModelName { get; set; }
        public string? Skills { get; set; } // JSON array
        public string? Config { get; set; } // JSON object
        public string? Prompt { get; set; }
        public string? Instructions { get; set; }
        public string? ExecutionPlan { get; set; }
        public bool IsActive { get; set; } = true;
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
        public string? UpdatedAt { get; set; }
        public string? Notes { get; set; }
        // Non-persistent friendly name for the assigned TTS voice
        public string? VoiceName { get; set; }
    }
}
