using TccManager.Shared.Enums;

namespace TccManager.Shared.DTOs;

public class UsuarioDto
{
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Senha { get; set; } = string.Empty;

    // Issue #140 (achado M5): exigida pelo UsuarioController em autoedição (o próprio
    // usuário editando o próprio registro), quando Senha ou Email estão sendo alterados —
    // nunca exigida de um Admin editando o registro de outro usuário.
    public string? SenhaAtual { get; set; }
    public TipoUsuario Tipo { get; set; }
    public bool Ativo { get; set; }


}
