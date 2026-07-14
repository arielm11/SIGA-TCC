using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TccManager.Shared.Models;

/// <summary>
/// Token opaco de acesso ao PDF rascunho da ata (N2 Etapa 2), emitido por par
/// (Banca, MembroExterno). Segue o espírito de <see cref="RefreshToken"/>: CSPRNG,
/// apenas o hash SHA-256 é persistido (<see cref="TokenHash"/>), com expiração e
/// suporte a revogação (ver docs/dados/2026-07-13-pdf-ata-rascunho-etapa2.md).
/// </summary>
[Table("rascunho_ata_tokens")]
public class RascunhoAtaToken
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int BancaId { get; set; }
    [ForeignKey("BancaId")]
    public Banca? Banca { get; set; }

    public int MembroExternoId { get; set; }
    [ForeignKey("MembroExternoId")]
    public MembroExterno? MembroExterno { get; set; }

    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// Snapshot de <c>Banca.DataHora</c> no momento da emissão — não autoritativo (a
    /// checagem de expiração é feita ao vivo contra <c>Banca.DataHora</c>); mantido por
    /// paridade estrutural com <see cref="RefreshToken"/> e para auditoria.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Null = token vigente. Setado no reenvio (RF-06) ou invalidação.</summary>
    public DateTime? RevokedAtUtc { get; set; }
}
