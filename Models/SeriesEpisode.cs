using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("series_episodes")]
public sealed class SeriesEpisode
{
    [Column("id")]
    [Key]
    public int Id { get; set; }

    [Column("serie_id")]
    [Required]
    public int SerieId { get; set; }

    [Column("number")]
    [Required]
    public int Number { get; set; }

    [Column("title")]
    [MaxLength(200)]
    public string? Title { get; set; }

    [Column("trama")]
    public string? Trama { get; set; }

    [Column("episode_goal")]
    public string? EpisodeGoal { get; set; }

    [Column("start_situation")]
    public string? StartSituation { get; set; }

    /// <summary>
    /// Optional initial narrative phase for this episode.
    /// Allowed values (case-insensitive): AZIONE, STASI, ERRORE, EFFETTO.
    /// </summary>
    [Column("initial_phase")]
    [MaxLength(20)]
    public string? InitialPhase { get; set; }

    /// <summary>
    /// Optional tactical planning override for this episode (tipo_planning.id_tipo_planning).
    /// If null, the series default should be used.
    /// </summary>
    [Column("tipo_planning_id")]
    public int? TipoPlanningId { get; set; }
}
