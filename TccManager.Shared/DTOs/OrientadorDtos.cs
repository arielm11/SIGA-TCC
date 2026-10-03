using TccManager.Shared.Enums;

namespace TccManager.Shared.DTOs;

public class DashboardOrientadorDto
{
    public List<TccResumoDto> OrientandosAtivos { get; set; } = new();
}

public class TccResumoDto
{
    public int Id { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public string Resumo { get; set; } = string.Empty;
    public string NomeAluno { get; set; } = string.Empty;
    public StatusTcc Status { get; set; }
    public DateTime DataCriacao { get; set; }
}

public class FeedbackDto
{
    // Issue #146 (achado D8): [Required] removido — duplicava FeedbackDtoValidator
    // (FluentValidation), que já tem NotEmpty() e sempre vence para campo vazio
    // ([ApiController] valida ModelState antes do filtro de FluentValidation).
    public string Feedback { get; set; } = string.Empty;

    public decimal? Nota { get; set; }
}

public class AcompanhamentoDto
{
    // Issue #146 (achados D8/D9): [Required] removido (duplicava
    // AcompanhamentoDtoValidator). O inicializador não-vazio (antes DateTime.Today) também
    // foi removido: com ele, ausência do campo no JSON nunca disparava nenhuma validação de
    // presença — registrava silenciosamente a reunião como "hoje". AcompanhamentoDtoValidator
    // ganhou um NotEmpty() explícito para DataReuniao (não existia nenhuma validação de
    // presença para este campo antes desta correção).
    public DateTime DataReuniao { get; set; }

    // Issue #146 (achado D8): mesmo motivo — AcompanhamentoDtoValidator já tem NotEmpty().
    public string Ata { get; set; } = string.Empty;
}