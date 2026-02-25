using System.ComponentModel.DataAnnotations;

namespace TinyGenerator.Models;

public sealed class Sound
{
    public int Id { get; set; }

    [Required]
    [MaxLength(16)]
    public string Type { get; set; } = "fx"; // fx, music, amb

    [MaxLength(255)]
    public string? Library { get; set; }

    [Required]
    [MaxLength(2048)]
    public string FilePath { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    public string FileName { get; set; } = string.Empty;

    public string? Description { get; set; }
    public string? License { get; set; }

    public string? Tags { get; set; }

    public string? Embedding { get; set; }
    public string? InsertDate { get; set; }
    public double? DurationSeconds { get; set; }
    public bool Enabled { get; set; } = true;

    public int UsageCount { get; set; }

    public string? UsageLast { get; set; }

    public double? ScoreLoudness { get; set; }
    public double? ScoreDynamic { get; set; }
    public double? ScoreClipping { get; set; }
    public double? ScoreNoise { get; set; }
    public double? ScoreDuration { get; set; }
    public double? ScoreFormat { get; set; }
    public double? ScoreConsistency { get; set; }
    public double? ScoreTagMatch { get; set; }
    public double? ScoreHuman { get; set; }
    public double? ScoreFinal { get; set; }
    public string? ScoreLastCalc { get; set; }
    public string? ScoreVersion { get; set; }
}
