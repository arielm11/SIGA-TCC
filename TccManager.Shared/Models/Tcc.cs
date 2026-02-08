using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using TccManager.Shared.Enums;

namespace TccManager.Shared.Models;

public class Tcc
{
    [Key]
    public int Id { get; set; }
    [Required]
    public string Titulo { get; set; } = string.Empty;
    [Required]
    public string Resumo { get; set; } = string.Empty;
    public DateTime DataCriacao { get; set; } = DateTime.UtcNow;
    public StatusTcc Status { get; set; } = StatusTcc.Pendente;
    public int AlunoId { get; set; }
    [ForeignKey("AlunoId")]
    public Usuario? Aluno { get; set; }
}