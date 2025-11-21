using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    /// <summary>
    /// LangChain version of StoryEvaluatorSkill.
    /// Provides story evaluation functions for models using function-calling.
    /// </summary>
    public class EvaluatorTool : BaseLangChainTool, ILangChainToolWithContext
    {
        private readonly DatabaseService? _database;
        public int? ModelId { get; set; }
        public string? ModelName { get; set; }
        public int? AgentId { get; set; }
        public string? LastResult { get; set; }

        public EvaluatorTool(DatabaseService? database = null, ICustomLogger? logger = null) 
            : base("evaluator", "Story evaluation functions: evaluate_single_feature, evaluate_full_story", logger)
        {
            _database = database;
        }

        public override Dictionary<string, object> GetSchema()
        {
            var properties = new Dictionary<string, object>
            {
                { "function", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "enum", new List<string> { "evaluate_single_feature", "evaluate_full_story" } },
                        { "description", "The evaluation function to call" }
                    }
                },
                { "score", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Score 1-10 for evaluation" }
                    }
                },
                { "defects", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "Description of defects or issues found" }
                    }
                },
                { "feature", new Dictionary<string, object>
                    {
                        { "type", "string" },
                        { "description", "The feature being evaluated (optional)" }
                    }
                },
                { "narrative_coherence_score", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Score for narrative coherence" }
                    }
                },
                { "structure_score", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Score for story structure" }
                    }
                },
                { "characterization_score", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Score for character development" }
                    }
                },
                { "dialogues_score", new Dictionary<string, object>
                    {
                        { "type", "integer" },
                        { "description", "Score for dialogue quality" }
                    }
                }
            };

            return CreateFunctionSchema("evaluator", "Story evaluation functions", properties, new List<string> { "function" });
        }

        public override async Task<string> ExecuteAsync(string jsonInput)
        {
            try
            {
                var input = JsonSerializer.Deserialize<EvaluatorToolInput>(jsonInput, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });

                if (input == null)
                    return JsonSerializer.Serialize(new { error = "Invalid input format" });

                var function = input.Function?.ToLower() ?? "";

                string result;

                if (function == "evaluate_single_feature")
                {
                    var score = input.Score ?? input.StructureScore ?? 0;
                    var obj = new
                    {
                        score = score,
                        defects = input.Defects ?? string.Empty,
                        feature = input.Feature ?? string.Empty
                    };

                    LastResult = JsonSerializer.Serialize(obj);

                    // Persist to database if available
                    if (_database != null && input.StoryId > 0)
                    {
                        try
                        {
                            _database.AddStoryEvaluation(input.StoryId, LastResult, score, ModelId, AgentId);
                        }
                        catch { }
                    }

                    result = LastResult;
                    CustomLogger?.Log("Info", "EvaluatorTool", $"Single feature evaluation: {result}");
                }
                else if (function == "evaluate_full_story")
                {
                    var obj = new
                    {
                        narrative_coherence_score = input.NarrativeCoherenceScore ?? 0,
                        narrative_coherence_defects = input.NarrativeCoherenceDefects ?? string.Empty,
                        structure_score = input.StructureScore ?? 0,
                        structure_defects = input.StructureDefects ?? string.Empty,
                        characterization_score = input.CharacterizationScore ?? 0,
                        characterization_defects = input.CharacterizationDefects ?? string.Empty,
                        dialogues_score = input.DialoguesScore ?? 0,
                        dialogues_defects = input.DialoguesDefects ?? string.Empty,
                        pacing_score = input.PacingScore ?? 0,
                        pacing_defects = input.PacingDefects ?? string.Empty
                    };

                    LastResult = JsonSerializer.Serialize(obj);

                    // Calculate total score
                    var totalScore = (
                        (input.NarrativeCoherenceScore ?? 0) +
                        (input.StructureScore ?? 0) +
                        (input.CharacterizationScore ?? 0) +
                        (input.DialoguesScore ?? 0) +
                        (input.PacingScore ?? 0)
                    ) / 5.0;

                    // Persist to database if available
                    if (_database != null && input.StoryId > 0)
                    {
                        try
                        {
                            _database.AddStoryEvaluation(input.StoryId, LastResult, (int)totalScore, ModelId, AgentId);
                        }
                        catch { }
                    }

                    result = LastResult;
                    CustomLogger?.Log("Info", "EvaluatorTool", $"Full story evaluation: total_score={totalScore}");
                }
                else
                    return JsonSerializer.Serialize(new { error = $"Unknown function: {function}" });

                return await Task.FromResult(result);
            }
            catch (Exception ex)
            {
                CustomLogger?.Log("Error", "EvaluatorTool", $"Execution failed: {ex.Message}", ex.ToString());
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }

    public class EvaluatorToolInput
    {
        [JsonPropertyName("function")]
        public string? Function { get; set; }

        [JsonPropertyName("score")]
        public int? Score { get; set; }

        [JsonPropertyName("structure_score")]
        public int? StructureScore { get; set; }

        [JsonPropertyName("defects")]
        public string? Defects { get; set; }

        [JsonPropertyName("feature")]
        public string? Feature { get; set; }

        [JsonPropertyName("story_id")]
        public long StoryId { get; set; }

        // Full story evaluation fields
        [JsonPropertyName("narrative_coherence_score")]
        public int? NarrativeCoherenceScore { get; set; }

        [JsonPropertyName("narrative_coherence_defects")]
        public string? NarrativeCoherenceDefects { get; set; }

        [JsonPropertyName("structure_defects")]
        public string? StructureDefects { get; set; }

        [JsonPropertyName("characterization_score")]
        public int? CharacterizationScore { get; set; }

        [JsonPropertyName("characterization_defects")]
        public string? CharacterizationDefects { get; set; }

        [JsonPropertyName("dialogues_score")]
        public int? DialoguesScore { get; set; }

        [JsonPropertyName("dialogues_defects")]
        public string? DialoguesDefects { get; set; }

        [JsonPropertyName("pacing_score")]
        public int? PacingScore { get; set; }

        [JsonPropertyName("pacing_defects")]
        public string? PacingDefects { get; set; }
    }
}
