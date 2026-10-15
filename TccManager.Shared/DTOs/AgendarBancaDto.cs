namespace TccManager.Shared.DTOs;

public class AgendarBancaDto
{
    // Issue #146 (achados D8/D9): [Required] removido — duplicava
    // AgendarBancaDtoValidator (FluentValidation sempre vence para campo ausente/vazio,
    // [ApiController] valida ModelState antes do filtro de FluentValidation, então o
    // [Required] nunca era de fato alcançado). O inicializador não-vazio (antes
    // DateTime.Now.AddDays(7)) também foi removido: com ele, ausência do campo no JSON
    // nunca disparava nenhuma validação de presença — agendava silenciosamente para "daqui a
    // 7 dias". Com o default(DateTime) (ano 0001), a regra "deve ser futura" do validator já
    // rejeita corretamente a ausência do campo.
    public DateTime DataHora { get; set; }

    // Issue #146 (achado D8): mesmo motivo — AgendarBancaDtoValidator já tem NotEmpty().
    public string Local { get; set; } = string.Empty;

    public List<int> ProfessoresIds { get; set; } = new();

    public List<int> MembrosExternosIds { get; set; } = new();
}
