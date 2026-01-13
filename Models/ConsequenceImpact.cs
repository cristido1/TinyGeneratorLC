using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("consequence_impacts")]
public class ConsequenceImpact
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("consequence_rule_id")]
    public int ConsequenceRuleId { get; set; }

    [MaxLength(200)]
    [Column("resource_name")]
    public string ResourceName { get; set; } = string.Empty;

    [Column("delta_value")]
    public int DeltaValue { get; set; }

    public ConsequenceRule? ConsequenceRule { get; set; }
}
