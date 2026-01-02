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
        private List<string>? _chunksCache;

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

        private readonly int _storyChunkSize = 2000;

        private Task<string> EvaluateFullStoryAsync(string jsonInput)
        {
            try
            {
                EnsureStoryChunks();

                var input = JsonSerializer.Deserialize<EvaluateFullStoryInput>(jsonInput, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (input == null)
                    return Task.FromResult(JsonSerializer.Serialize(new { error = "Invalid input format" }));

                var validationError = ValidateInput(input);
                if (validationError != null)
                    return Task.FromResult(JsonSerializer.Serialize(new { error = validationError }));

                if (!CurrentStoryId.HasValue || CurrentStoryId <= 0)
                    return Task.FromResult(JsonSerializer.Serialize(new { error = "Internal error: story id not provided" }));

                var narrative = input.NarrativeCoherenceScore!.Value;
                var originality = input.OriginalityScore!.Value;
                var emotional = input.EmotionalImpactScore!.Value;
                var action = input.ActionScore!.Value;

                var payloadForStorage = new
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

                var payloadForModel = new
                {
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

                LastResult = JsonSerializer.Serialize(payloadForModel);
                LastFunctionCalled = "evaluate_full_story";
                LastFunctionResult = LastResult;

                if (_database != null && CurrentStoryId > 0)
                {
                    try
                    {
                        var storageJson = JsonSerializer.Serialize(payloadForStorage);
                        _database.AddStoryEvaluation(CurrentStoryId.Value, storageJson, totalScore, ModelId, AgentId);
                    }
                    catch (Exception dbEx)
                    {
                        CustomLogger?.Log("Warn", "EvaluatorTool", $"Failed to persist evaluation: {dbEx.Message}");
                    }
                }

                CustomLogger?.Log("Info", "EvaluatorTool", $"Full story evaluation stored for story {CurrentStoryId} (score {totalScore:F2})");
                return Task.FromResult(LastResult ?? "{}");
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "EvaluatorTool", $"Execution failed: {ex.Message}", ex.ToString());
                return Task.FromResult(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }

        private Task<string> ReadStoryPartAsync(string jsonInput)
        {
            try
            {
                EnsureStoryChunks();

                var input = JsonSerializer.Deserialize<ReadStoryPartInput>(jsonInput, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (input == null)
                    return Task.FromResult(JsonSerializer.Serialize(new { error = "Invalid input format" }));

                if (input.PartIndex < 0)
                {
                    return Task.FromResult(JsonSerializer.Serialize(new { error = "part_index must be non-negative" }));
                }

                if (!CurrentStoryId.HasValue || CurrentStoryId.Value <= 0)
                    return Task.FromResult(JsonSerializer.Serialize(new { error = "CurrentStoryId not set" }));

                // register requested part for loop detection
                try { _requestedParts.Add(input.PartIndex); } catch { }

                var chunks = _chunksCache ?? new List<string>();
                if (input.PartIndex >= chunks.Count)
                {
                    return Task.FromResult(JsonSerializer.Serialize(new { error = "part_index out of range" }));
                }

                var chunk = chunks[input.PartIndex];
                var payload = new
                {
                    part_index = input.PartIndex,
                    text = chunk,
                    is_last = input.PartIndex >= chunks.Count - 1
                };

                // Diagnostic logging: report that a story part was read and its length
                try
                {
                    var isLast = input.PartIndex >= chunks.Count - 1;
                    CustomLogger?.Log("Info", "EvaluatorTool", $"ReadStoryPart part={input.PartIndex} len={chunk.Length} is_last={isLast}");
                }
                catch { }

                return Task.FromResult(JsonSerializer.Serialize(payload));
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "EvaluatorTool", $"ReadStoryPart failed: {ex.Message}", ex.ToString());
                return Task.FromResult(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }

        public void ResetRequestedParts()
        {
            _requestedParts.Clear();
        }

        public bool HasRequestedAllParts()
        {
            EnsureStoryChunks();
            var totalParts = _chunksCache?.Count ?? 0;
            if (totalParts == 0) return false;
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
            "narrative_coherence_defects",
            "originality_score",
            "originality_defects",
            "emotional_impact_score",
            "emotional_impact_defects",
            "action_score",
            "action_defects"
        };

        private void EnsureStoryChunks()
        {
            if (_chunksCache != null) return;
            if (!CurrentStoryId.HasValue || _database == null) return;
            var s = _database.GetStoryById(CurrentStoryId.Value);
            var storyText = !string.IsNullOrWhiteSpace(s?.StoryRevised)
                ? s!.StoryRevised!
                : (s?.StoryRaw ?? string.Empty);
            _chunksCache = StoryChunkHelper.SplitIntoChunks(storyText, _storyChunkSize);
        }

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
            [JsonPropertyName("part_index")]
            public int PartIndex { get; set; }
        }
    }
}
