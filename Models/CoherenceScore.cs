using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

/// <summary>
/// Rappresenta lo score di coerenza (locale e globale) per un chunk
/// </summary>
[Table("coherence_scores")]
public class CoherenceScore
{
    [Column("id")]
    public int Id { get; set; }
    
    [Column("story_id")]
    public int StoryId { get; set; }
    
    [Column("chunk_number")]
    public int ChunkNumber { get; set; }
    
    [Column("local_coherence")]
    public double LocalCoherence { get; set; }
    
    [Column("global_coherence")]
    public double GlobalCoherence { get; set; }
    
    [Column("errors")]
    public string? Errors { get; set; }
    
    [Column("ts")]
    public string Ts { get; set; } = DateTime.UtcNow.ToString("o");
}
