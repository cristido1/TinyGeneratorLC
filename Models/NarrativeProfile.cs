using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("narrative_profiles")]
public class NarrativeProfile
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    [Column("description")]
    public string? Description { get; set; }

    [Column("base_system_prompt")]
    public string? BaseSystemPrompt { get; set; }

    [Column("style_prompt")]
    public string? StylePrompt { get; set; }

    // JSON array of POV strings (e.g. ["ThirdPersonLimited", "FirstPerson"]).
    // Stored as a single column to avoid introducing new entities/tables.
    [Column("pov_list_json")]
    public string? PovListJson { get; set; }

    public List<NarrativeResource> Resources { get; set; } = new();
    public List<MicroObjective> MicroObjectives { get; set; } = new();
    public List<FailureRule> FailureRules { get; set; } = new();
    public List<ConsequenceRule> ConsequenceRules { get; set; } = new();
}
