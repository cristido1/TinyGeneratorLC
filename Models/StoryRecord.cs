using System;
using System.Collections.Generic;

namespace TinyGenerator.Models
{
    public class StoryRecord
    {
        public long Id { get; set; }
        public string MemoryKey { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string StoryA { get; set; } = string.Empty;
        public string EvalA { get; set; } = string.Empty;
        public double ScoreA { get; set; }
        public string ModelA { get; set; } = string.Empty;
        public string StoryB { get; set; } = string.Empty;
        public string EvalB { get; set; } = string.Empty;
        public double ScoreB { get; set; }
        public string ModelB { get; set; } = string.Empty;
        public string StoryC { get; set; } = string.Empty;
        public string EvalC { get; set; } = string.Empty;
        public double ScoreC { get; set; }
        public string ModelC { get; set; } = string.Empty;
        public bool Approved { get; set; }
        public string Status { get; set; } = string.Empty;
        // Optional generation id (grouping for A/B/C variants)
        public string GenerationId { get; set; } = string.Empty;

        // Evaluations attached to the story (one for each saved evaluation)
        public List<StoryEvaluation> Evaluations { get; set; } = new List<StoryEvaluation>();
    }
}
