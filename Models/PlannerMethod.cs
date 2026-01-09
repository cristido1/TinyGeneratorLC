using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

/// <summary>
/// Represents a narrative structure method used by planner agents.
/// Examples: Save the Cat, Story Grid, Hero's Journey, etc.
/// </summary>
[Table("planner_methods")]
public class PlannerMethod
{
    /// <summary>
    /// Primary key
    /// </summary>
    [Key]
    [Column("id")]
    public int Id { get; set; }

    /// <summary>
    /// Unique code identifier (e.g., SAVE_THE_CAT, STORY_GRID)
    /// </summary>
    [Column("code")]
    [Required]
    [MaxLength(100)]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name
    /// </summary>
    [Column("name")]
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of the planning method
    /// </summary>
    [Column("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Category: planner | validator | hybrid
    /// </summary>
    [Column("category")]
    [MaxLength(50)]
    public string? Category { get; set; }

    /// <summary>
    /// Number of beats/steps in this structure
    /// </summary>
    [Column("beat_count")]
    public int? BeatCount { get; set; }

    /// <summary>
    /// JSON schema describing the structure produced by this method
    /// </summary>
    [Column("structure_schema")]
    public string? StructureSchema { get; set; }

    /// <summary>
    /// Complete prompt for the planner agent using this method
    /// </summary>
    [Column("planner_prompt")]
    public string? PlannerPrompt { get; set; }

    /// <summary>
    /// Validation rules (textual or pseudo-code)
    /// </summary>
    [Column("validation_rules")]
    public string? ValidationRules { get; set; }

    /// <summary>
    /// Strengths of this method
    /// </summary>
    [Column("strengths")]
    public string? Strengths { get; set; }

    /// <summary>
    /// Known limitations
    /// </summary>
    [Column("weaknesses")]
    public string? Weaknesses { get; set; }

    /// <summary>
    /// Recommended genres (e.g., sci-fi, thriller, drama)
    /// </summary>
    [Column("recommended_genres")]
    public string? RecommendedGenres { get; set; }

    /// <summary>
    /// Whether this method supports series generation
    /// </summary>
    [Column("supports_series")]
    public bool SupportsSeries { get; set; }

    /// <summary>
    /// Additional notes
    /// </summary>
    [Column("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Whether this method is active and available for use
    /// </summary>
    [Column("is_active")]
    public bool IsActive { get; set; }

    /// <summary>
    /// Row version for concurrency control
    /// </summary>
    [Column("RowVersion")]
    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
