using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("narrative_resources")]
public class NarrativeResource
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("narrative_profile_id")]
    public int NarrativeProfileId { get; set; }

    [MaxLength(200)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("initial_value")]
    public int InitialValue { get; set; }

    [Column("min_value")]
    public int MinValue { get; set; }

    [Column("max_value")]
    public int MaxValue { get; set; }

    public NarrativeProfile? NarrativeProfile { get; set; }
}
