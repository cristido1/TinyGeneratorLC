using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("model_roles")]
public class ModelRole
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("model_id")]
    public int ModelId { get; set; }

    [Required]
    [Column("role_id")]
    public int RoleId { get; set; }

    [Column("is_primary")]
    public bool IsPrimary { get; set; } = false;

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    [Column("use_count")]
    public int UseCount { get; set; } = 0;

    [Column("use_successed")]
    public int UseSuccessed { get; set; } = 0;

    [Column("use_failed")]
    public int UseFailed { get; set; } = 0;

    [Column("last_use")]
    public string? LastUse { get; set; }

    [Column("instructions")]
    public string? Instructions { get; set; }

    [Column("top_p")]
    public double? TopP { get; set; }

    [Column("top_k")]
    public int? TopK { get; set; }

    [Column("created_at")]
    public string? CreatedAt { get; set; }

    [Column("updated_at")]
    public string? UpdatedAt { get; set; }

    // Cumulative token counters
    [Column("total_prompt_tokens")]
    public long TotalPromptTokens { get; set; } = 0;

    [Column("total_output_tokens")]
    public long TotalOutputTokens { get; set; } = 0;

    // Cumulative timings (nanoseconds)
    [Column("total_prompt_time_ns")]
    public long TotalPromptTimeNs { get; set; } = 0;

    [Column("total_gen_time_ns")]
    public long TotalGenTimeNs { get; set; } = 0;

    [Column("total_load_time_ns")]
    public long TotalLoadTimeNs { get; set; } = 0;

    [Column("total_total_time_ns")]
    public long TotalTotalTimeNs { get; set; } = 0;

    // Navigation properties
    [ForeignKey("ModelId")]
    public ModelInfo? Model { get; set; }

    [ForeignKey("RoleId")]
    public Role? Role { get; set; }

    // Computed property for success rate
    [NotMapped]
    public double SuccessRate => UseCount > 0 ? (double)UseSuccessed / UseCount : 0.0;

    [NotMapped]
    public double AvgGenTps => TotalGenTimeNs > 0
        ? TotalOutputTokens / (TotalGenTimeNs / 1_000_000_000.0)
        : 0.0;

    [NotMapped]
    public double AvgPromptTps => TotalPromptTimeNs > 0
        ? TotalPromptTokens / (TotalPromptTimeNs / 1_000_000_000.0)
        : 0.0;

    [NotMapped]
    public double AvgE2eTps => TotalTotalTimeNs > 0
        ? TotalOutputTokens / (TotalTotalTimeNs / 1_000_000_000.0)
        : 0.0;

    [NotMapped]
    public double LoadRatio => TotalTotalTimeNs > 0
        ? (double)TotalLoadTimeNs / TotalTotalTimeNs
        : 0.0;
}
