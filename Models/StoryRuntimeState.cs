using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("story_runtime_states")]
public class StoryRuntimeState
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("story_id")]
    public long StoryId { get; set; }

    [Column("narrative_profile_id")]
    public int NarrativeProfileId { get; set; }

    [Column("current_chunk_index")]
    public int CurrentChunkIndex { get; set; }

    [MaxLength(100)]
    [Column("current_phase")]
    public string? CurrentPhase { get; set; }

    [MaxLength(100)]
    [Column("current_pov")]
    public string? CurrentPOV { get; set; }

    [Column("failure_count")]
    public int FailureCount { get; set; }

    [MaxLength(4000)]
    [Column("last_context")]
    public string? LastContext { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; }

    public StoryRecord? Story { get; set; }
    public NarrativeProfile? NarrativeProfile { get; set; }
}
