namespace TinyGenerator.Models
{
    /// <summary>
    /// LEGACY: Story generation payload with fixed A/B/C structure.
    /// Used only by legacy test pages (LangChainTest.cshtml).
    /// For production, use FullStoryPipelineCommand which dynamically handles
    /// all active writer agents and stores stories via TaskExecution/MultiStepOrchestrationService.
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
