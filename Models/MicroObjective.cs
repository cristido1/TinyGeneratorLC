using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("micro_objectives")]
public class MicroObjective
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("narrative_profile_id")]
    public int NarrativeProfileId { get; set; }

    [MaxLength(50)]
    [Column("code")]
    public string Code { get; set; } = string.Empty;

    [MaxLength(2000)]
    [Column("description")]
    public string? Description { get; set; }

    [Column("difficulty")]
    public int Difficulty { get; set; }

    public NarrativeProfile? NarrativeProfile { get; set; }
}
