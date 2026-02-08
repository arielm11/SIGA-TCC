using System.ComponentModel.DataAnnotations;

namespace TccManager.Shared.DTOs;

public class PropostaTccDto
{
    [Required(ErrorMessage = "O título é obrigatório.")]
    [StringLength(200, ErrorMessage = "O título deve ter no máximo 200 caracteres.")]
    public string Titulo { get; set; } = string.Empty;

    [Required(ErrorMessage = "O resumo é obrigatório.")]
    public string Resumo { get; set; } = string.Empty;
}