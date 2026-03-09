using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("images")]
public sealed class ImageAsset : IActiveFlag, ITimeStamped, IDescription, IImageFile, ISoftDelete, IOrderable, IEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }
    [Column("image_name")]
    public string ImageName { get; set; } = string.Empty;

    [Column("description")]
    public string Description { get; set; } = string.Empty;

    [Column("provenance")]
    public string Provenance { get; set; } = string.Empty;

    [Column("tags")]
    public string Tags { get; set; } = string.Empty;

    [Column("image_path")]
    public string ImagePath { get; set; } = string.Empty;

    [Column("usage_count")]
    public int UsageCount { get; set; }

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





