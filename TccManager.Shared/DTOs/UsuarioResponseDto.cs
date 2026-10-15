using TccManager.Shared.Enums;
using TccManager.Shared.Models;

namespace TccManager.Shared.DTOs;

/// <summary>
/// Issue #165 (achados A1/B3): UsuarioDto também era o corpo de escrita (POST/PUT), com
/// Senha em texto puro — as 6 respostas de UsuarioController omitiam o campo manualmente na
/// construção do objeto, funcionando por convenção, não por estrutura (mesma classe de erro
/// que motivou [JsonIgnore] em Usuario.SenhaHash e a projeção de Tcc/Entrega/Acompanhamento
/// na issue #144). Este DTO cobre só a leitura; UsuarioDto continua sendo o corpo de escrita.
/// </summary>
public class UsuarioResponseDto
{
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public TipoUsuario Tipo { get; set; }
    public bool Ativo { get; set; }

    public static UsuarioResponseDto DeEntidade(Usuario usuario) => new()
    {
        Id = usuario.Id,
        Nome = usuario.Nome,
        Email = usuario.Email,
        Tipo = usuario.Tipo,
        Ativo = usuario.Ativo
    };
}
