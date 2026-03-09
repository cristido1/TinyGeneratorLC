using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("series_characters")]
public sealed partial class SeriesCharacter : ISoftDelete, IActiveFlag, IOrderable, IEntity
{
    [Column("id")]
    [Key]
    public int Id { get; set; }

    [Column("serie_id")]
    [Required]
    public int SerieId { get; set; }

    [Column("description")]
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Column("gender")]
    [Required]
    [MaxLength(20)]
    public string Gender { get; set; } = "other";

    [NotMapped]
    public string? Description
    {
        get => Name;
        set => Name = value ?? string.Empty;
    }

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

    [Column("ruolo_narrativo")]
    public string? RuoloNarrativo { get; set; }

    [Column("arco_personale")]
    public string? ArcoPersonale { get; set; }

    [Column("stato_attuale")]
    public string? StatoAttuale { get; set; }

    [Column("stato_attuale_json")]
    public string? StatoAttualeJson { get; set; }

    [Column("alleanza_relazione")]
    public string? AlleanzaRelazione { get; set; }

    [Column("last_seen_episode_number")]
    public int? LastSeenEpisodeNumber { get; set; }

    [Column("voice_id")]
    public int? VoiceId { get; set; }

    [Column("episode_in")]
    public int? EpisodeIn { get; set; }

    [Column("episode_out")]
    public int? EpisodeOut { get; set; }

    [Column("image")]
    public string? Image { get; set; }

    [Column("aspect")]
    public string? Aspect { get; set; }
}

