using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("failure_rules")]
public class FailureRule
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("narrative_profile_id")]
    public int NarrativeProfileId { get; set; }

    [MaxLength(2000)]
    [Column("description")]
    public string? Description { get; set; }

    [MaxLength(100)]
    [Column("trigger_type")]
    public string TriggerType { get; set; } = string.Empty;

    public NarrativeProfile? NarrativeProfile { get; set; }
}
