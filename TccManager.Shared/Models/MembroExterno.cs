using System.ComponentModel.DataAnnotations;

namespace TccManager.Shared.Models;

public class MembroExterno
{
    [Key]
    public int Id { get; set; }

    [Required(ErrorMessage = "O Nome é obrigatório.")]
    public string Nome { get; set; } = string.Empty;

    [Required(ErrorMessage = "O Email é obrigatório.")]
    [EmailAddress(ErrorMessage = "O Email não é válido.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "A instituição de origem é obrigatória.")]
    public string Instituicao { get; set; } = string.Empty;
}
