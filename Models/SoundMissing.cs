namespace TinyGenerator.Models;

public sealed class SoundMissing
{
    public int Id { get; set; }
    public string Type { get; set; } = "fx"; // fx, amb, music
    public string Prompt { get; set; } = string.Empty;
    public string? Tags { get; set; }
    public int? StoryId { get; set; }
    public string? StoryTitle { get; set; }
    public string? Source { get; set; }
    public int Occurrences { get; set; }
    public string Status { get; set; } = "open"; // open, resolved, ignored
    public string? FirstSeenAt { get; set; }
    public string? LastSeenAt { get; set; }
    public string? Notes { get; set; }
}
