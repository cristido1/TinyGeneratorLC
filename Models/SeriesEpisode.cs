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
}
