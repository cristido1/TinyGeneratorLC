using System;
using System.Globalization;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

public partial class Agent : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable, INote
{
    

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    string? INote.Note
    {
        get => Notes;
        set => Notes = value;
    }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ITimeStamped.UpdatedAt
    {
        get => ParseInterfaceDate(UpdatedAt);
        set => UpdatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class AppEventDefinition : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ITimeStamped.UpdatedAt
    {
        get => ParseInterfaceDate(UpdatedAt);
        set => UpdatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class Chapter : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class ChunkFacts : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class CoherenceScore : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class ConsequenceImpact : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class ConsequenceRule : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class FailureRule : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class GlobalCoherence : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable, INote
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    string? INote.Note
    {
        get => Notes;
        set => Notes = value;
    }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class LogAnalysis : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class LogEntry : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class MappedSentiment : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => CreatedAt;
        set => CreatedAt = value ?? DateTime.UtcNow;
    }

}

public partial class MicroObjective : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class ModelInfo : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable, INote
{
    [NotMapped]
    public string? Description
    {
        get => Name;
        set => Name = value ?? string.Empty;
    }
    

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ITimeStamped.UpdatedAt
    {
        get => ParseInterfaceDate(UpdatedAt);
        set => UpdatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class ModelRole : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

}

public partial class ModelStatsRecord : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class ModelTestAsset : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class ModelTestRun : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable, INote
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    string? INote.Note
    {
        get => Notes;
        set => Notes = value;
    }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class ModelTestStep : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class NarrativeContinuityState : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ITimeStamped.UpdatedAt
    {
        get => ParseInterfaceDate(UpdatedAt);
        set => UpdatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class NarrativeProfile : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class NarrativeResource : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [NotMapped]
    public string? Description
    {
        get => Name;
        set => Name = value ?? string.Empty;
    }
    

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class NarrativeStoryBlock : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class PlannerMethod : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable, INote
{
    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    string? INote.Note
    {
        get => Notes;
        set => Notes = value;
    }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class SentimentEmbedding : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => CreatedAt;
        set => CreatedAt = value ?? DateTime.UtcNow;
    }

}

public partial class Series : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class SeriesCharacter : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class SeriesEpisode : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class SeriesState : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class StepTemplate : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ITimeStamped.UpdatedAt
    {
        get => ParseInterfaceDate(UpdatedAt);
        set => UpdatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class StoryEvaluation : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class StoryRecord : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class StoryResourceState : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class StoryRuntimeState : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class StoryStatus : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class SystemReport : IActiveFlag, ITimeStamped, IDescription, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class TaskExecution : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ITimeStamped.UpdatedAt
    {
        get => ParseInterfaceDate(UpdatedAt);
        set => UpdatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class TaskExecutionStep : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class TaskTypeInfo : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class TestDefinition : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class TestPrompt : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class TipoPlanning : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class TtsVoice : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable, INote
{
    [NotMapped]
    public string? Description
    {
        get => Name;
        set => Name = value ?? string.Empty;
    }
    

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    string? INote.Note
    {
        get => Notes;
        set => Notes = value;
    }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ITimeStamped.UpdatedAt
    {
        get => ParseInterfaceDate(UpdatedAt);
        set => UpdatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }

}

public partial class UsageState : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

}

public partial class GenericLookupEntry : IActiveFlag, ITimeStamped, IDescription, ISoftDelete, IOrderable
{
    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    DateTime? ITimeStamped.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    DateTime? ITimeStamped.UpdatedAt
    {
        get => ParseInterfaceDate(UpdatedAt);
        set => UpdatedAt = value?.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static DateTime? ParseInterfaceDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return parsed;
        if (DateTime.TryParse(value, out parsed)) return parsed;
        return null;
    }
}


