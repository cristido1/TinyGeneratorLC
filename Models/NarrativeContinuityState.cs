using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("narrative_continuity_state")]
public class NarrativeContinuityState
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("story_id")]
    public long StoryId { get; set; }

    [Column("series_id")]
    public int? SeriesId { get; set; }

    [Column("episode_id")]
    public int? EpisodeId { get; set; }

    [Column("chapter_id")]
    public int? ChapterId { get; set; }

    [Column("scene_id")]
    public int? SceneId { get; set; }

    [Column("timeline_index")]
    public int TimelineIndex { get; set; }

    [Column("state_json")]
    public string StateJson { get; set; } = "{}";

    [Column("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [Column("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;
}
