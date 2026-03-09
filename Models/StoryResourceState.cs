using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("story_resource_states")]
public partial class StoryResourceState : ISoftDelete, IActiveFlag, IOrderable, IEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }
    [Column("story_id")]
    public int StoryId { get; set; }

    [Column("series_id")]
    public int? SeriesId { get; set; }

    [Column("episode_number")]
    public int? EpisodeNumber { get; set; }

    [Column("chunk_index")]
    public int ChunkIndex { get; set; }

    [Column("is_initial")]
    public bool IsInitial { get; set; }

    [Column("is_final")]
    public bool IsFinal { get; set; }

    [MaxLength(50)]
    [Column("source_engine")]
    public string SourceEngine { get; set; } = "state_driven";

    [Column("canon_state_json")]
    public string CanonStateJson { get; set; } = "{}";

    [Column("created_at")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
}




