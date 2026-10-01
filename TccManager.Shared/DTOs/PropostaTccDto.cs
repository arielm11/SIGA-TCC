using System.ComponentModel.DataAnnotations;

namespace TccManager.Shared.DTOs;
public class PropostaTccDto
{
    [Required(ErrorMessage = "O título é obrigatório.")]
    public string Titulo { get; set; } = string.Empty;

    [Required(ErrorMessage = "O resumo é obrigatório.")]
    public string Resumo { get; set; } = string.Empty;

    // Issue #112 (RF01): professor de escolha do aluno, opcional (P1 do documento de produto).
    // Formato validado em PropostaTccDtoValidator (> 0 quando informado); existência/papel/ativo
    // validados no controller (validadores do projeto não consultam banco).
    public int? OrientadorSolicitadoId { get; set; }
}
