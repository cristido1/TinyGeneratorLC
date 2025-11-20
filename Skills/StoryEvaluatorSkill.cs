using Microsoft.SemanticKernel;
using System.ComponentModel;
using System.Text.Json;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    [Description("Provides story evaluation functions intended for models using function-calling. The model should call these functions with the evaluation parameters; the functions return the provided parameters as JSON.")]
    public class StoryEvaluatorSkill
    {
        public string? LastCalled { get; set; }
        public string? LastResult { get; set; }
        private readonly DatabaseService? _db;
        private readonly long? _modelId;
        private readonly int? _agentId;

        public StoryEvaluatorSkill()
        {
        }
        public StoryEvaluatorSkill(DatabaseService db, long? modelId = null, int? agentId = null)
        {
            _db = db;
            _modelId = modelId;
            _agentId = agentId;
        }

        // Function expected to be invoked by the model via function-calling.
        // The model must supply 'score' (1-10) and 'defects' (string). Optionally the caller may include 'feature' to identify which feature was evaluated.
        [KernelFunction("evaluate_single_feature"), Description("Records a single feature evaluation. Parameters: score (int), defects (string), feature (optional string). Accepts alternate score fields (e.g., 'structure_score').")]
        public string EvaluateSingleFeature(int? score = null, int? structure_score = null, string defects = "", string? feature = null, long story_id = 0)
        {
            LastCalled = nameof(EvaluateSingleFeature);
            var finalScore = score ?? structure_score ?? 0;
            var obj = new
            {
                score = finalScore,
                defects = defects ?? string.Empty,
                feature = feature ?? string.Empty
            };

            // Persist the serialized result so external callers (test runner) can inspect the last evaluation
            LastResult = JsonSerializer.Serialize(obj);
            // Return the accepted parameters as a compact JSON string so callers (and tests) can inspect them.
            if (_db != null && story_id > 0)
            {
                try
                {
                    // store as a quick evaluation row — wrap a minimal evaluator object
                    var json = LastResult!;
                    var total = finalScore; // single feature total is the value
                    // Store evaluation using model_id / agent_id for scoping; embed any feature info inside raw JSON.
                    _db.AddStoryEvaluation(story_id, json, total, _modelId, _agentId);
                }
                catch { }
            }
            return LastResult!;
        }

        // Function expected to be invoked by the model via function-calling to provide a full-story evaluation.
        // All parameter names are in English to match the function-calling contract.
        [KernelFunction("evaluate_full_story"), Description("Records a full story evaluation across all categories. The function returns the provided parameters as JSON.")]
        public string EvaluateFullStory(
            int narrative_coherence_score = 0, string narrative_coherence_defects = "",
            int structure_score = 0, string structure_defects = "",
            int characterization_score = 0, string characterization_defects = "",
            int dialogues_score = 0, string dialogues_defects = "",
            int pacing_score = 0, string pacing_defects = "",
            int originality_score = 0, string originality_defects = "",
            int style_score = 0, string style_defects = "",
            int worldbuilding_score = 0, string worldbuilding_defects = "",
            int thematic_coherence_score = 0, string thematic_coherence_defects = "",
            int emotional_impact_score = 0, string emotional_impact_defects = "",
            string overall_evaluation = "", long story_id = 0)
        {
            LastCalled = nameof(EvaluateFullStory);

            var obj = new
            {
                narrative_coherence = new { score = narrative_coherence_score, defects = narrative_coherence_defects ?? string.Empty },
                structure = new { score = structure_score, defects = structure_defects ?? string.Empty },
                characterization = new { score = characterization_score, defects = characterization_defects ?? string.Empty },
                dialogues = new { score = dialogues_score, defects = dialogues_defects ?? string.Empty },
                pacing = new { score = pacing_score, defects = pacing_defects ?? string.Empty },
                originality = new { score = originality_score, defects = originality_defects ?? string.Empty },
                style = new { score = style_score, defects = style_defects ?? string.Empty },
                worldbuilding = new { score = worldbuilding_score, defects = worldbuilding_defects ?? string.Empty },
                thematic_coherence = new { score = thematic_coherence_score, defects = thematic_coherence_defects ?? string.Empty },
                emotional_impact = new { score = emotional_impact_score, defects = emotional_impact_defects ?? string.Empty },
                total = narrative_coherence_score + structure_score + characterization_score + dialogues_score + pacing_score + originality_score + style_score + worldbuilding_score + thematic_coherence_score + emotional_impact_score,
                overall_evaluation = overall_evaluation ?? string.Empty
            };

            LastResult = JsonSerializer.Serialize(obj);
            if (_db != null && story_id > 0)
            {
                try
                {
                    // store parsed details in DB as story evaluation
                    var json = LastResult!;
                    var total = obj.total;
                    _db.AddStoryEvaluation(story_id, json, total, _modelId, _agentId);
                }
                catch { }
            }
            return LastResult!;
        }
    }
}
