namespace TinyGenerator.Models
{
    /// <summary>
    /// Simple plan with sequential steps (stub for removed HandlebarsPlanner)
    /// </summary>
    public class Plan
    {
        public string? Goal { get; set; }
        public List<PlanStep> Steps { get; set; } = new List<PlanStep>();
    }

    public class PlanStep
    {
        public string Description { get; set; } = string.Empty;
    }
}
