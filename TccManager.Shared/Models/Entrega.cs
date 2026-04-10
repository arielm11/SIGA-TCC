using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using TccManager.Shared.Enums;

namespace TccManager.Shared.Models;

public class Entrega
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string Titulo { get; set; } = string.Empty;

    [Required]
    public string ArquivoCaminho { get; set; } = string.Empty;
    public DateTime DataEnvio { get; set; } = DateTime.Now;
    public TipoEntrega Tipo { get; set; } = TipoEntrega.Parcial;
    public string? Feedback { get; set; }
    [Column(TypeName = "decimal(5,2)")]
    public decimal? Nota { get; set; }
    public int TccId { get; set; }
    [ForeignKey("TccId")]
    public Tcc? Tcc { get; set; }
}
