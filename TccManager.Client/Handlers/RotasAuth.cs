namespace TccManager.Client.Handlers;

/// <summary>
/// Issue #171 (docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md, §3.3) — fonte
/// única das listas de rotas de autenticação usadas tanto por <see cref="AuthTokenHandler"/>
/// (bypass do interceptor de 401) quanto por <see cref="BrowserCredentialsHandler"/>
/// (credenciais "include" para o cookie HttpOnly de refresh). Mesmo critério de casamento de
/// antes: <c>AbsolutePath</c> normalizado (<c>ToLowerInvariant</c>, sem barra nas pontas)
/// comparado por <c>EndsWith</c> — o casamento em si é insensível a maiúsculas/minúsculas (a
/// normalização cobre isso), mas o Path-match do cookie de refresh no navegador (<c>Path=/api/auth</c>
/// em Development, D2 da arquitetura) É sensível a maiúsculas/minúsculas — por isso o Client
/// sempre escreve essas rotas em minúsculas nas chamadas reais, para que os dois casamentos
/// (deste handler e do cookie) coincidam na prática.
/// </summary>
public static class RotasAuth
{
    /// <summary>
    /// As quatro rotas <c>[AllowAnonymous]</c> que não devem disparar o interceptor de refresh
    /// a partir de um 401 — nenhuma delas representa uma sessão expirada. Em particular,
    /// <c>trocar-senha-obrigatoria</c> entra aqui (issue #88): um 401 ali significa "senha
    /// atual incorreta", não "sessão expirada", e esse fluxo nunca teve sessão em jogo.
    /// </summary>
    public static readonly string[] SemInterceptorDeRefresh =
    {
        "api/auth/login",
        "api/auth/refresh",
        "api/auth/logout",
        "api/auth/trocar-senha-obrigatoria"
    };

    /// <summary>
    /// As três rotas que leem e/ou gravam o cookie HttpOnly de refresh (D4 da arquitetura) —
    /// precisam de credenciais "include" para o navegador enviar/aceitar esse cookie
    /// cross-origin em dev. <c>trocar-senha-obrigatoria</c> fica de fora: não lê nem grava o
    /// cookie (D1/7.1).
    /// </summary>
    public static readonly string[] ComCookieDeRefresh =
    {
        "api/auth/login",
        "api/auth/refresh",
        "api/auth/logout"
    };

    public static bool Corresponde(HttpRequestMessage request, IReadOnlyCollection<string> rotas)
    {
        var path = request.RequestUri?.AbsolutePath.Trim('/').ToLowerInvariant() ?? string.Empty;
        return rotas.Any(rota => path.EndsWith(rota, StringComparison.Ordinal));
    }
}
