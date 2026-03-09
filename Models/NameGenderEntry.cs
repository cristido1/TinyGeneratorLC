using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("name_gender")]
public sealed class NameGenderEntry : ISoftDelete, IActiveFlag, ITimeStamped, IDescription, IOrderable, IEntity
{
    [Column("id")]
    public int Id { get; set; }
    [Column("description")]
    public string Name { get; set; } = string.Empty;

    [Column("gender")]
    public string Gender { get; set; } = "unknown";

    [Column("verified")]
    public bool Verified { get; set; }

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





