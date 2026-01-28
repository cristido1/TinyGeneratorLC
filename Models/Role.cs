using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("roles")]
public class Role
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("ruolo")]
    [MaxLength(100)]
    public string Ruolo { get; set; } = string.Empty;

    [Column("comando_collegato")]
    [MaxLength(500)]
    public string? ComandoCollegato { get; set; }

    [Column("created_at")]
    public string? CreatedAt { get; set; }

    [Column("updated_at")]
    public string? UpdatedAt { get; set; }
}
