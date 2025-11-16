using System;

namespace TinyGenerator.Models
{
    public class StoryEvaluation
    {
        public long Id { get; set; }
        public long StoryId { get; set; }

        public int NarrativeCoherenceScore { get; set; }
        public string NarrativeCoherenceDefects { get; set; } = string.Empty;

        public int StructureScore { get; set; }
        public string StructureDefects { get; set; } = string.Empty;

        public int CharacterizationScore { get; set; }
        public string CharacterizationDefects { get; set; } = string.Empty;

        public int DialoguesScore { get; set; }
        public string DialoguesDefects { get; set; } = string.Empty;

        public int PacingScore { get; set; }
        public string PacingDefects { get; set; } = string.Empty;

        public int OriginalityScore { get; set; }
        public string OriginalityDefects { get; set; } = string.Empty;

        public int StyleScore { get; set; }
        public string StyleDefects { get; set; } = string.Empty;

        public int WorldbuildingScore { get; set; }
        public string WorldbuildingDefects { get; set; } = string.Empty;

        public int ThematicCoherenceScore { get; set; }
        public string ThematicCoherenceDefects { get; set; } = string.Empty;

        public int EmotionalImpactScore { get; set; }
        public string EmotionalImpactDefects { get; set; } = string.Empty;

        public double TotalScore { get; set; }
        // Backwards-compatible property for UI views (summary score)
        public double Score { get => TotalScore; set => TotalScore = value; }
        public string Model { get; set; } = string.Empty;
        public string OverallEvaluation { get; set; } = string.Empty;
        public string RawJson { get; set; } = string.Empty;

        public long? ModelId { get; set; }
        public int? AgentId { get; set; }
        public string Ts { get; set; } = string.Empty;
    }
}
