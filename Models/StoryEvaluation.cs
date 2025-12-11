using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models
{
    [Table("stories_evaluations")]
    public class StoryEvaluation
    {
        [Column("id")]
        public long Id { get; set; }
        
        [Column("story_id")]
        public long StoryId { get; set; }

        [Column("narrative_coherence_score")]
        public int NarrativeCoherenceScore { get; set; }
        
        [Column("narrative_coherence_defects")]
        public string NarrativeCoherenceDefects { get; set; } = string.Empty;

        [Column("originality_score")]
        public int OriginalityScore { get; set; }
        
        [Column("originality_defects")]
        public string OriginalityDefects { get; set; } = string.Empty;

        [Column("emotional_impact_score")]
        public int EmotionalImpactScore { get; set; }
        
        [Column("emotional_impact_defects")]
        public string EmotionalImpactDefects { get; set; } = string.Empty;

        [Column("action_score")]
        public int ActionScore { get; set; }
        
        [Column("action_defects")]
        public string ActionDefects { get; set; } = string.Empty;

        [Column("total_score")]
        public double TotalScore { get; set; }
        // Backwards-compatible property for UI views (summary score)
        [NotMapped]
        public double Score { get => TotalScore; set => TotalScore = value; }
        
        [NotMapped]
        public string Model { get; set; } = string.Empty;
        
        [Column("raw_json")]
        public string RawJson { get; set; } = string.Empty;

        [Column("model_id")]
        public long? ModelId { get; set; }
        
        [Column("agent_id")]
        public int? AgentId { get; set; }
        
        [NotMapped]
        public string AgentName { get; set; } = string.Empty;
        
        [NotMapped]
        public string AgentModel { get; set; } = string.Empty;
        
        [Column("ts")]
        public string Timestamp { get; set; } = string.Empty;
        
        // Concurrency token for optimistic locking
        [Timestamp]
        public byte[]? RowVersion { get; set; }
    }
}
