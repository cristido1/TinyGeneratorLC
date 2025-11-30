using System;

namespace TinyGenerator.Models
{
    public class StoryEvaluation
    {
        public long Id { get; set; }
        public long StoryId { get; set; }

        public int NarrativeCoherenceScore { get; set; }
        public string NarrativeCoherenceDefects { get; set; } = string.Empty;

        public int OriginalityScore { get; set; }
        public string OriginalityDefects { get; set; } = string.Empty;

        public int EmotionalImpactScore { get; set; }
        public string EmotionalImpactDefects { get; set; } = string.Empty;

        public int ActionScore { get; set; }
        public string ActionDefects { get; set; } = string.Empty;

        public double TotalScore { get; set; }
        // Backwards-compatible property for UI views (summary score)
        public double Score { get => TotalScore; set => TotalScore = value; }
        public string Model { get; set; } = string.Empty;
        public string RawJson { get; set; } = string.Empty;

        public long? ModelId { get; set; }
        public int? AgentId { get; set; }
        public string AgentName { get; set; } = string.Empty;
        public string AgentModel { get; set; } = string.Empty;
        public string Ts { get; set; } = string.Empty;
    }
}
