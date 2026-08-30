using System.ComponentModel.DataAnnotations;

namespace TccManager.Shared.DTOs;

// Issue #81 (D5): movido de CoordenadorDtos.cs para arquivo próprio — deixou de ser
// exclusivo do fluxo de rejeição de proposta do Coordenador, agora também é o corpo de
// POST api/orientador/entregas/{id}/rejeitar. Mesmo namespace: nenhum using muda.
public class RejeicaoDto
{
    [Required(ErrorMessage = "O motivo da rejeição é obrigatório!")]
    public string Motivo { get; set; } = string.Empty;
}
