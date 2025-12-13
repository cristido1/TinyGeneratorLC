using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

/// <summary>
/// Rappresenta la coerenza globale finale di una storia
/// </summary>
[Table("global_coherence")]
public class GlobalCoherence
{
    [Column("id")]
    public int Id { get; set; }
    
    [Column("story_id")]
    public int StoryId { get; set; }
    
    [Column("global_coherence_value")]
    public double GlobalCoherenceValue { get; set; }
    
    [Column("chunk_count")]
    public int ChunkCount { get; set; }
    
    [Column("notes")]
    public string? Notes { get; set; }
    
    [Column("ts")]
    public string Ts { get; set; } = DateTime.UtcNow.ToString("o");
}
