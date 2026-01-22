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
    /// Description of the planning method
    /// </summary>
    [Column("description")]
    public string? Description { get; set; }

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
