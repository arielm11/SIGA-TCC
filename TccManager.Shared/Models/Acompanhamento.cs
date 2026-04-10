using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;


namespace TccManager.Shared.Models;

public class Acompanhamento
{
    [Key]
    public int Id { get; set; }
    [Required]
    public DateTime DataReuniao { get; set; } = DateTime.Now;
    [Required]
    public string Ata { get; set; } = string.Empty;
    public int TccId { get; set; }
    [ForeignKey("TccId")]
    public Tcc? Tcc { get; set; }
}
