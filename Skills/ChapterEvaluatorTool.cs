using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// Tool for evaluating a single chapter/step during multi-step story generation.
    /// The chapter text is passed directly in the prompt, not read from file.
    /// Returns score and feedback without persisting to DB.
    /// </summary>
    public class ChapterEvaluatorTool : BaseLangChainTool, ITinyTool
    {
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }
        public string? LastResult { get; set; }

        public ChapterEvaluatorTool(ICustomLogger? logger = null)
            : base("chapter_evaluator", "Evaluates a chapter/step of a story and returns score with feedback", logger)
        {
        }

        public override Dictionary<string, object> GetSchema()
        {
            return CreateFunctionSchema(
                "evaluate_chapter",
                "Evaluate the chapter text provided in the prompt. Return scores (1-10) for each criterion and specific feedback. Call this exactly once when you finish your evaluation.",
                BuildChapterEvaluationProperties(),
                RequiredProperties);
        }

        public override IEnumerable<Dictionary<string, object>> GetFunctionSchemas()
        {
            yield return GetSchema();
        }

        public override IEnumerable<string> FunctionNames
            => new[] { "evaluate_chapter" };

        private static readonly List<string> RequiredProperties = new()
        {
            "narrative_coherence_score",
            "originality_score",
            "emotional_impact_score",
            "style_score"
            // overall_feedback is optional - only needed if score < 6
        };

        private static Dictionary<string, object> BuildChapterEvaluationProperties()
        {
            return new Dictionary<string, object>
            {
                { "narrative_coherence_score", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Score from 1 to 10 for narrative coherence, logical flow, and consistency" },
                        { "minimum", 1 },
                        { "maximum", 10 }
                    }
                },
                { "narrative_coherence_feedback", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "Specific feedback on narrative coherence issues or strengths" }
                    }
                },
                { "originality_score", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Score from 1 to 10 for originality, creativity, and uniqueness" },
                        { "minimum", 1 },
                        { "maximum", 10 }
                    }
                },
                { "originality_feedback", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "Specific feedback on originality aspects" }
                    }
                },
                { "emotional_impact_score", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Score from 1 to 10 for emotional engagement and impact" },
                        { "minimum", 1 },
                        { "maximum", 10 }
                    }
                },
                { "emotional_impact_feedback", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "Specific feedback on emotional aspects" }
                    }
                },
                { "style_score", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Score from 1 to 10 for writing style, prose quality, and language use" },
                        { "minimum", 1 },
                        { "maximum", 10 }
                    }
                },
                { "style_feedback", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "Specific feedback on writing style" }
                    }
                },
                { "overall_feedback", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "Overall evaluation summary with main strengths and areas for improvement. Required only if average score is below 6." }
                    }
                }
            };
        }

        public override Task<string> ExecuteAsync(string jsonInput)
        {
            return EvaluateChapterAsync(jsonInput);
        }

        public override Task<string> ExecuteFunctionAsync(string functionName, string input)
        {
            if (string.Equals(functionName, "evaluate_chapter", StringComparison.OrdinalIgnoreCase))
            {
                return EvaluateChapterAsync(input);
            }
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Unknown function: {functionName}" }));
        }

        private Task<string> EvaluateChapterAsync(string jsonInput)
        {
            try
            {
                var input = JsonSerializer.Deserialize<ChapterEvaluationInput>(jsonInput, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (input == null)
                    return Task.FromResult(JsonSerializer.Serialize(new { error = "Invalid input format" }));

                var validationError = ValidateInput(input);
                if (validationError != null)
                    return Task.FromResult(JsonSerializer.Serialize(new { error = validationError }));

                // Calculate average score
                var scores = new[]
                {
                    input.NarrativeCoherenceScore!.Value,
                    input.OriginalityScore!.Value,
                    input.EmotionalImpactScore!.Value,
                    input.StyleScore!.Value
                };
                var averageScore = scores.Average();

                // Se il punteggio medio è inferiore a 6 e manca overall_feedback, generiamo un messaggio automatico
                var overallFeedback = input.OverallFeedback;
                if (string.IsNullOrWhiteSpace(overallFeedback) && averageScore < 6)
                {
                    overallFeedback = $"Il capitolo ha ottenuto un punteggio medio di {averageScore:F1}/10. " +
                        $"Aree da migliorare: " +
                        (input.NarrativeCoherenceScore < 6 ? $"coerenza narrativa ({input.NarrativeCoherenceScore}/10), " : "") +
                        (input.OriginalityScore < 6 ? $"originalità ({input.OriginalityScore}/10), " : "") +
                        (input.EmotionalImpactScore < 6 ? $"impatto emotivo ({input.EmotionalImpactScore}/10), " : "") +
                        (input.StyleScore < 6 ? $"stile ({input.StyleScore}/10)" : "");
                    overallFeedback = overallFeedback.TrimEnd(' ', ',');
                }

                var result = new ChapterEvaluationResult
                {
                    NarrativeCoherenceScore = input.NarrativeCoherenceScore.Value,
                    NarrativeCoherenceFeedback = input.NarrativeCoherenceFeedback ?? string.Empty,
                    OriginalityScore = input.OriginalityScore.Value,
                    OriginalityFeedback = input.OriginalityFeedback ?? string.Empty,
                    EmotionalImpactScore = input.EmotionalImpactScore.Value,
                    EmotionalImpactFeedback = input.EmotionalImpactFeedback ?? string.Empty,
                    StyleScore = input.StyleScore.Value,
                    StyleFeedback = input.StyleFeedback ?? string.Empty,
                    OverallFeedback = overallFeedback ?? string.Empty,
                    AverageScore = averageScore,
                    EvaluatorAgentId = AgentId,
                    EvaluatorModelName = ModelName
                };

                LastResult = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
                LastFunctionCalled = "evaluate_chapter";
                LastFunctionResult = LastResult;

                CustomLogger?.Log("Info", "ChapterEvaluatorTool", 
                    $"Chapter evaluation completed - Average: {averageScore:F2}, Agent: {AgentId}, Model: {ModelName}");

                return Task.FromResult(LastResult);
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "ChapterEvaluatorTool", $"Evaluation failed: {ex.Message}", ex.ToString());
                return Task.FromResult(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }

        private static string? ValidateInput(ChapterEvaluationInput input)
        {
            if (!input.NarrativeCoherenceScore.HasValue)
                return "narrative_coherence_score is required";
            if (input.NarrativeCoherenceScore < 1 || input.NarrativeCoherenceScore > 10)
                return "narrative_coherence_score must be between 1 and 10";

            if (!input.OriginalityScore.HasValue)
                return "originality_score is required";
            if (input.OriginalityScore < 1 || input.OriginalityScore > 10)
                return "originality_score must be between 1 and 10";

            if (!input.EmotionalImpactScore.HasValue)
                return "emotional_impact_score is required";
            if (input.EmotionalImpactScore < 1 || input.EmotionalImpactScore > 10)
                return "emotional_impact_score must be between 1 and 10";

            if (!input.StyleScore.HasValue)
                return "style_score is required";
            if (input.StyleScore < 1 || input.StyleScore > 10)
                return "style_score must be between 1 and 10";

            // overall_feedback is only required if average score is below 6
            // We'll check this after calculating the average, so skip validation here

            return null;
        }

        private class ChapterEvaluationInput
        {
            [JsonPropertyName("narrative_coherence_score")]
            public int? NarrativeCoherenceScore { get; set; }

            [JsonPropertyName("narrative_coherence_feedback")]
            public string? NarrativeCoherenceFeedback { get; set; }

            [JsonPropertyName("originality_score")]
            public int? OriginalityScore { get; set; }

            [JsonPropertyName("originality_feedback")]
            public string? OriginalityFeedback { get; set; }

            [JsonPropertyName("emotional_impact_score")]
            public int? EmotionalImpactScore { get; set; }

            [JsonPropertyName("emotional_impact_feedback")]
            public string? EmotionalImpactFeedback { get; set; }

            [JsonPropertyName("style_score")]
            public int? StyleScore { get; set; }

            [JsonPropertyName("style_feedback")]
            public string? StyleFeedback { get; set; }

            [JsonPropertyName("overall_feedback")]
            public string? OverallFeedback { get; set; }
        }
    }

    /// <summary>
    /// Result of a chapter evaluation, returned by ChapterEvaluatorTool.
    /// </summary>
    public class ChapterEvaluationResult
    {
        [JsonPropertyName("narrative_coherence_score")]
        public int NarrativeCoherenceScore { get; set; }

        [JsonPropertyName("narrative_coherence_feedback")]
        public string NarrativeCoherenceFeedback { get; set; } = string.Empty;

        [JsonPropertyName("originality_score")]
        public int OriginalityScore { get; set; }

        [JsonPropertyName("originality_feedback")]
        public string OriginalityFeedback { get; set; } = string.Empty;

        [JsonPropertyName("emotional_impact_score")]
        public int EmotionalImpactScore { get; set; }

        [JsonPropertyName("emotional_impact_feedback")]
        public string EmotionalImpactFeedback { get; set; } = string.Empty;

        [JsonPropertyName("style_score")]
        public int StyleScore { get; set; }

        [JsonPropertyName("style_feedback")]
        public string StyleFeedback { get; set; } = string.Empty;

        [JsonPropertyName("overall_feedback")]
        public string OverallFeedback { get; set; } = string.Empty;

        [JsonPropertyName("average_score")]
        public double AverageScore { get; set; }

        [JsonPropertyName("evaluator_agent_id")]
        public int? EvaluatorAgentId { get; set; }

        [JsonPropertyName("evaluator_model_name")]
        public string? EvaluatorModelName { get; set; }
    }
}
