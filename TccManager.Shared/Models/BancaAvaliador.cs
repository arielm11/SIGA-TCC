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

    public int ProfessorId { get; set; }
    [ForeignKey("ProfessorId")]
    public Usuario? Professor { get; set; }
}
