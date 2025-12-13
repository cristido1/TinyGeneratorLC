using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

/// <summary>
/// Rappresenta i fatti oggettivi estratti da un chunk di storia
/// </summary>
[Table("chunk_facts")]
public class ChunkFacts
{
    [Column("id")]
    public int Id { get; set; }
    
    [Column("story_id")]
    public int StoryId { get; set; }
    
    [Column("chunk_number")]
    public int ChunkNumber { get; set; }
    
    [Column("facts_json")]
    public string FactsJson { get; set; } = string.Empty;
    
    [Column("ts")]
    public string Ts { get; set; } = DateTime.UtcNow.ToString("o");
}
