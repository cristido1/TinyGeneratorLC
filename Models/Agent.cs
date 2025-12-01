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
        public string? JsonResponseFormat { get; set; } // Nome file schema JSON (es. "full_evaluation.json")
        public string? Prompt { get; set; }
        public string? Instructions { get; set; }
        public string? ExecutionPlan { get; set; }
        public bool IsActive { get; set; } = true;
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
        public string? UpdatedAt { get; set; }
        public string? Notes { get; set; }
        // Non-persistent friendly name for the assigned TTS voice
        public string? VoiceName { get; set; }
        public double? Temperature { get; set; } // Sampling temperature for model calls (0.0-2.0)
        public double? TopP { get; set; } // Nucleus sampling probability (0.0-1.0)
        // Multi-step template association
        public int? MultiStepTemplateId { get; set; }
        // Non-persistent helper
        public string? MultiStepTemplateName { get; set; }
    }
}
