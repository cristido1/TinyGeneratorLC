namespace TinyGenerator.Models
{
    /// <summary>
    /// Unified story generation payload used by LangChain pipelines and persistence.
    /// Mirrors the historical StoryGeneratorService.GenerationResult structure so
    /// DatabaseService/StoriesService can stay agnostic to the generating engine.
    /// </summary>
    public class StoryGenerationResult
    {
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

        /// <summary>
        /// Contains the approved story text when generation meets the acceptance threshold.
        /// </summary>
        public string? Approved { get; set; }

        public string Message { get; set; } = string.Empty;
        public bool Success { get; set; }
    }
}
