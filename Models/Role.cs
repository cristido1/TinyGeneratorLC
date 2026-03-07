using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;

namespace TinyGenerator.Models;

[Table("roles")]
public partial class Role : IKeyAndDescription, ICreateUpdateDate, IDescription, ISoftDelete, IActiveFlag, IOrderable
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Column("linked_command")]
    [MaxLength(500)]
    public string? LinkedCommand { get; set; }

    [Column("created_at")]
    public string? CreatedAt { get; set; }

    [Column("updated_at")]
    public string? UpdatedAt { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [NotMapped]
    public string Description
    {
        get => Name;
        set => Name = value;
    }

    DateTime? ICreateUpdateDate.CreatedAt
    {
        get => ParseIsoDate(CreatedAt);
        set => CreatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    DateTime? ICreateUpdateDate.UpdatedAt
    {
        get => ParseIsoDate(UpdatedAt);
        set => UpdatedAt = value?.ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateTime? ParseIsoDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}
