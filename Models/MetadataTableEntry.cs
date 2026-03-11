using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("metadata_tables")]
public class MetadataTableEntry : IEntity, IOrderable
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("table_name")]
    [MaxLength(128)]
    public string TableName { get; set; } = string.Empty;

    [Column("default_sort_field")]
    [MaxLength(128)]
    public string? DefaultSortField { get; set; }

    [Column("default_sort_direction")]
    [MaxLength(4)]
    public string? DefaultSortDirection { get; set; }

    [Column("default_page_size")]
    public int? DefaultPageSize { get; set; }

    [Column("edit_mode")]
    [MaxLength(20)]
    public string? EditMode { get; set; }

    [Column("allow_insert")]
    public bool AllowInsert { get; set; } = true;

    [Column("allow_update")]
    public bool AllowUpdate { get; set; } = true;

    [Column("allow_delete")]
    public bool AllowDelete { get; set; } = true;

    [Column("child_table_id")]
    public int? ChildTableId { get; set; }

    [Column("child_table_parent_id_field_name")]
    [MaxLength(128)]
    public string? ChildTableParentIdFieldName { get; set; }

    [Column("title")]
    [MaxLength(256)]
    public string? Title { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("icon")]
    [MaxLength(64)]
    public string? Icon { get; set; }

    [Column("group")]
    [MaxLength(64)]
    public string? Group { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

}
