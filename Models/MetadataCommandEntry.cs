using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("metadata_commands")]
public sealed class MetadataCommandEntry : IEntity, IActiveFlag, ITimeStamped
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("command_id")]
    public int CommandId { get; set; }

    [Column("table_id")]
    public int TableId { get; set; }

    [Required]
    [MaxLength(20)]
    [Column("view_type")]
    public string ViewType { get; set; } = "grid";

    [Required]
    [MaxLength(20)]
    [Column("position")]
    public string Position { get; set; } = "row";

    [Column("visible")]
    public bool Visible { get; set; } = true;

    [Column("enabled")]
    public bool Enabled { get; set; } = true;

    [Column("requires_confirm")]
    public bool RequiresConfirm { get; set; }

    [Column("confirm_message")]
    public string? ConfirmMessage { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}

