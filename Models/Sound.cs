using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("sounds")]
public sealed class Sound : ISoundFile, IActiveFlag, IOrderable, IEntity
{
    public int Id { get; set; }

    [Required]
    [MaxLength(16)]
    public string Type { get; set; } = "fx"; // fx, music, amb

    [MaxLength(255)]
    public string? Library { get; set; }

    [Required]
    [MaxLength(2048)]
    [Column("sound_path")]
    public string SoundPath { get; set; } = string.Empty;

    [Required]
    [MaxLength(512)]
    [Column("sound_name")]
    public string SoundName { get; set; } = string.Empty;

    [NotMapped]
    public string FilePath
    {
        get => SoundPath;
        set => SoundPath = value;
    }

    [NotMapped]
    public string FileName
    {
        get => SoundName;
        set => SoundName = value;
    }

    public string? Description { get; set; }
    public string? License { get; set; }

    public string? Tags { get; set; }

    public string? Embedding { get; set; }
    [Column("created_at")]
    public string? InsertDate { get; set; }
    public double? DurationSeconds { get; set; }
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [NotMapped]
    public bool Enabled
    {
        get => IsActive;
        set => IsActive = value;
    }

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

    [Column("sort_order")]
    public int SortOrder { get; set; }
}

