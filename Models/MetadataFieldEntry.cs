using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("metadata_fields")]
public class MetadataFieldEntry : IEntity, IActiveFlag
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("parent_table_id")]
    public int ParentTableId { get; set; }

    [Required]
    [Column("field_name")]
    [MaxLength(128)]
    public string FieldName { get; set; } = string.Empty;

    [Column("caption")]
    [MaxLength(200)]
    public string? Caption { get; set; }

    [Column("editor_type")]
    [MaxLength(30)]
    public string? EditorType { get; set; }

    [Column("width")]
    public int? Width { get; set; }

    [Column("multiline")]
    public bool Multiline { get; set; }

    [Column("required_override")]
    public bool? RequiredOverride { get; set; }

    [Column("readonly_override")]
    public bool? ReadonlyOverride { get; set; }

    [Column("visible_override")]
    public bool? VisibleOverride { get; set; }

    [Column("sort_override")]
    public int? SortOverride { get; set; }

    [Column("group_name")]
    [MaxLength(100)]
    public string? GroupName { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}
