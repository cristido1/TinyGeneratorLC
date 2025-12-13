using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("model_test_assets")]
public class ModelTestAsset
{
    [Column("id")]
    public int Id { get; set; }
    [Column("step_id")]
    public int StepId { get; set; }
    [Column("file_type")]
    public string? FileType { get; set; }
    [Column("file_path")]
    public string? FilePath { get; set; }
    [Column("description")]
    public string? Description { get; set; }
    [Column("duration_sec")]
    public double? DurationSec { get; set; }
    [Column("size_bytes")]
    public long? SizeBytes { get; set; }
    [Column("story_id")]
    public long? StoryId { get; set; }
}
