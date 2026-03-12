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
    [Column("duration_seconds")]
    public double? DurationSeconds { get; set; }
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [NotMapped]
    public bool Enabled
    {
        get => IsActive;
        set => IsActive = value;
    }

    [Column("usage_count")]
    public int UsageCount { get; set; }

    [Column("usage_last")]
    public string? UsageLast { get; set; }

    [Column("score_loudness")]
    public double? ScoreLoudness { get; set; }
    [Column("score_dynamic")]
    public double? ScoreDynamic { get; set; }
    [Column("score_clipping")]
    public double? ScoreClipping { get; set; }
    [Column("score_noise")]
    public double? ScoreNoise { get; set; }
    [Column("score_duration")]
    public double? ScoreDuration { get; set; }
    [Column("score_format")]
    public double? ScoreFormat { get; set; }
    [Column("score_consistency")]
    public double? ScoreConsistency { get; set; }
    [Column("score_tag_match")]
    public double? ScoreTagMatch { get; set; }
    [Column("score_human")]
    public double? ScoreHuman { get; set; }
    [Column("score_final")]
    public double? ScoreFinal { get; set; }
    [Column("score_last_calc")]
    public string? ScoreLastCalc { get; set; }
    [Column("score_version")]
    public string? ScoreVersion { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }
}

