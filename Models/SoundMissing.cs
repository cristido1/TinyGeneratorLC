using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("sounds_missing")]
public sealed class SoundMissing : IEntity, IActiveFlag
{
    public int Id { get; set; }
    public string Type { get; set; } = "fx"; // fx, amb, music
    public string Prompt { get; set; } = string.Empty;
    public string? Tags { get; set; }
    [Column("story_id")]
    public int? StoryId { get; set; }
    [Column("story_title")]
    public string? StoryTitle { get; set; }
    public string? Source { get; set; }
    public int Occurrences { get; set; }
    public string Status { get; set; } = "open"; // open, resolved, ignored
    [Column("first_seen_at")]
    public string? FirstSeenAt { get; set; }
    [Column("last_seen_at")]
    public string? LastSeenAt { get; set; }
    public string? Notes { get; set; }
    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
