using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("model_roles_errors")]
public sealed partial class ModelRoleError : ICreateUpdateDate, ISoftDelete, IActiveFlag, IOrderable
{
    [Column("id")]
    public long Id { get; set; }

    [Column("parent_id")]
    public int ParentId { get; set; }

    [Column("error_text")]
    public string ErrorText { get; set; } = string.Empty;

    [Column("error_type")]
    public string ErrorType { get; set; } = string.Empty;

    [Column("error_count")]
    public int ErrorCount { get; set; } = 1;

    [Column("date_insert")]
    public string DateInsert { get; set; } = string.Empty;

    [Column("date_last")]
    public string DateLast { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("is_deleted")]
    public bool IsDeleted { get; set; } = false;

    [Column("sort_order")]
    public int SortOrder { get; set; } = 0;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
