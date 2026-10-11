using Microsoft.AspNetCore.Mvc.Testing;

namespace TccManager.Tests.Fixtures;

/// <summary>
/// Issue #171 (migração do refresh token para cookie HttpOnly — ver
/// docs/arquitetura/2026-10-10-refresh-token-cookie-httponly.md, seção 9). Infraestrutura comum
/// aos testes que precisam inspecionar/controlar precisamente o cookie <c>siga_refresh</c>:
///
/// <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> já devolve um cliente com
/// cookies ligados por padrão (<c>HandleCookies = true</c>) — suficiente para fluxos lineares
/// (login → refresh → refresh). Cenários que precisam reapresentar um valor ESPECÍFICO (ex.: o
/// token A já rotacionado, enquanto o "jar" automático teria o sucessor B — a janela de graça da
/// issue #85, ou a sessão 1 depois de um segundo login) exigem desligar o jar automático e
/// montar o header <c>Cookie</c> manualmente, lendo o valor do <c>Set-Cookie</c> da resposta
/// anterior. Por isso os testes desta suíte usam <see cref="ClienteSemCookies"/> de forma
/// uniforme — determinístico em todos os casos, não só nos de corrida.
/// </summary>
public static class CookieTestHelpers
{
    /// <summary>Nome do cookie em Development (<c>TccApiFactory</c>, ambiente padrão dos testes).</summary>
    public const string NomeCookieRefresh = "siga_refresh";

    /// <summary>
    /// Nome do cookie fora de Development (achado L3 — <c>ProductionApiFactory</c>): prefixo
    /// <c>__Host-</c>, exigido pela especificação junto de <c>Secure=true</c>/<c>Path=/</c>.
    /// </summary>
    public const string NomeCookieRefreshForaDeDevelopment = "__Host-" + NomeCookieRefresh;

    public static HttpClient ClienteSemCookies(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>
    /// Extrai o valor bruto do cookie de refresh do Set-Cookie da resposta, se houver — aceita
    /// tanto o nome de Development ("siga_refresh") quanto o de fora de Development
    /// ("__Host-siga_refresh"), para funcionar com qualquer factory sem o chamador precisar saber
    /// o ambiente.
    /// </summary>
    public static string? ExtrairValorCookieRefresh(HttpResponseMessage resposta)
    {
        if (ExtrairSetCookie(resposta) is not { } setCookie)
            return null;

        var par = setCookie.Split(';')[0];
        var separador = par.IndexOf('=');
        return separador < 0 ? null : Uri.UnescapeDataString(par[(separador + 1)..]);
    }

    /// <summary>
    /// Extrai o valor de um atributo do Set-Cookie de "siga_refresh" (ex.: "Path", "Max-Age").
    /// Para atributos booleanos sem valor (ex.: "HttpOnly", "Secure"), devolve string vazia
    /// quando presente; <c>null</c> quando o atributo não está no cabeçalho.
    /// </summary>
    public static string? ExtrairAtributoCookieRefresh(HttpResponseMessage resposta, string atributo)
    {
        var setCookie = ExtrairSetCookie(resposta);
        if (setCookie == null)
            return null;

        foreach (var parteBruta in setCookie.Split(';'))
        {
            var parte = parteBruta.Trim();

            if (parte.Equals(atributo, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            if (parte.StartsWith($"{atributo}=", StringComparison.OrdinalIgnoreCase))
                return parte[(atributo.Length + 1)..];
        }

        return null;
    }

    /// <summary>
    /// O Set-Cookie inteiro do cookie de refresh na resposta, ou <c>null</c> se ausente. Casa com
    /// o nome de Development ("siga_refresh=") ou o de fora de Development
    /// ("__Host-siga_refresh=") — nunca os dois ao mesmo tempo numa resposta real.
    /// </summary>
    public static string? ExtrairSetCookie(HttpResponseMessage resposta)
    {
        if (!resposta.Headers.TryGetValues("Set-Cookie", out var valores))
            return null;

        return valores.FirstOrDefault(v =>
            v.StartsWith($"{NomeCookieRefresh}=", StringComparison.Ordinal) ||
            v.StartsWith($"{NomeCookieRefreshForaDeDevelopment}=", StringComparison.Ordinal));
    }

    /// <param name="nomeCookie">
    /// Nome do cookie a enviar no header <c>Cookie</c>. Default = nome de Development
    /// ("siga_refresh"); testes contra <c>ProductionApiFactory</c> devem passar
    /// <see cref="NomeCookieRefreshForaDeDevelopment"/> explicitamente — o servidor só lê o cookie
    /// pelo nome correspondente ao ambiente (<c>RefreshTokenCookie.NomeCookie</c>, achado L3).
    /// </param>
    public static HttpRequestMessage RequisicaoComCookieRefresh(
        HttpMethod metodo, string rota, string? valorCookie, string nomeCookie = NomeCookieRefresh)
    {
        var requisicao = new HttpRequestMessage(metodo, rota);

        if (valorCookie != null)
            requisicao.Headers.Add("Cookie", $"{nomeCookie}={Uri.EscapeDataString(valorCookie)}");

        return requisicao;
    }
}
