using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("series_state")]
public sealed class SeriesState
{
    [Column("id")]
    [Key]
    public int Id { get; set; }

    [Column("serie_id")]
    [Required]
    public int SerieId { get; set; }

    [Column("is_current")]
    public bool IsCurrent { get; set; } = false;

    [Column("state_version")]
    public int StateVersion { get; set; } = 1;

    [Column("state_summary")]
    public string? StateSummary { get; set; }

    [Column("world_state_json")]
    public string? WorldStateJson { get; set; }

    [Column("open_threads_json")]
    public string? OpenThreadsJson { get; set; }

    [Column("last_major_event")]
    public string? LastMajorEvent { get; set; }

    [Column("created_at")]
    public string? CreatedAt { get; set; }

    [Column("created_by")]
    public string? CreatedBy { get; set; }

    [Column("source_episode_id")]
    public int? SourceEpisodeId { get; set; }
}
