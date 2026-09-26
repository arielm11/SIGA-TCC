namespace TccManager.Shared.DTOs;

/// <summary>
/// DTO marcador devolvido pelo login (403) quando <c>Usuario.PrecisaTrocarSenha == true</c>
/// (issue #88, D8). Nenhum token é emitido junto — a barreira é estrutural, não uma flag que
/// o cliente pode ignorar. Nunca carrega e-mail, senha ou hash.
///
/// Nome deliberadamente distinto de <see cref="TrocarSenhaObrigatoriaDto"/> (o corpo do
/// endpoint que efetua a troca) — "Troca..." (substantivo, esta resposta) vs "Trocar..."
/// (verbo, aquele pedido) são fáceis de confundir; renomeado para não depender de quem lê
/// notar a diferença de uma letra.
/// </summary>
public class PrecisaTrocarSenhaResponseDto
{
    public bool PrecisaTrocarSenha { get; set; } = true;
    public string Mensagem { get; set; } = "É necessário trocar a senha antes de continuar.";
}
