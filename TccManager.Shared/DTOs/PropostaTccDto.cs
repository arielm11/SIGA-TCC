namespace TccManager.Shared.DTOs;
public class PropostaTccDto
{
    // Issue #146 (achado D8): [Required] removido — mesmo motivo já aplicado a
    // [StringLength] nestes dois campos (ver PropostaTccDtoValidator): duplicava
    // PropostaTccDtoValidator (FluentValidation), que já tem NotEmpty() e sempre vence para
    // campo vazio ([ApiController] valida ModelState antes do filtro de FluentValidation).
    public string Titulo { get; set; } = string.Empty;

    public string Resumo { get; set; } = string.Empty;

    // Issue #112 (RF01): professor de escolha do aluno, opcional (P1 do documento de produto).
    // Formato validado em PropostaTccDtoValidator (> 0 quando informado); existência/papel/ativo
    // validados no controller (validadores do projeto não consultam banco).
    public int? OrientadorSolicitadoId { get; set; }
}
