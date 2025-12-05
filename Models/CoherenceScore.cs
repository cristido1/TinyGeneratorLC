namespace TinyGenerator.Models;

/// <summary>
/// Rappresenta lo score di coerenza (locale e globale) per un chunk
/// </summary>
public class CoherenceScore
{
    public int Id { get; set; }
    public int StoryId { get; set; }
    public int ChunkNumber { get; set; }
    public double LocalCoherence { get; set; }
    public double GlobalCoherence { get; set; }
    public string? Errors { get; set; }
    public string Ts { get; set; } = DateTime.UtcNow.ToString("o");
}
