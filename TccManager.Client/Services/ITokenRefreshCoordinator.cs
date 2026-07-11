namespace TccManager.Client.Services;

/// <summary>
/// Single-flight de renovação de sessão (§6.3 da arquitetura). Garante que múltiplas
/// requisições recebendo 401 ao mesmo tempo disparem apenas uma chamada real a
/// <c>/api/auth/refresh</c> — as demais reaproveitam o token renovado pela primeira.
/// </summary>
public interface ITokenRefreshCoordinator
{
    /// <summary>
    /// Garante que a sessão esteja renovada. <paramref name="tokenUsado"/> é o access
    /// token que estava em uso quando o 401 ocorreu (ou o token expirado detectado na
    /// navegação, ver P-C1). Retorna <c>true</c> se, ao final, há um access token válido
    /// no localStorage (seja porque outra chamada concorrente já renovou, seja porque este
    /// chamador renovou agora); <c>false</c> se o refresh falhou (sessão encerrada).
    /// </summary>
    Task<bool> EnsureRefreshedAsync(string tokenUsado);
}
