using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("story_resource_states")]
public class StoryResourceState
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("story_runtime_state_id")]
    public long StoryRuntimeStateId { get; set; }

    [MaxLength(200)]
    [Column("resource_name")]
    public string ResourceName { get; set; } = string.Empty;

    [Column("current_value")]
    public int CurrentValue { get; set; }

    public StoryRuntimeState? StoryRuntimeState { get; set; }
}
