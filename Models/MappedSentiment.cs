using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

/// <summary>
/// Mappa i sentimenti liberi usati dallo scrittore ai sentimenti
/// supportati dal TTS (neutral, happy, sad, angry, fearful, disgusted, surprised).
/// Funge da cache per evitare ricalcoli.
/// </summary>
[Table("mapped_sentiments")]
public class MappedSentiment
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    /// <summary>
    /// Sentimento originale usato dallo scrittore (es. "furioso", "terrorizzato", "euforico")
    /// </summary>
    [Column("source_sentiment")]
    [Required]
    [MaxLength(100)]
    public string SourceSentiment { get; set; } = string.Empty;

    /// <summary>
    /// Sentimento destinazione supportato dal TTS.
    /// Valori ammessi: neutral, happy, sad, angry, fearful, disgusted, surprised
    /// </summary>
    [Column("dest_sentiment")]
    [Required]
    [MaxLength(50)]
    public string DestSentiment { get; set; } = string.Empty;

    /// <summary>
    /// Score di confidenza della mappatura (0-1)
    /// </summary>
    [Column("confidence")]
    public float? Confidence { get; set; }

    /// <summary>
    /// Come è stata determinata la mappatura: "direct", "seed", "embedding", "agent", "default"
    /// </summary>
    [Column("source_type")]
    [MaxLength(20)]
    public string? SourceType { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
