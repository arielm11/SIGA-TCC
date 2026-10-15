using System.Net;
using System.Net.Http.Json;
using TccManager.Shared.DTOs;
using TccManager.Tests.Fixtures;
using Xunit;

namespace TccManager.Tests.Configuration;

/// <summary>
/// Issue #60 — política de rate limiting "refresh", criada para <c>/api/auth/refresh</c>.
/// Antes desta correção o endpoint compartilhava a política "login" (5 req/janela), o que
/// fazia a renovação silenciosa do cliente competir pelo mesmo orçamento das tentativas de
/// login e, na prática, derrubar sessões legítimas.
///
/// Issue #171: o refresh token passou a viajar via cookie HttpOnly ("siga_refresh"), não mais
/// no corpo — as chamadas de /refresh e /logout aqui enviam um cookie inválido/ausente (em vez
/// de <c>RefreshRequestDto</c>/<c>LogoutRequestDto</c>, removidos). As asserções de limite não
/// mudam: o rate limiting acontece antes do controller tentar ler o cookie.
///
/// Valores esperados vêm do appsettings.json versionado da API
/// (<c>RateLimiting:Refresh</c> = 15 req / 60 s / QueueLimit 0), no mesmo padrão já adotado
/// por <see cref="RateLimitingLoginTests"/>.
///
/// Isolamento: cada teste cria sua própria <see cref="TccApiFactory"/>, portanto sua própria
/// instância do PartitionedRateLimiter — a contagem não vaza entre casos.
/// </summary>
public class RateLimitingRefreshTests
{
    private const string RotaLogin = "/api/auth/login";
    private const string RotaRefresh = "/api/auth/refresh";
    private const string RotaLogout = "/api/auth/logout";

    private const int LimiteRefresh = 15;
    private const int LimiteLogin = 5;

    private static Task<HttpResponseMessage> ChamarRefreshComCookieInvalido(HttpClient client) =>
        client.SendAsync(CookieTestHelpers.RequisicaoComCookieRefresh(
            HttpMethod.Post, RotaRefresh, Guid.NewGuid().ToString()));

    private static Task<HttpResponseMessage> ChamarLogoutComCookieInvalido(HttpClient client) =>
        client.SendAsync(CookieTestHelpers.RequisicaoComCookieRefresh(
            HttpMethod.Post, RotaLogout, Guid.NewGuid().ToString()));

    private static LoginDto CredencialInvalida() =>
        new() { Email = "naoexiste@teste.com", Senha = "errada" };

    [Fact]
    public async Task DentroDoLimite_QuinzeChamadas_NenhumaEhBloqueada()
    {
        // No código anterior (política "login" compartilhada) a 6ª chamada já viria 429.
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        for (var i = 1; i <= LimiteRefresh; i++)
        {
            var resp = await ChamarRefreshComCookieInvalido(client);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    [Fact]
    public async Task DecimaSextaChamadaDentroDaJanela_Retorna429ComRetryAfter()
    {
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        for (var i = 1; i <= LimiteRefresh; i++)
        {
            var permitida = await ChamarRefreshComCookieInvalido(client);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, permitida.StatusCode);
        }

        var bloqueada = await ChamarRefreshComCookieInvalido(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueada.StatusCode);
        Assert.True(bloqueada.Headers.Contains("Retry-After"),
            "A resposta 429 do /refresh deve conter o header Retry-After, como a de /login.");

        var retryAfter = bloqueada.Headers.GetValues("Retry-After").First();
        Assert.True(int.TryParse(retryAfter, out var segundos),
            $"Retry-After deveria ser um inteiro em segundos, mas foi '{retryAfter}'.");
        Assert.InRange(segundos, 1, 60);
    }

    [Fact]
    public async Task LimiteDeLoginEsgotado_NaoBloqueiaORefresh()
    {
        // Núcleo da correção: as duas rotas passaram a ter orçamentos independentes.
        // No código anterior, esgotar o /login também esgotava o /refresh.
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        for (var i = 1; i <= LimiteLogin; i++)
        {
            await client.PostAsJsonAsync(RotaLogin, CredencialInvalida());
        }

        var loginBloqueado = await client.PostAsJsonAsync(RotaLogin, CredencialInvalida());
        Assert.Equal(HttpStatusCode.TooManyRequests, loginBloqueado.StatusCode);

        var refresh = await ChamarRefreshComCookieInvalido(client);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task LimiteDeRefreshEsgotado_NaoBloqueiaOLogin()
    {
        // Recíproca: o inverso também precisa valer, senão um cliente com renovação em
        // loop impediria o usuário de refazer login manualmente.
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        for (var i = 1; i <= LimiteRefresh; i++)
        {
            await ChamarRefreshComCookieInvalido(client);
        }

        var refreshBloqueado = await ChamarRefreshComCookieInvalido(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, refreshBloqueado.StatusCode);

        var login = await client.PostAsJsonAsync(RotaLogin, CredencialInvalida());
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Logout_NaoCompartilhaMaisOOrcamentoDoLogin()
    {
        // Issue #142 (achado M2): /logout ganhou política própria ("logout", 15 req/60s) —
        // antes compartilhava "login" (5 req/60s), o que fazia um atacante martelando /login
        // conseguir bloquear o logout de usuários legítimos (e vice-versa). Substitui o teste
        // anterior, que travava deliberadamente o comportamento antigo.
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        for (var i = 1; i <= LimiteLogin; i++)
        {
            await client.PostAsJsonAsync(RotaLogin, CredencialInvalida());
        }

        var loginBloqueado = await client.PostAsJsonAsync(RotaLogin, CredencialInvalida());
        Assert.Equal(HttpStatusCode.TooManyRequests, loginBloqueado.StatusCode);

        var logout = await ChamarLogoutComCookieInvalido(client);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, logout.StatusCode);
    }

    [Fact]
    public async Task Logout_AcimaDoProprioLimite_Retorna429ComRetryAfter()
    {
        const int limiteLogout = 15;
        var factory = new TccApiFactory();
        var client = CookieTestHelpers.ClienteSemCookies(factory);

        for (var i = 1; i <= limiteLogout; i++)
        {
            var resposta = await ChamarLogoutComCookieInvalido(client);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, resposta.StatusCode);
        }

        var bloqueado = await ChamarLogoutComCookieInvalido(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, bloqueado.StatusCode);
        Assert.True(bloqueado.Headers.Contains("Retry-After"));
    }
}
