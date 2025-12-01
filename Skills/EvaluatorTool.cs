using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain version of StoryEvaluatorSkill exposing a single evaluate_full_story function.
    /// </summary>
    public class EvaluatorTool : BaseLangChainTool, ITinyTool
    {
        private readonly DatabaseService? _database;
        // Track which part indexes have been requested during evaluation
        private readonly HashSet<int> _requestedParts = new();
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }
        public string? LastResult { get; set; }
        public long? CurrentStoryId { get; set; }

        public IReadOnlyCollection<int> RequestedParts => _requestedParts;

        public EvaluatorTool(DatabaseService? database = null, ICustomLogger? logger = null) 
            : base("evaluate_full_story", "Complete story evaluation for a single story", logger)
        {
            _database = database;
        }

        public override Dictionary<string, object> GetSchema()
        {
            return CreateFunctionSchema(
                "evaluate_full_story",
                "Provide the complete evaluation for a story. Call this exactly once when you finish your review.",
                BuildFullStoryProperties(),
                RequiredProperties);
        }

        public override IEnumerable<Dictionary<string, object>> GetFunctionSchemas()
        {
            yield return GetSchema();
            yield return CreateFunctionSchema(
                "read_story_part",
                "Reads a segment of the story for evaluation.",
                new Dictionary<string, object>
                {
                    { "part_index", new Dictionary<string, object> { { "type", "integer" }, { "description", "0-based segment index" } } }
                },
                new List<string> { "part_index" });
        }

        public override IEnumerable<string> FunctionNames
            => new[] { "evaluate_full_story", "read_story_part" };

        public override Task<string> ExecuteAsync(string jsonInput)
        {
            return EvaluateFullStoryAsync(jsonInput);
        }

        public override Task<string> ExecuteFunctionAsync(string functionName, string input)
        {
            if (string.Equals(functionName, "evaluate_full_story", StringComparison.OrdinalIgnoreCase))
            {
                return EvaluateFullStoryAsync(input);
            }
            if (string.Equals(functionName, "read_story_part", StringComparison.OrdinalIgnoreCase))
            {
                return ReadStoryPartAsync(input);
            }
            return Task.FromResult(JsonSerializer.Serialize(new { error = $"Unknown function: {functionName}" }));
        }

        private readonly int _storyChunkSize = 1500;

        private async Task<string> EvaluateFullStoryAsync(string jsonInput)
        {
            try
            {
                var input = JsonSerializer.Deserialize<EvaluateFullStoryInput>(jsonInput, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (input == null)
                    return JsonSerializer.Serialize(new { error = "Invalid input format" });

                var validationError = ValidateInput(input);
                if (validationError != null)
                    return JsonSerializer.Serialize(new { error = validationError });

                if (!CurrentStoryId.HasValue || CurrentStoryId <= 0)
                    return JsonSerializer.Serialize(new { error = "Internal error: story id not provided" });

                var narrative = input.NarrativeCoherenceScore!.Value;
                var originality = input.OriginalityScore!.Value;
                var emotional = input.EmotionalImpactScore!.Value;
                var action = input.ActionScore!.Value;

                var payload = new
                {
                    story_id = CurrentStoryId,
                    narrative_coherence_score = narrative,
                    narrative_coherence_defects = input.NarrativeCoherenceDefects,
                    originality_score = originality,
                    originality_defects = input.OriginalityDefects,
                    emotional_impact_score = emotional,
                    emotional_impact_defects = input.EmotionalImpactDefects,
                    action_score = action,
                    action_defects = input.ActionDefects
                };

                var scores = new[]
                {
                    narrative,
                    originality,
                    emotional,
                    action
                };
                var totalScore = (double)scores.Sum();

                LastResult = JsonSerializer.Serialize(payload);
                LastFunctionCalled = "evaluate_full_story";
                LastFunctionResult = LastResult;

                if (_database != null && CurrentStoryId > 0)
                {
                    try
                    {
                        _database.AddStoryEvaluation(CurrentStoryId.Value, LastResult, totalScore, ModelId, AgentId);
                    }
                    catch (Exception dbEx)
                    {
                        CustomLogger?.Log("Warn", "EvaluatorTool", $"Failed to persist evaluation: {dbEx.Message}");
                    }
                }

                CustomLogger?.Log("Info", "EvaluatorTool", $"Full story evaluation stored for story {CurrentStoryId} (score {totalScore:F2})");
                return await Task.FromResult(LastResult ?? "{}");
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "EvaluatorTool", $"Execution failed: {ex.Message}", ex.ToString());
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        private async Task<string> ReadStoryPartAsync(string jsonInput)
        {
            try
            {
                var input = JsonSerializer.Deserialize<ReadStoryPartInput>(jsonInput, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (input == null)
                    return JsonSerializer.Serialize(new { error = "Invalid input format" });

                if (input.PartIndex < 0)
                {
                    return JsonSerializer.Serialize(new { error = "part_index must be non-negative" });
                }

                if (!CurrentStoryId.HasValue || CurrentStoryId.Value <= 0)
                    return JsonSerializer.Serialize(new { error = "CurrentStoryId not set" });

                // register requested part for loop detection
                try { _requestedParts.Add(input.PartIndex); } catch { }

                var story = _database?.GetStoryById(CurrentStoryId.Value);
                
                if (story == null || string.IsNullOrEmpty(story.Story))
                {
                    return JsonSerializer.Serialize(new { error = "Story not found or empty" });
                }

                var text = story.Story;
                var start = input.PartIndex * _storyChunkSize;
                if (start >= text.Length)
                {
                    return JsonSerializer.Serialize(new { error = "part_index out of range" });
                }

                var end = Math.Min(text.Length, start + _storyChunkSize);
                var chunk = text[start..end];
                var payload = new
                {
                    part_index = input.PartIndex,
                    text = chunk,
                    is_last = end >= text.Length
                };

                // Diagnostic logging: report that a story part was read and its length
                try
                {
                    var isLast = end >= text.Length;
                    CustomLogger?.Log("Info", "EvaluatorTool", $"ReadStoryPart part={input.PartIndex} len={chunk.Length} is_last={isLast}");
                }
                catch { }

                return JsonSerializer.Serialize(payload);
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "EvaluatorTool", $"ReadStoryPart failed: {ex.Message}", ex.ToString());
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        public void ResetRequestedParts()
        {
            _requestedParts.Clear();
        }

        public bool HasRequestedAllParts()
        {
            string storyText = string.Empty;
            if (CurrentStoryId.HasValue && _database != null)
            {
                var s = _database.GetStoryById(CurrentStoryId.Value);
                storyText = s?.Story ?? string.Empty;
            }
            if (string.IsNullOrEmpty(storyText))
                return false;
            var totalParts = Math.Max(1, (int)Math.Ceiling((double)storyText.Length / _storyChunkSize));
            return _requestedParts.Count >= totalParts;
        }

        private static string? ValidateInput(EvaluateFullStoryInput input)
        {
            var missing = new List<string>();
            bool InRange(int? value) => value.HasValue && value.Value >= 0 && value.Value <= 10;
            void RequireScore(int? value, string name)
            {
                if (!InRange(value)) missing.Add(name);
            }

            RequireScore(input.NarrativeCoherenceScore, "narrative_coherence_score");
            RequireScore(input.OriginalityScore, "originality_score");
            RequireScore(input.EmotionalImpactScore, "emotional_impact_score");
            RequireScore(input.ActionScore, "action_score");

            input.NarrativeCoherenceDefects ??= string.Empty;
            input.OriginalityDefects ??= string.Empty;
            input.EmotionalImpactDefects ??= string.Empty;
            input.ActionDefects ??= string.Empty;

            if (missing.Count > 0)
            {
                return $"Missing or invalid fields: {string.Join(", ", missing)}";
            }

            return null;
        }

        private static Dictionary<string, object> BuildFullStoryProperties()
        {
            Dictionary<string, object> CreateScoreProperty(string description) => new Dictionary<string, object>
            {
                { "type", "integer" },
                { "description", description }
            };

            Dictionary<string, object> CreateDefectProperty(string description) => new Dictionary<string, object>
            {
                { "type", "string" },
                { "description", description }
            };

            return new Dictionary<string, object>
            {
                { "narrative_coherence_score", CreateScoreProperty("Score for narrative coherence (0-10)") },
                { "narrative_coherence_defects", CreateDefectProperty("Defects found in narrative coherence") },
                { "originality_score", CreateScoreProperty("Score for originality/inventiveness (0-10)") },
                { "originality_defects", CreateDefectProperty("Defects found in originality") },
                { "emotional_impact_score", CreateScoreProperty("Score for emotional impact (0-10)") },
                { "emotional_impact_defects", CreateDefectProperty("Defects found in emotional impact") },
                { "action_score", CreateScoreProperty("Score for pacing/action intensity (0-10)") },
                { "action_defects", CreateDefectProperty("Defects found in action/pacing") }
            };
        }

        private static readonly List<string> RequiredProperties = new()
        {
            "narrative_coherence_score",
            "originality_score",
            "emotional_impact_score",
            "action_score"
        };

        public class EvaluateFullStoryInput
        {
            [JsonPropertyName("narrative_coherence_score")]
            public int? NarrativeCoherenceScore { get; set; }

            [JsonPropertyName("narrative_coherence_defects")]
            public string? NarrativeCoherenceDefects { get; set; }

            [JsonPropertyName("originality_score")]
            public int? OriginalityScore { get; set; }

            [JsonPropertyName("originality_defects")]
            public string? OriginalityDefects { get; set; }

            [JsonPropertyName("emotional_impact_score")]
            public int? EmotionalImpactScore { get; set; }

            [JsonPropertyName("emotional_impact_defects")]
            public string? EmotionalImpactDefects { get; set; }

            [JsonPropertyName("action_score")]
            public int? ActionScore { get; set; }

            [JsonPropertyName("action_defects")]
            public string? ActionDefects { get; set; }

            public bool AllScoresProvided()
            {
                bool InRange(int? value) => value.HasValue && value.Value >= 0 && value.Value <= 10;

                return InRange(NarrativeCoherenceScore)
                    && InRange(OriginalityScore)
                    && InRange(EmotionalImpactScore)
                    && InRange(ActionScore);
            }

            public bool AllDefectsProvided()
            {
                return true;
            }
        }

        public class ReadStoryPartInput
        {
            [JsonPropertyName("story_id")]
            public long StoryId { get; set; }

            [JsonPropertyName("part_index")]
            public int PartIndex { get; set; }
        }
    }
}
