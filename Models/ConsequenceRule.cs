using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("consequence_rules")]
public class ConsequenceRule
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("narrative_profile_id")]
    public int NarrativeProfileId { get; set; }

    [MaxLength(2000)]
    [Column("description")]
    public string? Description { get; set; }

    public NarrativeProfile? NarrativeProfile { get; set; }

    public List<ConsequenceImpact> Impacts { get; set; } = new();
}
