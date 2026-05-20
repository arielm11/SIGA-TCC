using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;


namespace TccManager.Shared.Models;

public class Banca
{
    [Key]
    public int Id { get; set; }
    [Required]
    public int TccId { get; set; }
    [ForeignKey("TccId")]
    public Tcc? Tcc { get; set; }
    public DateTime DataHora { get; set; }
    [Required]
    public string Local { get; set; } = string.Empty;
    [Column(TypeName = "decimal(5,2)")]
    public decimal? NotaFinal { get; set; }
    public string AtaCaminho { get; set; } = string.Empty;

    public ICollection<BancaAvaliador> Avaliadores { get; set; } = new List<BancaAvaliador>();
}
