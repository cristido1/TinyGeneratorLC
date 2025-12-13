using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("model_test_steps")]
public class ModelTestStep
{
    [Column("id")]
    public int Id { get; set; }
    [Column("run_id")]
    public int RunId { get; set; }
    [Column("step_number")]
    public int StepNumber { get; set; }
    [Column("step_name")]
    public string? StepName { get; set; }
    [Column("input_json")]
    public string? InputJson { get; set; }
    [Column("output_json")]
    public string? OutputJson { get; set; }
    [Column("passed")]
    public bool Passed { get; set; }
    [Column("error")]
    public string? Error { get; set; }
    [Column("duration_ms")]
    public long? DurationMs { get; set; }
}
