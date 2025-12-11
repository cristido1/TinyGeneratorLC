using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("tts_voices")]
    public class TtsVoice
    {
        [Column("id")]
        public int Id { get; set; }
        
        [Column("voice_id")]
        public string VoiceId { get; set; } = string.Empty; // TTS service id
        
        [Column("name")]
        public string Name { get; set; } = string.Empty;
        
        [Column("model")]
        public string? Model { get; set; }
        
        [Column("language")]
        public string? Language { get; set; }
        
        [Column("gender")]
        public string? Gender { get; set; }
        
        [Column("age")]
        public string? Age { get; set; }
        
        [Column("confidence")]
        public double? Confidence { get; set; }
        
        // Optional numeric score used by evaluations/admin
        [Column("score")]
        public double? Score { get; set; }
        
        [Column("tags")]
        public string? Tags { get; set; } // JSON string
        
        [Column("template_wav")]
        public string? TemplateWav { get; set; }
        
        [Column("archetype")]
        public string? Archetype { get; set; }
        
        [Column("notes")]
        public string? Notes { get; set; }
        
        // template_wav stores the sample wav filename (relative to wwwroot/data_voices_samples)
        // metadata and sample_path were removed from DB schema; template_wav now holds the sample filename
        [Column("created_at")]
        public string? CreatedAt { get; set; }
        
        [Column("updated_at")]
        public string? UpdatedAt { get; set; }
        
        // Concurrency token for optimistic locking
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
