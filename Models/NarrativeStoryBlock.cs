using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("narrative_story_blocks")]
public partial class NarrativeStoryBlock : ISoftDelete, IActiveFlag, IOrderable, IEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }
    [Column("story_id")]
    public int StoryId { get; set; }

    [Column("series_id")]
    public int? SeriesId { get; set; }

    [Column("episode_id")]
    public int? EpisodeId { get; set; }

    [Column("chapter_id")]
    public int? ChapterId { get; set; }

    [Column("scene_id")]
    public int? SceneId { get; set; }

    [Column("block_index")]
    public int BlockIndex { get; set; }

    [Column("text_content")]
    public string TextContent { get; set; } = string.Empty;

    [Column("continuity_state_id")]
    public int? ContinuityStateId { get; set; }

    [Column("quality_score")]
    public double? QualityScore { get; set; }

    [Column("coherence_score")]
    public double? CoherenceScore { get; set; }

    [Column("created_at")]
    public string CreatedAt { get; set; } = string.Empty;
}




