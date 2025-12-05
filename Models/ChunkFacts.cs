namespace TinyGenerator.Models;

/// <summary>
/// Rappresenta i fatti oggettivi estratti da un chunk di storia
/// </summary>
public class ChunkFacts
{
    public int Id { get; set; }
    public int StoryId { get; set; }
    public int ChunkNumber { get; set; }
    public string FactsJson { get; set; } = string.Empty;
    public string Ts { get; set; } = DateTime.UtcNow.ToString("o");
}
