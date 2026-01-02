using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("series_characters")]
public sealed class SeriesCharacter
{
    [Column("id")]
    [Key]
    public int Id { get; set; }

    [Column("serie_id")]
    [Required]
    public int SerieId { get; set; }

    [Column("name")]
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Column("gender")]
    [Required]
    [MaxLength(20)]
    public string Gender { get; set; } = "other";

    [Column("description")]
    public string? Description { get; set; }

    [Column("eta")]
    public string? Eta { get; set; }

    [Column("formazione")]
    public string? Formazione { get; set; }

    [Column("specializzazione")]
    public string? Specializzazione { get; set; }

    [Column("profilo")]
    public string? Profilo { get; set; }

    [Column("conflitto_interno")]
    public string? ConflittoInterno { get; set; }

    [Column("voice_id")]
    public int? VoiceId { get; set; }

    [Column("episode_in")]
    public int? EpisodeIn { get; set; }

    [Column("episode_out")]
    public int? EpisodeOut { get; set; }

    [Column("image")]
    public string? Image { get; set; }
}
