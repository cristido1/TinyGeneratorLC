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

    // Navigation properties
    [ForeignKey("ModelId")]
    public ModelInfo? Model { get; set; }

    [ForeignKey("RoleId")]
    public Role? Role { get; set; }

    // Computed property for success rate
    [NotMapped]
    public double SuccessRate => UseCount > 0 ? (double)UseSuccessed / UseCount : 0.0;
}
