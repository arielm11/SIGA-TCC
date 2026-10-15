using System.Net;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using TccManager.Client.Handlers;
using Xunit;

namespace TccManager.Tests.Client.Handlers;

/// <summary>
/// Issue #171 (D4 da arquitetura) — <see cref="BrowserCredentialsHandler"/> marca
/// "credentials: include" (necessário para o navegador enviar/aceitar o cookie HttpOnly de
/// refresh cross-origin, em dev) só nas três rotas que leem/gravam o cookie: login, refresh,
/// logout. Nenhuma outra requisição recebe a marcação — inclusive
/// <c>trocar-senha-obrigatoria</c>, que é <c>[AllowAnonymous]</c> mas não depende do cookie.
///
/// <see cref="HttpRequestMessage.SetBrowserRequestCredentials"/> grava a marcação em
/// <c>request.Options["WebAssemblyFetchOptions"]</c> (um <c>IDictionary&lt;string, object&gt;</c>),
/// com a chave <c>"credentials"</c> = <c>"include"</c> — confirmado por reflexão sobre
/// <c>Microsoft.AspNetCore.Components.WebAssembly</c> 9.0.4 (a versão referenciada pelo
/// projeto), já que a API pública não documenta o formato interno de armazenamento (P4 da
/// arquitetura).
/// </summary>
public class BrowserCredentialsHandlerTests
{
    private const string ApiBaseUrl = "https://localhost:7188/";

    private sealed class HandlerEspiao : HttpMessageHandler
    {
        public HttpRequestMessage? UltimaRequisicao { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UltimaRequisicao = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient Client, HandlerEspiao Espiao) Montar()
    {
        var espiao = new HandlerEspiao();
        var handler = new BrowserCredentialsHandler { InnerHandler = espiao };
        var client = new HttpClient(handler) { BaseAddress = new Uri(ApiBaseUrl) };
        return (client, espiao);
    }

    private static bool MarcadaComCredenciaisInclude(HttpRequestMessage request)
    {
        if (!request.Options.TryGetValue(
                new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"),
                out var opcoes))
        {
            return false;
        }

        return opcoes.TryGetValue("credentials", out var valor) && Equals(valor, "include");
    }

    [Theory]
    [InlineData("api/auth/login")]
    [InlineData("api/auth/refresh")]
    [InlineData("api/auth/logout")]
    public async Task RotasQueLeemOuGravamOCookieDeRefresh_SaoMarcadasComCredenciaisInclude(string rota)
    {
        var (client, espiao) = Montar();

        await client.PostAsync(rota, null);

        Assert.True(MarcadaComCredenciaisInclude(espiao.UltimaRequisicao!));
    }

    [Theory]
    [InlineData("api/auth/trocar-senha-obrigatoria")]
    [InlineData("api/tcc")]
    [InlineData("api/usuarios/me")]
    public async Task NenhumaOutraRota_EMarcada(string rota)
    {
        var (client, espiao) = Montar();

        await client.GetAsync(rota);

        Assert.False(MarcadaComCredenciaisInclude(espiao.UltimaRequisicao!));
    }
}
