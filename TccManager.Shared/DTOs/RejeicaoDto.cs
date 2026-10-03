namespace TccManager.Shared.DTOs;

// Issue #81 (D5): movido de CoordenadorDtos.cs para arquivo próprio — deixou de ser
// exclusivo do fluxo de rejeição de proposta do Coordenador, agora também é o corpo de
// POST api/orientador/entregas/{id}/rejeitar. Mesmo namespace: nenhum using muda.
public class RejeicaoDto
{
    // Issue #146 (achado D8): [Required] removido — duplicava RejeicaoDtoValidator
    // (FluentValidation), que já tem NotEmpty() e sempre vence para campo vazio
    // ([ApiController] valida ModelState antes do filtro de FluentValidation).
    public string Motivo { get; set; } = string.Empty;
}
