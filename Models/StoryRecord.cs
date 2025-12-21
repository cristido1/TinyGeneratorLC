using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("stories")]
    public class StoryRecord
    {
        [Column("id")]
        public long Id { get; set; }
        
        [Column("generation_id")]
        public string GenerationId { get; set; } = string.Empty;
        
        [Column("memory_key")]
        public string MemoryKey { get; set; } = string.Empty;
        
        [Column("ts")]
        public string Timestamp { get; set; } = string.Empty;
        
        [Column("prompt")]
        public string Prompt { get; set; } = string.Empty;
        
        [Column("title")]
        public string? Title { get; set; }
        
        [Column("story")]
        public string Story { get; set; } = string.Empty;
        
        [Column("char_count")]
        public int CharCount { get; set; }
        
        [NotMapped]
        public string Model { get; set; } = string.Empty;
        
        [NotMapped]
        public string Agent { get; set; } = string.Empty;
        
        [Column("eval")]
        public string Eval { get; set; } = string.Empty;
        
        [Column("score")]
        public double Score { get; set; }
        
        [Column("approved")]
        public bool Approved { get; set; }
        
        [NotMapped]
        public string Status { get; set; } = string.Empty;
        
        [Column("status_id")]
        public int? StatusId { get; set; }
        
        [NotMapped]
        public string? StatusDescription { get; set; }
        
        [NotMapped]
        public string? StatusColor { get; set; }
        
        [NotMapped]
        public string? StatusOperationType { get; set; }
        
        [NotMapped]
        public string? StatusAgentType { get; set; }
        
        [NotMapped]
        public string? StatusFunctionName { get; set; }
        
        [NotMapped]
        public int? StatusStep { get; set; }
        
        [Column("folder")]
        public string? Folder { get; set; }
        
        [NotMapped]
        public bool HasVoiceSource { get; set; }

        // Generated asset flags
        [Column("generated_tts_json")]
        public bool GeneratedTtsJson { get; set; }

        [Column("generated_tts")]
        public bool GeneratedTts { get; set; }

        [Column("generated_ambient")]
        public bool GeneratedAmbient { get; set; }

        [Column("generated_music")]
        public bool GeneratedMusic { get; set; }

        [Column("generated_effects")]
        public bool GeneratedEffects { get; set; }

        [Column("generated_mixed_audio")]
        public bool GeneratedMixedAudio { get; set; }

        /// <summary>
        /// Indicates if the story has a final mixed audio file (final_mix.wav or final_mix.mp3)
        /// </summary>
        [NotMapped]
        public bool HasFinalMix { get; set; }

        // Test information (if story was generated from a test)
        [NotMapped]
        public int? TestRunId { get; set; }
        
        [NotMapped]
        public int? TestStepId { get; set; }
        
        [Column("model_id")]
        public int? ModelId { get; set; }
        
        [Column("agent_id")]
        public int? AgentId { get; set; }

        /// <summary>
        /// JSON string containing the list of characters with their canonical names and genders.
        /// Format: [{"name": "Carta", "gender": "male", "role": "protagonist", "aliases": ["COMANDANTE CARTA", "Alessandro Carta"]}]
        /// </summary>
        [Column("characters")]
        public string? Characters { get; set; }

        // Evaluations attached to the story (one for each saved evaluation)
        [NotMapped]
        public List<StoryEvaluation> Evaluations { get; set; } = new List<StoryEvaluation>();
        
        // Legacy properties for backward compatibility (mapped to Story field)
        [Obsolete("Use Story property instead")]
        [NotMapped]
        public string StoryA { get => Story; set => Story = value; }
        [Obsolete("Use Model property instead")]
        [NotMapped]
        public string ModelA { get => Model; set => Model = value; }
        [Obsolete("Use Eval property instead")]
        [NotMapped]
        public string EvalA { get => Eval; set => Eval = value; }
        [Obsolete("Use Score property instead")]
        [NotMapped]
        public double ScoreA { get => Score; set => Score = value; }
        
        // Concurrency token for optimistic locking
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
