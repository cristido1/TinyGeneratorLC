using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

/// <summary>
/// Cache persistente degli embedding dei sentimenti destinazione TTS.
/// Contiene solo i 7 sentimenti fissi (neutral, happy, sad, angry, fearful, disgusted, surprised),
/// calcolati una sola volta all'avvio e riusati.
/// </summary>
[Table("sentiment_embeddings")]
public class SentimentEmbedding
{
    /// <summary>
    /// Nome del sentimento (chiave primaria)
    /// </summary>
    [Key]
    [Column("sentiment")]
    [MaxLength(50)]
    public string Sentiment { get; set; } = string.Empty;

    /// <summary>
    /// Embedding serializzato come JSON array di float
    /// </summary>
    [Column("embedding")]
    [Required]
    public string Embedding { get; set; } = string.Empty;

    /// <summary>
    /// Modello usato per generare l'embedding (es. "nomic-embed-text:latest")
    /// </summary>
    [Column("model")]
    [MaxLength(100)]
    public string? Model { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
