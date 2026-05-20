using System.ComponentModel.DataAnnotations;

namespace TccManager.Shared.DTOs;

public class AgendarBancaDto
{
    [Required(ErrorMessage = "A Data e Hora são obrigatórias.")]
    public DateTime DataHora { get; set;  } = DateTime.Now.AddDays(7);

    [Required(ErrorMessage = "O local ou link é obrigatório.")]
    public string Local { get; set; } = string.Empty;

    public List<int> ProfessoresIds { get; set; } = new();

    public List<int> MembrosExternosIds { get; set; } = new();
}
