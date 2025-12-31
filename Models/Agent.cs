using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("agents")]
    public class Agent
    {
        [Column("id")]
        public int Id { get; set; }
        
        [Column("name")]
        public string Name { get; set; } = string.Empty;
        
        [Column("role")]
        public string Role { get; set; } = string.Empty; // coordinator, writer, story_evaluator, musician, sfx, tts, ambient, mixer
        
        [Column("model_id")]
        public int? ModelId { get; set; }
        
        // Reference to tts_voices.id (rowid) - used for referential integrity
        [Column("voice_rowid")]
        public int? VoiceId { get; set; }
        
        // Non-persistent helper to display linked model name in UI
        [NotMapped]
        public string? ModelName { get; set; }
        
        [Column("skills")]
        public string? Skills { get; set; } // JSON array
        
        [Column("config")]
        public string? Config { get; set; } // JSON object
        
        [Column("json_response_format")]
        public string? JsonResponseFormat { get; set; } // Nome file schema JSON (es. "full_evaluation.json")
        
        [Column("prompt")]
        public string? Prompt { get; set; }
        
        [Column("instructions")]
        public string? Instructions { get; set; }
        
        [Column("execution_plan")]
        public string? ExecutionPlan { get; set; }
        
        [Column("is_active")]
        public bool IsActive { get; set; } = true;
        
        [Column("created_at")]
        public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
        
        [Column("updated_at")]
        public string? UpdatedAt { get; set; }
        
        [Column("notes")]
        public string? Notes { get; set; }
        
        // Non-persistent friendly name for the assigned TTS voice
        [NotMapped]
        public string? VoiceName { get; set; }
        
        [Column("temperature")]
        public double? Temperature { get; set; } // Sampling temperature for model calls (0.0-2.0)
        
        [Column("top_p")]
        public double? TopP { get; set; } // Nucleus sampling probability (0.0-1.0)

        [Column("repeat_penalty")]
        public double? RepeatPenalty { get; set; } // Penalize repetition (model-specific)

        [Column("top_k")]
        public int? TopK { get; set; } // Top-K sampling (model-specific)

        [Column("repeat_last_n")]
        public int? RepeatLastN { get; set; } // Repeat window size (model-specific)

        [Column("num_predict")]
        public int? NumPredict { get; set; } // Max tokens to predict (model-specific)
        
        // Multi-step template association
        [Column("multi_step_template_id")]
        public int? MultiStepTemplateId { get; set; }
        
        // Non-persistent helper
        [NotMapped]
        public string? MultiStepTemplateName { get; set; }
        
        // Concurrency token for optimistic locking
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
