using System;
using System.Globalization;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

public partial class Agent : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable, INote
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    string? INote.Note
    {
        get => Notes;
        set => Notes = value;
    }

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ICreateUpdateDate.UpdatedAt
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

public partial class AppEventDefinition : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ICreateUpdateDate.UpdatedAt
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

public partial class Chapter : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class ChunkFacts : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class CoherenceScore : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class ConsequenceImpact : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class ConsequenceRule : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class FailureRule : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class GlobalCoherence : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable, INote
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

public partial class LogAnalysis : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class LogEntry : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class MappedSentiment : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => CreatedAt;
        set => CreatedAt = value ?? DateTime.UtcNow;
    }

}

public partial class MicroObjective : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class ModelInfo : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable, INote
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ICreateUpdateDate.UpdatedAt
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

public partial class ModelRole : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ICreateUpdateDate.UpdatedAt
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

public partial class ModelStatsRecord : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class ModelTestAsset : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class ModelTestRun : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable, INote
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

public partial class ModelTestStep : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class NarrativeContinuityState : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ICreateUpdateDate.UpdatedAt
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

public partial class NarrativeProfile : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class NarrativeResource : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class NarrativeStoryBlock : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

    DateTime? ICreateUpdateDate.CreatedAt
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

public partial class PlannerMethod : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable, INote
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

public partial class SentimentEmbedding : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => CreatedAt;
        set => CreatedAt = value ?? DateTime.UtcNow;
    }

}

public partial class Series : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class SeriesCharacter : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class SeriesEpisode : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class SeriesState : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

    DateTime? ICreateUpdateDate.CreatedAt
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

public partial class StepTemplate : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
{
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ICreateUpdateDate.UpdatedAt
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

public partial class StoryEvaluation : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class StoryRecord : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class StoryResourceState : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

    DateTime? ICreateUpdateDate.CreatedAt
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

public partial class StoryRuntimeState : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class StoryStatus : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class SystemReport : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

    DateTime? ICreateUpdateDate.CreatedAt
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

public partial class TaskExecution : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
{
    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ICreateUpdateDate.UpdatedAt
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

public partial class TaskExecutionStep : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class TaskTypeInfo : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class TestDefinition : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class TestPrompt : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class TipoPlanning : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class TtsVoice : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable, INote
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

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ICreateUpdateDate.UpdatedAt
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

public partial class UsageState : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
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

public partial class GenericLookupEntry : IActiveFlag, ICreateUpdateDate, IDescription, ISoftDelete, IOrderable
{
    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => ParseInterfaceDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    DateTime? ICreateUpdateDate.UpdatedAt
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


