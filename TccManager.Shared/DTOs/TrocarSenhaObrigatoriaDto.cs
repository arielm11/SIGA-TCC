namespace TccManager.Shared.DTOs;

/// <summary>
/// Corpo de <c>POST /api/auth/trocar-senha-obrigatoria</c> (issue #88, D9). Exige a senha
/// ATUAL (temporária, de bootstrap) para autenticar a troca — não é um "esqueci minha senha".
/// </summary>
public class TrocarSenhaObrigatoriaDto
{
    public string Email { get; set; } = string.Empty;
    public string SenhaAtual { get; set; } = string.Empty;
    public string NovaSenha { get; set; } = string.Empty;
}
