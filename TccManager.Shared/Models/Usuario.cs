using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TccManager.Shared.Models;

[Table("usuarios")]
public class Usuario
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public string Nome { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string SenhaHash { get; set; } = string.Empty;

    public bool Admin { get; set; }

    public bool Ativo { get; set; }
}