using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain tool for story evaluation via function-calling.
    /// Converted from StoryEvaluatorSkill (Semantic Kernel).
    /// </summary>
    public class StoryEvaluatorTool : BaseLangChainTool, ITinyTool
    {
        public string? LastResult { get; set; }
        private readonly DatabaseService? _db;

        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastFunctionCalled { get; set; }
        public string? LastFunctionResult { get; set; }
        // When the orchestrator creates/registers this tool, it should set CurrentStoryId
        // so the tool does not require the story_id to be passed as a function parameter.
        public long? CurrentStoryId { get; set; }

        public StoryEvaluatorTool(DatabaseService? db = null, ICustomLogger? logger = null) 
            : base("storyevaluator", "Provides story evaluation functions for models using function-calling.", logger)
        {
            _db = db;
        }

        public override Dictionary<string, object> GetSchema()
        {
            return CreateFunctionSchema(
                Name,
                Description,
                new Dictionary<string, object>
                {
                    {
                        "operation",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "The operation: 'evaluate_single_feature', 'evaluate_full_story', 'describe'" }
                        }
                    },
                    {
                        "score",
                        new Dictionary<string, object>
                        {
                            { "type", "integer" },
                            { "description", "Score (1-10)" }
                        }
                    },
                    {
                        "defects",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Defects description" }
                        }
                    },
                    {
                        "feature",
                        new Dictionary<string, object>
                        {
                            { "type", "string" },
                            { "description", "Feature name being evaluated" }
                        }
                    },
                    // story_id removed — the orchestrator must set CurrentStoryId on the tool instance
                },
                new List<string> { "operation" }
            );
        }

        public override Task<string> ExecuteAsync(string input)
        {
            try
            {
                var request = ParseInput<StoryEvaluatorToolRequest>(input);
                if (request == null)
                    return Task.FromResult(SerializeResult(new { error = "Invalid input format" }));

                CustomLogger?.Log("Info", "StoryEvaluatorTool", $"Executing operation: {request.Operation}");

                var result = request.Operation?.ToLowerInvariant() switch
                {
                    "evaluate_single_feature" => ExecuteEvaluateSingleFeature(request),
                    "evaluate_full_story" => ExecuteEvaluateFullStory(request),
                    "describe" => SerializeResult(new { result = "Available operations: evaluate_single_feature(score, defects, feature) and evaluate_full_story(...all categories...). The orchestrator must set the tool's CurrentStoryId before use." }),
                    _ => SerializeResult(new { error = $"Unknown operation: {request.Operation}" })
                };
                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "StoryEvaluatorTool", $"Error executing operation: {ex.Message}", ex.ToString());
                return Task.FromResult(SerializeResult(new { error = ex.Message }));
            }
        }

        private string ExecuteEvaluateSingleFeature(StoryEvaluatorToolRequest request)
        {
            try
            {
                var finalScore = request.Score ?? request.StructureScore ?? 0;
                var obj = new
                {
                    score = finalScore,
                    defects = request.Defects ?? string.Empty,
                    feature = request.Feature ?? string.Empty
                };

                LastResult = JsonSerializer.Serialize(obj);

                var storyId = CurrentStoryId;
                if (_db != null && storyId.HasValue && storyId > 0)
                {
                    try
                    {
                        var json = LastResult!;
                        var total = finalScore;
                        _db.AddStoryEvaluation(storyId.Value, json, total, ModelId, AgentId);
                    }
                    catch { }
                }

                return LastResult!;
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private string ExecuteEvaluateFullStory(StoryEvaluatorToolRequest request)
        {
            try
            {
                var obj = new
                {
                    narrative_coherence = new { score = request.NarrativeCoherenceScore ?? 0, defects = request.NarrativeCoherenceDefects ?? string.Empty },
                    structure = new { score = request.StructureScore ?? 0, defects = request.StructureDefects ?? string.Empty },
                    characterization = new { score = request.CharacterizationScore ?? 0, defects = request.CharacterizationDefects ?? string.Empty },
                    dialogues = new { score = request.DialoguesScore ?? 0, defects = request.DialoguesDefects ?? string.Empty },
                    pacing = new { score = request.PacingScore ?? 0, defects = request.PacingDefects ?? string.Empty },
                    originality = new { score = request.OriginalityScore ?? 0, defects = request.OriginalityDefects ?? string.Empty },
                    style = new { score = request.StyleScore ?? 0, defects = request.StyleDefects ?? string.Empty },
                    worldbuilding = new { score = request.WorldbuildingScore ?? 0, defects = request.WorldbuildingDefects ?? string.Empty },
                    thematic_coherence = new { score = request.ThematicCoherenceScore ?? 0, defects = request.ThematicCoherenceDefects ?? string.Empty },
                    emotional_impact = new { score = request.EmotionalImpactScore ?? 0, defects = request.EmotionalImpactDefects ?? string.Empty },
                    total = (request.NarrativeCoherenceScore ?? 0) + (request.StructureScore ?? 0) + (request.CharacterizationScore ?? 0) + 
                            (request.DialoguesScore ?? 0) + (request.PacingScore ?? 0) + (request.OriginalityScore ?? 0) + 
                            (request.StyleScore ?? 0) + (request.WorldbuildingScore ?? 0) + (request.ThematicCoherenceScore ?? 0) + 
                            (request.EmotionalImpactScore ?? 0),
                    overall_evaluation = request.OverallEvaluation ?? string.Empty
                };

                LastResult = JsonSerializer.Serialize(obj);

                var storyId = CurrentStoryId;
                if (_db != null && storyId.HasValue && storyId > 0)
                {
                    try
                    {
                        var json = LastResult!;
                        var total = (int)((dynamic)obj).total;
                        _db.AddStoryEvaluation(storyId.Value, json, total, ModelId, AgentId);
                    }
                    catch { }
                }

                return LastResult!;
            }
            catch (Exception ex)
            {
                return SerializeResult(new { error = ex.Message });
            }
        }

        private class StoryEvaluatorToolRequest
        {
            public string? Operation { get; set; }
            public int? Score { get; set; }
            public int? StructureScore { get; set; }
            public string? Defects { get; set; }
            public string? Feature { get; set; }

            // Full story fields
            public int? NarrativeCoherenceScore { get; set; }
            public string? NarrativeCoherenceDefects { get; set; }
            public string? StructureDefects { get; set; }
            public int? CharacterizationScore { get; set; }
            public string? CharacterizationDefects { get; set; }
            public int? DialoguesScore { get; set; }
            public string? DialoguesDefects { get; set; }
            public int? PacingScore { get; set; }
            public string? PacingDefects { get; set; }
            public int? OriginalityScore { get; set; }
            public string? OriginalityDefects { get; set; }
            public int? StyleScore { get; set; }
            public string? StyleDefects { get; set; }
            public int? WorldbuildingScore { get; set; }
            public string? WorldbuildingDefects { get; set; }
            public int? ThematicCoherenceScore { get; set; }
            public string? ThematicCoherenceDefects { get; set; }
            public int? EmotionalImpactScore { get; set; }
            public string? EmotionalImpactDefects { get; set; }
            public string? OverallEvaluation { get; set; }
        }
    }
}
