using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TinyGenerator.Models;

[Table("tipo_planning")]
public sealed partial class TipoPlanning : ISoftDelete, IActiveFlag, IOrderable, IEntity
{
    [Key]
    [Column("id_tipo_planning")]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    [Column("codice")]
    public string Codice { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    [Column("nome")]
    public string Nome { get; set; } = string.Empty;

    [NotMapped]
    public string? Descrizione
    {
        get => Description;
        set => Description = value;
    }

    [Required]
    [MaxLength(500)]
    [Column("successione_stati")]
    public string SuccessioneStati { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;
}

