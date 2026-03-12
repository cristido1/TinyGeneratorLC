using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("Memory")]
public sealed class MemoryEntry : IEntity, IActiveFlag
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    public string Collection { get; set; } = string.Empty;

    [Required]
    public string TextValue { get; set; } = string.Empty;

    public string? Metadata { get; set; }

    [Column("model_id")]
    public int? ModelId { get; set; }

    [Column("agent_id")]
    public int? AgentId { get; set; }

    public byte[]? Embedding { get; set; }

    [Required]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [NotMapped]
    int IEntity.Id
    {
        get => int.TryParse(Id, out var n) ? n : 0;
        set => Id = value.ToString();
    }
}

[Table("Memory_new_fix")]
public sealed class MemoryNewFixEntry : IEntity, IActiveFlag
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    public string Collection { get; set; } = string.Empty;

    [Required]
    public string TextValue { get; set; } = string.Empty;

    public string? Metadata { get; set; }

    [Column("model_id")]
    public int? ModelId { get; set; }

    [Column("agent_id")]
    public int? AgentId { get; set; }

    [Required]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [NotMapped]
    public bool IsActive { get; set; } = true;

    [NotMapped]
    int IEntity.Id
    {
        get => int.TryParse(Id, out var n) ? n : 0;
        set => Id = value.ToString();
    }
}

[Table("evaluations")]
public sealed class EvaluationLegacyEntry : IEntity, IActiveFlag
{
    [Key]
    public int Id { get; set; }
    public int StoryId { get; set; }
    public int NarrativeCoherenceScore { get; set; }
    public string NarrativeCoherenceDefects { get; set; } = string.Empty;
    public int OriginalityScore { get; set; }
    public string OriginalityDefects { get; set; } = string.Empty;
    public int EmotionalImpactScore { get; set; }
    public string EmotionalImpactDefects { get; set; } = string.Empty;
    public int ActionScore { get; set; }
    public string ActionDefects { get; set; } = string.Empty;
    public double TotalScore { get; set; }
    public string Model { get; set; } = string.Empty;
    public string RawJson { get; set; } = string.Empty;
    public int? ModelId { get; set; }
    public int? AgentId { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string AgentModel { get; set; } = string.Empty;
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
    public byte[]? RowVersion { get; set; }
    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}

[Table("narrative_agent_calls_log")]
public sealed class NarrativeAgentCallLogEntry : IEntity, IActiveFlag
{
    [Key]
    [Column("id")]
    public int Id { get; set; }
    [Column("story_id")]
    public int StoryId { get; set; }
    [Column("agent_name")]
    public string AgentName { get; set; } = string.Empty;
    [Column("input_tokens")]
    public int? InputTokens { get; set; }
    [Column("output_tokens")]
    public int? OutputTokens { get; set; }
    [Column("deterministic_checks_result")]
    public string? DeterministicChecksResult { get; set; }
    [Column("response_checker_result")]
    public string? ResponseCheckerResult { get; set; }
    [Column("retry_count")]
    public int RetryCount { get; set; }
    [Column("latency_ms")]
    public int? LatencyMs { get; set; }
    [Column("created_at")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}

[Table("narrative_planning_state")]
public sealed class NarrativePlanningStateEntry : IEntity, IActiveFlag
{
    [Key]
    [Column("id")]
    public int Id { get; set; }
    [Column("series_id")]
    public int SeriesId { get; set; }
    [Column("episode_id")]
    public int? EpisodeId { get; set; }
    [Column("planning_json")]
    public string PlanningJson { get; set; } = "{}";
    [Column("created_at")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}

[Table("story_chunk_facts")]
public sealed class StoryChunkFactEntry : IEntity, IActiveFlag
{
    [Key]
    [Column("id")]
    public int Id { get; set; }
    [Column("story_id")]
    public int StoryId { get; set; }
    [Column("chunk_number")]
    public int ChunkNumber { get; set; }
    [Column("facts_json")]
    public string FactsJson { get; set; } = "{}";
    [Column("ts")]
    public string Ts { get; set; } = DateTime.UtcNow.ToString("o");
    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
