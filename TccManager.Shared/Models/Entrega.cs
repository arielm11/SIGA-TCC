using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using TccManager.Shared.Enums;

namespace TccManager.Shared.Models;

public class Entrega
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Titulo { get; set; } = string.Empty;

    [Required]
    public string ArquivoCaminho { get; set; } = string.Empty;
    public DateTime DataEnvio { get; set; } = DateTime.UtcNow;
    public TipoEntrega Tipo { get; set; } = TipoEntrega.Parcial;

    // Issue #81: veredito do orientador sobre esta entrega. Não-nullable, default Pendente —
    // ver docs/dados/2026-08-30-reprovacao-durante-orientacao.md, seção 1.2. Participa do
    // predicado do índice único filtrado UX_Entregas_TccId_Final (AppDbContext).
    public StatusEntrega Status { get; set; } = StatusEntrega.Pendente;

    [MaxLength(2000)]
    public string? Feedback { get; set; }
    [Column(TypeName = "decimal(5,2)")]
    public decimal? Nota { get; set; }
    public int TccId { get; set; }
    [ForeignKey("TccId")]
    public Tcc? Tcc { get; set; }
}
