using System.ComponentModel.DataAnnotations;
using TccManager.Shared.Enums;

namespace TccManager.Shared.DTOs;

public class DashboardOrientadorDto
{
    public List<TccResumoDto> PropostasPendentes { get; set; } = new();
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

public class RejeicaoDto 
{
    [Required(ErrorMessage = "O motivo da rejeição é obrigatório!")]
    public string Motivo { get; set; } = string.Empty;
}

public class FeedbackDto
{
    [Required(ErrorMessage = "O feedback é obrigatório!")]
    public string Feedback { get; set; } = string.Empty;

    public decimal? Nota { get; set; }
}

public class AcompanhamentoDto
{
    [Required(ErrorMessage = "A data da reunião é obrigatória!")]
    public DateTime DataReuniao { get; set; } = DateTime.Today;

    [Required(ErrorMessage = "O registro da ata é obrigatória!")]
    public string Ata { get; set; } = string.Empty;
}