using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("model_test_runs")]
public class ModelTestRun
{
    [Column("id")]
    public int Id { get; set; }
    [Column("model_id")]
    public int ModelId { get; set; }
    [Column("test_group")]
    public string? TestGroup { get; set; }
    [Column("passed")]
    public bool Passed { get; set; }
    [Column("duration_ms")]
    public long? DurationMs { get; set; }
    [Column("run_date")]
    public string? RunDate { get; set; }
    [Column("description")]
    public string? Description { get; set; }
    [Column("notes")]
    public string? Notes { get; set; }
    [Column("test_folder")]
    public string? TestFolder { get; set; }
}
