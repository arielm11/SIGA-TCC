using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace TccManager.Client.Handlers;

/// <summary>
/// Issue #171 (D4 da arquitetura) — marca "credentials: include" só nas três requisições que
/// leem e/ou gravam o cookie HttpOnly <c>siga_refresh</c>: <c>api/auth/login</c> (grava),
/// <c>api/auth/refresh</c> (lê e grava o sucessor) e <c>api/auth/logout</c> (lê e expira). Sem
/// isso, o navegador descarta silenciosamente o <c>Set-Cookie</c> do login (requisição
/// cross-origin em dev) e nunca envia o cookie de volta em refresh/logout.
///
/// Nenhuma outra requisição recebe a marcação — nem <c>trocar-senha-obrigatoria</c>, que não
/// depende do cookie — para não expor nenhum outro cookie eventual do host da API a chamadas
/// comuns (menor privilégio, RF06/D4). Registrado nos clientes nomeados "Api" e "AuthRaw".
/// </summary>
public class BrowserCredentialsHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (RotasAuth.Corresponde(request, RotasAuth.ComCookieDeRefresh))
        {
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
