using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;


namespace TccManager.Shared.Models;

public class BancaAvaliador
{
    [Key]
    public int Id { get; set; }

    public int BancaId { get; set; }
    [ForeignKey("BancaId")]
    public Banca? Banca { get; set; }

    // Avaliador Interno
    public int? ProfessorId { get; set; }
    [ForeignKey("ProfessorId")]
    public Usuario? Professor { get; set; }

    // Avaliador Externo
    public int? MembroExternoId { get; set; }
    [ForeignKey("MembroExternoId")]
    public MembroExterno? MembroExterno { get; set; }
}
