namespace TccManager.Client.Services;

/// <summary>
/// Fluxo de encerramento de sessão (RF07/RF08 — §6.5 da arquitetura): limpa o
/// localStorage, notifica o estado de autenticação como anônimo e redireciona para
/// <c>/login?expirado=1</c>, onde a tela exibe a mensagem de sessão expirada.
/// </summary>
public interface ISessionEndedHandler
{
    Task EncerrarSessaoAsync();
}
