using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("name_gender")]
public sealed class NameGenderEntry : ISoftDelete, IActiveFlag, ICreateUpdateDate, IDescription, IOrderable
{
    [Column("id")]
    public long Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = string.Empty;

    [Column("gender")]
    public string Gender { get; set; } = "unknown";

    [Column("insert_date")]
    public string InsertDate { get; set; } = string.Empty;

    [Column("verified")]
    public bool Verified { get; set; }

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
