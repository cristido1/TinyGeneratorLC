using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;

namespace TinyGenerator.Skills
{
    public class ResponseCheckerTool
    {
        private readonly Services.ResponseCheckerService _checkerService;

        public ResponseCheckerTool(Services.ResponseCheckerService checkerService)
        {
            _checkerService = checkerService;
        }

        [KernelFunction("validate_step_output")]
        [Description("Validates if a writer's output meets the step requirements. Returns validation result with is_valid, reason, and needs_retry fields.")]
        public async Task<string> ValidateStepOutputAsync(
            [Description("The step instruction that was given to the writer")] string stepInstruction,
            [Description("The output produced by the writer")] string writerOutput,
            [Description("Optional validation criteria as JSON string")] string? validationCriteria = null)
        {
            var criteria = string.IsNullOrWhiteSpace(validationCriteria) 
                ? null 
                : System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(validationCriteria);

            var result = await _checkerService.ValidateStepOutputAsync(
                stepInstruction,
                writerOutput,
                criteria,
                threadId: 0, // Tool invocations don't have thread context
                agentName: null,
                modelName: null
            );

            return System.Text.Json.JsonSerializer.Serialize(result);
        }

        [KernelFunction("check_semantic_alignment")]
        [Description("Calculates semantic similarity between two texts using embeddings. Returns a score between 0 and 1, where 1 means semantically identical.")]
        public async Task<string> CheckSemanticAlignmentAsync(
            [Description("First text to compare")] string text1,
            [Description("Second text to compare")] string text2)
        {
            var score = await _checkerService.CalculateSemanticAlignmentAsync(text1, text2);

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                semantic_score = score,
                interpretation = score switch
                {
                    null => "Could not calculate (embedding model unavailable)",
                    >= 0.8 => "Very high semantic alignment",
                    >= 0.6 => "Good semantic alignment",
                    >= 0.4 => "Moderate semantic alignment",
                    _ => "Low semantic alignment"
                }
            });
        }
    }
}
