using System;
using System.Collections.Generic;

namespace TinyGenerator.Models
{
    public class StoryRecord
    {
        public long Id { get; set; }
        public string GenerationId { get; set; } = string.Empty;
        public string MemoryKey { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string Story { get; set; } = string.Empty;
        public int CharCount { get; set; }
        public string Model { get; set; } = string.Empty;
        public string Agent { get; set; } = string.Empty;
        public string Eval { get; set; } = string.Empty;
        public double Score { get; set; }
        public bool Approved { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Folder { get; set; }

        // Test information (if story was generated from a test)
        public int? TestRunId { get; set; }
        public int? TestStepId { get; set; }

        // Evaluations attached to the story (one for each saved evaluation)
        public List<StoryEvaluation> Evaluations { get; set; } = new List<StoryEvaluation>();
        
        // Legacy properties for backward compatibility (mapped to Story field)
        [Obsolete("Use Story property instead")]
        public string StoryA { get => Story; set => Story = value; }
        [Obsolete("Use Model property instead")]
        public string ModelA { get => Model; set => Model = value; }
        [Obsolete("Use Eval property instead")]
        public string EvalA { get => Eval; set => Eval = value; }
        [Obsolete("Use Score property instead")]
        public double ScoreA { get => Score; set => Score = value; }
    }
}
