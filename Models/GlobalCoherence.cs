namespace TinyGenerator.Models;

/// <summary>
/// Rappresenta la coerenza globale finale di una storia
/// </summary>
public class GlobalCoherence
{
    public int Id { get; set; }
    public int StoryId { get; set; }
    public double GlobalCoherenceValue { get; set; }
    public int ChunkCount { get; set; }
    public string? Notes { get; set; }
    public string Ts { get; set; } = DateTime.UtcNow.ToString("o");
}
